using System.Text.RegularExpressions;
using OutSystems.Model;

namespace OmlCli;

class AmbiguousPathError(string message) : ModelError(message);

// Element paths, the one syntax that describe, tree, edits, copy, folders and tidyFlows read:
//   Name          a name; from the module: this module's own top-level element, then a referenced one, then a nested one
//   A/B/C         each part searched breadth-first under the one before (B need not be a direct child of A)
//   Kind:Name     only elements of that kind (Screen:Orders, Entity:Orders, Flow:Admin)
//   Module.Name   first part only: an element of that module (this module or a reference)
//   Kind[i]       the i-th child of that kind, as tree prints unnamed parts
// Directly under an action, a name that an input or output parameter and a flow node both carry means the parameter
// when it ends the path (`Pick/Response`); `Pick/Assign:Response` is the node. Before another part, the rest of the path chooses.
// Each part takes the first group of candidates that has a match (own top-level, referenced, then each breadth-first
// level). Within that group, the rest of the path picks; it never falls through to a later group.
static class ElementPaths
{
    public static string Kind(object o) => FieldMappings.Is(o) ? FieldMappings.Kind : Reflect.Get(o, "CustomWidgetDescriptor") is { } descriptor
        ? descriptor.GetType().Name.Replace("Descriptor", "") : o.GetType().Name;

    // Children in an order that is the same on every load. The model's Children enumeration varies between loads (the
    // saved order does not) for flow nodes, connectors, switch conditions, arguments, and the members of a screen or
    // block (inputs, locals, aggregates, actions, events). Those follow the parent's Conditions list or their
    // parameter's position, then their GlobalKey, after the other children (widgets and the rest) in model order.
    public static IEnumerable<IObjectSignature> Children(IObjectSignature o)
    {
        // A field mapping holds its source expression; an argument or assignment holds its mappings (see FieldMappings).
        if (FieldMappings.Is(o)) return FieldMappings.Source(o) is IObjectSignature { } source ? [source] : [];
        var mappings = FieldMappings.Of(o);
        return mappings.Count == 0 ? Direct(o) : Direct(o).Concat(mappings);
    }

    static IEnumerable<IObjectSignature> Direct(IObjectSignature o)
    {
        var all = o.Children.Distinct().ToList();
        var member = Cached(holders, o, () => o is OutSystems.Model.UI.IScreenSignature || Inspect.IsBlock(o));
        bool Moves(IObjectSignature c) => member ? c is not OutSystems.Model.UI.IWidgetSignature : Varies(c);
        if (!all.Any(Moves)) return all;
        return all.Where(c => !Moves(c)).Concat(Ordered(all.Where(Moves), o));
    }

    // Per concrete type: Children runs for every object a reader or the resolver visits.
    static readonly Dictionary<Type, bool> holders = new(), varies = new();
    static bool Cached(Dictionary<Type, bool> cache, object o, Func<bool> compute)
    {
        lock (cache)
        {
            if (!cache.TryGetValue(o.GetType(), out var value)) cache[o.GetType()] = value = compute();
            return value;
        }
    }
    static bool Varies(IObjectSignature c) => Cached(varies, c, () => c is IFlowNodeSignature or OutSystems.Model.Logic.Nodes.ISwitchCondition
        || c.GetType().GetInterfaces().Any(i => i.Name is "IArgumentSignature" or "IArgument" || i.Name.EndsWith("Connector", StringComparison.Ordinal)));

    // Switch conditions keep their evaluation order. Screen members sort by GlobalKey: their typed lists vary too.
    static readonly string[] TypedLists = ["Conditions"];

    // Elements of a list whose model order varies (also describe's exception handlers and inspect's process nodes), in
    // the same stable order on every load.
    public static IEnumerable<T> Ordered<T>(IEnumerable<T> items, IObjectSignature? parent = null) where T : IObjectSignature
    {
        // A referenced module's proxy may throw for these getters: the element then sorts by GlobalKey only.
        static object? Get(object o, string key)
        {
            try { return Reflect.Property(o, key)?.GetValue(o); }
            catch (System.Reflection.TargetInvocationException) { return null; }
        }
        var lists = parent is null ? [] : TypedLists.Select(key => Get(parent, key) is System.Collections.IEnumerable xs ? xs.Cast<object>().ToList() : null).ToList();
        (int, int) Rank(T c)
        {
            for (var i = 0; i < lists.Count; i++)
                if (lists[i]?.IndexOf(c) is int at && at >= 0) return (i, at);
            if (Get(c, "Parameter") is IObjectSignature { Parent: { } owner } parameter
                && Get(owner, "InputParameters") is System.Collections.IEnumerable inputs
                && inputs.Cast<object>().ToList().IndexOf(parameter) is var position && position >= 0) return (lists.Count, position);
            return (int.MaxValue, 0);
        }
        return items.OrderBy(Rank).ThenBy(c => c.GlobalKey.ToString(), StringComparer.Ordinal);
    }

    // A name that a path can carry: no '/', and no "[digits]" at the end (that reads as an index).
    static bool Plain(string? name) => !string.IsNullOrEmpty(name) && !name.Contains('/') && !Regex.IsMatch(name, @"\[\d+\]$");

    public static string Segment(IObjectSignature o, IObjectSignature parent)
    {
        var name = Model.NameOf(o);
        if (Plain(name) && Children(parent).Count(c => Model.NameOf(c) == name) == 1) return name!;
        var kind = Kind(o);
        return $"{kind}[{Children(parent).Where(c => Kind(c) == kind).ToList().IndexOf(o)}]";
    }

    // write: the element is changed. Every part must then match one element of its group (the rest of the path does
    // not choose), and a referenced module's element (a proxy) is refused.
    public static IObjectSignature Resolve(IObjectSignature root, string path, bool write = false)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ModelError("element path is required");
        var failure = new Failure(write);
        var parts = path.Split('/');
        var hits = Matches(root, root, parts, 0, failure);
        if (failure.Several is { } several)
            throw parts.Length == 1 ? Ambiguous(root, path, several.group)
                : Ambiguous(root, path, several.group, parts[several.part]);
        if (hits.Count == 0) throw new ModelError(failure.Message(path));
        if (hits.Count > 1) throw Ambiguous(root, path, hits);
        if (write && Referenced(hits[0]))
            throw ProductionRules.Consumed(hits[0]);
        return hits[0];
    }

    public static bool Referenced(IObjectSignature o)
    {
        for (var p = o.Parent; p is not null; p = p.Parent) if (p is IReference) return true;
        return false;
    }

    // Every candidate with its kind and an exact path, sorted. Only the shown ones get the (slower) path search.
    // part: for a write, the part of the path that matched several elements; the paths listed are for that part.
    static AmbiguousPathError Ambiguous(IObjectSignature root, string path, List<IObjectSignature> hits, string? part = null)
    {
        string Chain(IObjectSignature o) => o.Parent is { } p && !ReferenceEquals(o, root) ? Chain(p) + "/" + Model.NameOf(o) : "";
        var shown = hits.OrderBy(h => Chain(h), StringComparer.Ordinal).ThenBy(h => h.GlobalKey.ToString(), StringComparer.Ordinal).Take(20);
        var listed = shown.Select(h => PathTo(root, h) is { } p
            ? p.Split('/')[^1].StartsWith(Describe.Kind(h) + ":", StringComparison.Ordinal) ? p : $"{p} ({Describe.Kind(h)})"
            : $"{Describe.Kind(h)} {Model.NameOf(h)} (no exact path; give it a unique name)").Order(StringComparer.Ordinal);
        var more = string.Join(", ", listed) + (hits.Count > 20 ? $", and {hits.Count - 20} more" : "");
        return new AmbiguousPathError(part is null
            ? $"ambiguous path '{path}': {hits.Count} elements match. Use one of these exact paths: " + more
            : $"ambiguous path '{path}': '{part}' matches {hits.Count} elements, and a change must name one. Use one of these exact paths for '{part}': " + more);
    }

    // The shortest path that resolves to exactly this element: Name, Module.Name, Kind:Name, then the parent's path.
    // Null when no form resolves to it alone.
    public static string? PathTo(IObjectSignature root, IObjectSignature o)
    {
        foreach (var path in Forms(root, o))
            if (Exact(root, path, o)) return path;
        return null;
    }

    // `path` when it means this element alone under the write rules, else the shortest path that does, else `path`.
    // For messages: the path they print must work in describe, tree and edits.
    public static string Best(IObjectSignature root, IObjectSignature o, string path) =>
        Exact(root, path, o) ? path : PathTo(root, o) ?? path;

    // True when the path means this element alone under the write rules (each part one element), so it works in
    // describe, tree and edits alike.
    public static bool Exact(IObjectSignature root, string path, IObjectSignature o)
    {
        var failure = new Failure(write: true);
        return Matches(root, root, path.Split('/'), 0, failure) is [var only] && failure.Several is null && ReferenceEquals(only, o);
    }

    static IEnumerable<string> Forms(IObjectSignature root, IObjectSignature o)
    {
        var name = Model.NameOf(o);
        if (Plain(name))
        {
            yield return name!;
            var owner = root is IESpace m ? Refs.All(m).FirstOrDefault(x => ReferenceEquals(x.element, o)).owner : null;
            if (owner is not null) yield return owner + "." + name;
            yield return Describe.Kind(o) + ":" + name;
            if (owner is not null) yield return Describe.Kind(o) + ":" + owner + "." + name;
        }
        // The tree parent: the nearest ancestor that lists this element as a child and has a path itself. A widget's
        // model parent is a content area, which lists it but has no path; its widget lists it too (tree shows that).
        for (var parent = o.Parent; parent is not null; parent = parent.Parent)
        {
            if (!Children(parent).Contains(o)) continue;
            if (ReferenceEquals(parent, root)) { yield return Segment(o, parent); yield break; }
            if (PathTo(root, parent) is not { } above) continue;
            yield return above + "/" + Segment(o, parent);
            if (Plain(name)) yield return above + "/" + Describe.Kind(o) + ":" + name;
            yield break;
        }
    }

    // Why a path found nothing: the deepest part that had no match, and the elements it was looked for under.
    sealed class Failure(bool write = false)
    {
        public readonly bool Write = write;
        public (int part, List<IObjectSignature> group)? Several;
        int depth = -1;
        string part = "";
        string? under;
        readonly SortedSet<string> kinds = new(StringComparer.Ordinal);
        public void Note(int i, string[] parts, IObjectSignature scope, bool fromRoot)
        {
            if (i < depth) return;
            if (i > depth) { depth = i; part = parts[i]; under = fromRoot ? null : parts[i - 1]; kinds.Clear(); }
            if (!fromRoot) kinds.Add(Describe.Kind(scope));
        }
        public string Message(string path) => under is null
            ? $"no element '{part}' in path '{path}'"
            : $"no element '{part}' under '{under}' ({string.Join(", ", kinds)}) in path '{path}'";
    }

    // Every element the rest of the path can mean from `scope`. The first group with a match decides (see Groups);
    // the rest of the path only chooses among that group's candidates.
    static List<IObjectSignature> Matches(IObjectSignature root, IObjectSignature scope, string[] parts, int i, Failure failure)
    {
        if (i == parts.Length) return [scope];
        foreach (var all in Groups(root, scope, parts[i], i == 0))
        {
            var group = all;
            if (group.Count == 0) continue;
            // The last part, directly under an action: its own input or output parameter wins over a flow node of the
            // same name (`Action/Response`); the typed form `Action/Kind:Name` still reaches the node. A middle part
            // keeps the old rule (the rest of the path chooses), so a path that worked still reaches the same element.
            if (i == parts.Length - 1 && group.Count > 1 && group.All(o => ReferenceEquals(o.Parent, scope))
                && group.Where(o => o is IInputParameterSignature or IOutputParameterSignature).ToList() is { Count: > 0 } own) group = own;
            if (failure.Write && group.Count > 1) { failure.Several ??= (i, group); return []; }
            var found = new List<IObjectSignature>();
            foreach (var candidate in group)
                foreach (var hit in Matches(root, candidate, parts, i + 1, failure))
                    if (!found.Contains(hit)) found.Add(hit);
            return found;
        }
        failure.Note(i, parts, scope, ReferenceEquals(scope, root) && i == 0);
        return [];
    }

    // Candidates for one part, best group first. From the module, a plain name looks at this module's own top-level
    // elements, then at referenced ones, then breadth-first through everything. Under an element, breadth-first only.
    static IEnumerable<List<IObjectSignature>> Groups(IObjectSignature root, IObjectSignature scope, string part, bool first)
    {
        var index = Regex.Match(part, @"^(.+)\[(\d+)\]$");
        if (index.Success)
        {
            bool At(IObjectSignature o) => Kind(o).Equals(index.Groups[1].Value, StringComparison.OrdinalIgnoreCase) && o.Parent is { } parent
                && Children(parent).Where(c => Kind(c) == Kind(o)).ToList().IndexOf(o) == int.Parse(index.Groups[2].Value);
            foreach (var level in Levels(scope, null)) yield return level.Where(At).ToList();
            yield break;
        }
        // A literal name may hold ':' (a node label "Note: x"), so it is also matched as written.
        var colon = part.IndexOf(':');
        var (kind, name) = colon > 0 ? (part[..colon], part[(colon + 1)..]) : ((string?)null, part);
        bool Fits(IObjectSignature o) => Model.NameOf(o) is { } n && (n == part || kind is not null && n == name && KindIs(o, kind));
        if (first && ReferenceEquals(scope, root) && root is IESpace m)
        {
            var own = ((OutSystems.Model.Applications.IModule)m).Name;
            var references = m.References.Cast<object>().ToHashSet();
            var dot = name.IndexOf('.');
            var module = dot > 0 ? name[..dot] : null;
            if (module is not null && (module == own || m.References.Any(r => r.Name == module)))
            {
                var element = name[(dot + 1)..];
                bool Named(IObjectSignature o) => Model.NameOf(o) == element && (kind is null || KindIs(o, kind));
                if (module == own)
                {
                    yield return TopLevel(m, own).Where(Named).ToList();
                    foreach (var level in Levels(m, references)) yield return level.Where(Named).ToList();
                }
                else yield return Refs.All(m).Where(x => x.owner == module).Select(x => x.element).OfType<IObjectSignature>().Where(Named).Distinct().ToList();
                yield break;
            }
            yield return TopLevel(m, own).Where(Fits).ToList();
            yield return Refs.All(m).Where(x => x.owner != own).Select(x => x.element).OfType<IObjectSignature>().Where(Fits).Distinct().ToList();
        }
        foreach (var level in Levels(scope, null)) yield return level.Where(Fits).ToList();
    }

    // This module's own top-level elements: what the module holds directly (not its references), plus the screens,
    // blocks, generated entity actions and role actions that a spec names as if they were top-level.
    static IEnumerable<IObjectSignature> TopLevel(IESpace m, string own) =>
        Children(m).Where(c => c is not IReference)
            .Concat(Refs.All(m).Where(x => x.owner == own).Select(x => x.element).OfType<IObjectSignature>()).Distinct();

    // Breadth-first levels under scope, each element once; `skip` subtrees are not entered.
    static IEnumerable<List<IObjectSignature>> Levels(IObjectSignature scope, HashSet<object>? skip)
    {
        var visited = new HashSet<IObjectSignature> { scope };
        var level = Children(scope).Where(c => skip?.Contains(c) != true).ToList();
        while (level.Count > 0)
        {
            level = level.Where(visited.Add).ToList();
            yield return level;
            level = level.SelectMany(Children).Where(c => skip?.Contains(c) != true).Distinct().ToList();
        }
    }

    // The kind before ':' or '[': the kind describe and the error list print, the concrete type tree prints,
    // or a model interface name (Entity, Flow, Action).
    static bool KindIs(IObjectSignature o, string kind) =>
        Describe.Kind(o).Equals(kind, StringComparison.OrdinalIgnoreCase) || Kind(o).Equals(kind, StringComparison.OrdinalIgnoreCase)
        || o.GetType().GetInterfaces().Any(i => i.Namespace?.StartsWith("OutSystems.Model") == true
            && Regex.Replace(i.Name, "^I|Signature$", "").Equals(kind, StringComparison.OrdinalIgnoreCase));
}
