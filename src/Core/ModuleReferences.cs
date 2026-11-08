using System.Reflection;
using System.Text.Json;
using OutSystems.Model;
using E = OutSystems.Model.Extensions;

namespace OmlCli;

record ReferenceSpec(string Producer, JsonElement Elements, string? Extension = null);

static class ModuleReferences
{
    public static void Alias(IESpace m, Dictionary<string, string>? aliases, List<string> problems, ProductionRules rules)
    {
        foreach (var (qualified, alias) in aliases ?? new())
        {
            try
            {
                if (!qualified.Contains('.')) throw new ModelError("reference alias needs Module.Element");
                var dot = qualified.IndexOf('.');
                var owner = qualified[..dot];
                var target = Refs.All(m).FirstOrDefault(x => x.owner == owner && Model.NameOf(x.element) == qualified[(dot + 1)..]).element
                    ?? Refs.Find(m, owner + "." + alias, typeof(IObjectSignature));
                if (!Refs.All(m).Any(x => x.owner != ((OutSystems.Model.Applications.IModule)m).Name && ReferenceEquals(x.element, target)))
                    throw new ModelError("only referenced elements can be aliased");
                var property = Reflect.Property(target, "Name", writable: true) ?? throw new ModelError("reference name is not writable on this build");
                rules.RenameReference((IObjectSignature)target);
                property.SetValue(target, alias);
                if (Model.NameOf(target) != alias) throw new ModelError($"alias {alias}: {NameClash.Taken(target, alias)}; the model renamed it {Model.NameOf(target)}");
            }
            catch (Exception e) when (e is ModelError or ArgumentException or InvalidOperationException or TargetInvocationException)
            { problems.Add($"reference alias {qualified}: {(e.InnerException ?? e).Message}"); }
        }
    }

    static readonly string[] Collections = { "Entities", "Structures", "ServerActions", "ServiceActions",
        "ClientActions", "Roles", "UserExceptions", "MobileThemes", "WebThemes",
        "Images", "Scripts", "Resources" };

    internal static IEnumerable<IObjectSignature> Elements(object module)
    {
        var seen = new HashSet<IObjectSignature>();
        foreach (var key in Collections.Concat(new[] { "MobileFlows", "WebFlows" }))
            if (Reflect.Get(module, key) is System.Collections.IEnumerable items)
                foreach (var item in items.Cast<IObjectSignature>())
                    if (key.EndsWith("Flows"))
                    {
                        if (Reflect.Get(item, "Nodes") is System.Collections.IEnumerable nodes)
                            foreach (var node in nodes.OfType<IObjectSignature>())
                                if (seen.Add(node)) yield return node;
                    }
                    else if (seen.Add(item)) yield return item;
        // Referenced public screens can be children outside the flow's Nodes collection.
        if (module is IReference reference)
            foreach (var screen in Descendants(reference).OfType<OutSystems.Model.UI.IScreenSignature>())
                if (seen.Add(screen)) yield return screen;
    }

    internal static IEnumerable<IObjectSignature> Descendants(IObjectSignature root)
    {
        var seen = new HashSet<IObjectSignature>();
        var pending = new Stack<IObjectSignature>(root.Children);
        while (pending.TryPop(out var item))
            if (seen.Add(item))
            {
                yield return item;
                foreach (var child in item.Children) pending.Push(child);
            }
    }

    internal static bool Same(IObjectSignature a, IObjectSignature b) => Equals(a.GlobalKey, b.GlobalKey);

    public static void Add(IESpace consumer, List<ReferenceSpec>? specs,
        List<string> created, List<string> existing, List<string> problems, List<string>? warnings = null)
    {
        foreach (var spec in specs ?? new())
        {
            var label = $"reference {spec.Producer}";
            try
            {
                // Extensions: from their .xif, or (platform extensions have no local .xif) from another module that references them.
                if (spec.Extension is not null || spec.Producer.EndsWith(".xif", StringComparison.OrdinalIgnoreCase))
                {
                    AddExtension(consumer, spec, created, existing, problems, warnings);
                    continue;
                }
                // LoadESpace reads bytes, so the producer never has a writable file path.
                using var producer = Model.Load(Path.GetFullPath(spec.Producer));
                if (Equals(producer.ObjectKey, consumer.ObjectKey)) throw new ModelError("a module cannot reference itself");
                var all = Elements(producer).ToList();
                List<IObjectSignature> selected;
                if (spec.Elements.ValueKind == JsonValueKind.String && spec.Elements.GetString() == "*")
                    selected = all.Where(e => Reflect.Get(e, "Public") is true).ToList();
                else if (spec.Elements.ValueKind == JsonValueKind.Array)
                {
                    selected = new();
                    foreach (var value in spec.Elements.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.String) throw new ModelError("elements must contain names");
                        var name = value.GetString();
                        var hits = all.Where(e => Model.NameOf(e) == name).ToList();
                        if (hits.Count != 1) throw new ModelError($"element '{name}' is {(hits.Count == 0 ? "missing" : "ambiguous; use a unique producer element name")}");
                        if (Reflect.Get(hits[0], "Public") is not true) throw new ModelError($"element '{name}' is not public");
                        selected.Add(hits[0]);
                    }
                }
                else throw new ModelError("elements must be an array of names or \"*\"");

                foreach (var element in selected.Distinct())
                {
                    var where = $"reference {Model.NameOf(producer)}.{Model.NameOf(element)}";
                    try
                    {
                        if (consumer.References.SelectMany(Descendants).Any(e => Same(e, element)))
                        { existing.Add(where); continue; }
                        var contract = element.GetType().GetInterfaces().Where(t => t.IsGenericType
                            && t.GetGenericTypeDefinition() == typeof(IShareable<,>))
                            .OrderByDescending(t => t.GetGenericArguments()[0] == element.GetInterface())
                            .ThenByDescending(t => t.GetGenericArguments()[0].GetInterfaces().Length).FirstOrDefault()
                            ?? throw new ModelError($"{element.GetInterface().Name} is not shareable on this build");
                        var factory = typeof(IESpace).GetMethods().Single(m => m.Name == "AddDependency" && m.IsGenericMethodDefinition);
                        var result = factory.MakeGenericMethod(contract.GetGenericArguments()).Invoke(consumer, new object[] { element });
                        if (result is not IObjectSignature imported || !Same(imported, element))
                            throw new ModelError("AddDependency did not return the requested element");
                        if (Model.NameOf(imported) != Model.NameOf(element))
                            throw new ModelError($"name clash: {NameClash.Taken(imported, Model.NameOf(element)!)}; model renamed the reference to {Model.NameOf(imported)}");
                        created.Add(where);
                    }
                    catch (Exception e) { problems.Add($"{where}: {Reason(e)}"); }
                }
            }
            catch (Exception e) { problems.Add($"{label}: {Reason(e)}"); }
        }
    }

    static void AddExtension(IESpace consumer, ReferenceSpec spec, List<string> created, List<string> existing, List<string> problems, List<string>? warnings)
    {
        var path = Path.GetFullPath(spec.Producer);
        if (spec.Producer.EndsWith(".xif", StringComparison.OrdinalIgnoreCase))
        {
            if (spec.Extension is not null) throw new ModelError("\"extension\" names a reference inside another .oml; a .xif producer is the extension itself");
            using var extension = (E.IExtension)Reflect.Call(Model.Services(), "LoadExtension", File.ReadAllBytes(path))!;
            AddActions(consumer, extension, Names(spec.Elements, () => extension.Actions.Select(a => a.Name)), created, existing, problems);
            return;
        }
        using var source = Model.Load(path);
        var reference = source.References.FirstOrDefault(r => r.Name == spec.Extension && r.Kind == OutSystems.Model.Enumerations.ReferenceKind.Extension)
            ?? throw new ModelError($"{Path.GetFileName(path)} has no reference to an extension named '{spec.Extension}'");
        var names = Names(spec.Elements, () => reference.ServerActions.Select(a => a.Name!));
        foreach (var name in names.Where(n => !reference.ServerActions.Any(a => a.Name == n)))
            throw new ModelError($"{Path.GetFileName(path)} references '{spec.Extension}' without an action named '{name}'. Only the actions it uses can be copied: {string.Join(", ", reference.ServerActions.Select(a => a.Name))}");
        E.IExtension standIn;
        try { standIn = StandIn(source, reference, names); }
        catch (ModelError e) when (e.Message.Contains("reserved", StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelError($"'{spec.Extension}' is a platform extension: Service Studio reserves its key, so oml cannot make the reference from {Path.GetFileName(path)}, and platform extensions have no .xif to read. "
                + $"Add it once by hand: Manage Dependencies in Service Studio, select {spec.Extension} and its actions, then apply the spec to that module.");
        }
        using var _ = standIn;
        AddActions(consumer, standIn, names, created, existing, problems);
        warnings?.Add($"reference {spec.Extension}: copied from the reference in {Path.GetFileName(path)}, with the extension's own keys. Run 'oml references <module>' (Refresh All) in Service Studio, or publish it there, to check it against the extension on the server.");
    }

    static List<string> Names(JsonElement elements, Func<IEnumerable<string>> all)
    {
        if (elements.ValueKind == JsonValueKind.String && elements.GetString() == "*") return all().ToList();
        if (elements.ValueKind != JsonValueKind.Array) throw new ModelError("elements must be an array of names or \"*\"");
        return elements.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new ModelError("elements must contain names")).ToList();
    }

    static void AddActions(IESpace consumer, E.IExtension extension, List<string> names, List<string> created, List<string> existing, List<string> problems)
    {
        var module = ((OutSystems.Model.Applications.IModule)extension).Name;
        foreach (var name in names.Distinct())
        {
            var where = $"reference {module}.{name}";
            try
            {
                var action = extension.Actions.FirstOrDefault(a => a.Name == name)
                    ?? throw new ModelError($"the extension has no action named '{name}' (only actions can be referenced; it has: {string.Join(", ", extension.Actions.Select(a => a.Name))})");
                if (consumer.References.Any(r => Equals(r.ModuleKey, ((OutSystems.Model.Applications.IModule)extension).Key) && r.ServerActions.Any(a => a.Name == name)))
                { existing.Add(where); continue; }
                if (consumer.AddDependency(action) is not { } added || Model.NameOf(added) != name)
                    throw new ModelError("AddDependency did not return the requested action");
                // The model drops the Function flag on the reference; without it an expression cannot call the action.
                if (action.Function && Reflect.Property(added, "IsFunction", writable: true) is { } function) function.SetValue(added, true);
                created.Add(where);
            }
            catch (Exception e) { problems.Add($"{where}: {Reason(e)}"); }
        }
    }

    // A platform extension has no local .xif, but a module that references it holds its signature and its keys. An
    // extension with those keys, made in memory, gives the consumer the same reference Service Studio would make.
    static E.IExtension StandIn(IESpace source, IReference reference, List<string> names)
    {
        var services = Model.Services();
        var extension = (E.IExtension)Reflect.Call(services, "CreateExtension", reference.Name, "", reference.ModuleKey)!;
        var structures = new Dictionary<string, E.IStructure>(StringComparer.Ordinal);
        E.IType Type(OutSystems.Model.Types.ITypeSignature? type)
        {
            if (type is OutSystems.Model.Types.IListType list) return (E.IType)Reflect.Call(extension, "GetOrCreateListType", Type(list.ElementType))!;
            if (type is OutSystems.Model.Data.IStructureSignature s && Reference(s) == reference)
            {
                if (structures.TryGetValue(s.Name, out var made)) return made;
                var copy = (E.IStructure)Reflect.Call(extension, "CreateStructure", s.Name, s.ObjectKey)!;
                structures[s.Name] = copy;
                foreach (var a in s.Attributes)
                {
                    var attribute = (E.IStructureAttribute)Reflect.Call(copy, "CreateAttribute", a.Name, a.ObjectKey)!;
                    Reflect.Property(attribute, "DataType", true)!.SetValue(attribute, Type(a.DataType));
                    Reflect.Property(attribute, "IsMandatory", true)?.SetValue(attribute, a.IsMandatory);
                }
                return copy;
            }
            var basic = Model.Describe(source, type);
            return Extensions.Resolve(extension, basic);
        }
        foreach (var signature in reference.ServerActions.Where(a => names.Contains(a.Name!)))
        {
            var action = (E.IAction)Reflect.Call(extension, "CreateAction", signature.Name, signature.ObjectKey)!;
            if (Reflect.Get(signature, "IsFunction") is true || Reflect.Get(signature, "Function") is true) Reflect.Property(action, "Function", true)?.SetValue(action, true);
            foreach (var (collection, factory) in new[] { ("InputParameters", "CreateInputParameter"), ("OutputParameters", "CreateOutputParameter") })
                foreach (var p in ((System.Collections.IEnumerable)Reflect.Get(signature, collection)!).Cast<IObjectSignature>())
                {
                    var parameter = Reflect.Call(action, factory, Model.NameOf(p), p.ObjectKey)!;
                    Reflect.Property(parameter, "DataType", true)!.SetValue(parameter, Type(Reflect.Get(p, "DataType") as OutSystems.Model.Types.ITypeSignature));
                    if (parameter is E.IInputParameter) Reflect.Property(parameter, "IsMandatory", true)?.SetValue(parameter, Reflect.Get(p, "IsMandatory") is true);
                }
        }
        return extension;
    }

    static IReference? Reference(IObjectSignature element)
    {
        for (var p = element.Parent; p is not null; p = p.Parent) if (p is IReference r) return r;
        return null;
    }

    static string Reason(Exception e)
    {
        while (e is TargetInvocationException && e.InnerException is { } inner) e = inner;
        return e is NotImplementedException
            ? "the headless model could not complete this operation (a Service Studio dialog, including IPP protection, may be required); open the producer in its authorized infrastructure. " + e.Message
            : e.Message;
    }
}
