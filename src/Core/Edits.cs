using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Logic.Nodes;

namespace OmlCli;

partial class Builder
{
    static IEnumerable<object> Elements(object? value) => value is System.Collections.IEnumerable list
        ? list.Cast<object>() : Enumerable.Empty<object>();

    void Edit(List<JsonElement>? edits)
    {
        foreach (var stage in new[] { flows, references, targets, widgets }) stage.Clear();
        var index = 0;
        foreach (var edit in edits ?? new())
        {
            var label = $"edit[{index++}]";
            Try(label, () => EditOne(edit, label));
            // Finish topology before resolving the next edit, while keeping expressions last.
            foreach (var stage in new[] { flows, references, targets, widgets })
            {
                for (var i = 0; i < stage.Count; i++) Try(stage[i].label, stage[i].apply);
                stage.Clear();
            }
        }
        foreach (var expression in expressions) Try(expression.label, expression.apply);
        foreach (var deletion in dataDeletes) Try(deletion.label, deletion.apply);
    }

    void EditOne(JsonElement edit, string label)
    {
        string Required(string key) => edit.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()! : throw new ModelError($"'{key}' is required");
        IObjectSignature Find(string key) => ElementPaths.Resolve(m, Required(key), write: true);
        T Read<T>() => JsonSerializer.Deserialize<T>(edit.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        switch (Required("op"))
        {
            case "addExceptionHandler":
            {
                var action = Find("action");
                if (!Reflect.Has(action, "CreateNode") || Reflect.Get(action, "Nodes") is null)
                    throw new ModelError("action must be an editable action or data action");
                var flow = edit.GetProperty("flow").EnumerateArray().ToList();
                if (flow.Count == 0 || !flow[0].GetProperty("kind").GetString()!.Equals("exceptionHandler", StringComparison.OrdinalIgnoreCase))
                    throw new ModelError("addExceptionHandler requires a flow starting with exceptionHandler");
                Flow(action, flow, label, connectEntry: false);
                break;
            }
            case "setAccess":
                SetAccess(Find("screen"), Read<AccessSpec>());
                break;

            case "insertNodes":
            {
                var action = Find("action");
                if (!Reflect.Has(action, "CreateNode")) throw new ModelError("action must be an editable action or data action");
                var after = ElementPaths.Resolve(action, Required("after"));
                if (after is not IActionNode) throw new ModelError("after must be a flow node");
                var branch = Reflect.Property(after, "TrueTarget") is not null || Reflect.Property(after, "CycleTarget") is not null;
                var connector = edit.TryGetProperty("connector", out var c) ? c.GetString()! : branch
                    ? throw new ModelError("if/forEach requires connector: true, false, cycle or next") : "next";
                var property = ConnectorProperty(connector);
                if (Reflect.Property(after, property, writable: true) is null) throw new ModelError($"node has no '{connector}' connector");
                var outgoing = Edges(action).Where(e => ReferenceEquals(e.from, after) && e.property == property).ToList();
                if (outgoing.Count != 1) throw new ModelError("insertion requires exactly one existing outgoing connector of the selected kind");
                if (!edit.TryGetProperty("flow", out var flow) || flow.ValueKind != JsonValueKind.Array || flow.GetArrayLength() == 0)
                    throw new ModelError("insertNodes requires a nonempty flow");
                Flow(action, flow.EnumerateArray().ToList(), label, after, property, outgoing[0].to);
                break;
            }
            case "addWidgets":
            {
                var screen = Find("screen");
                var parent = edit.TryGetProperty("parent", out var p) ? ElementPaths.Resolve(screen, p.GetString()!) : screen;
                var area = edit.TryGetProperty("area", out var a) ? a.GetString()! : "children";
                var container = area == "children" ? Content(parent) : Reflect.Get(parent, area)
                    ?? throw new ModelError($"no content area '{area}'");
                var handlers = Elements(Reflect.Get(screen, "ScreenActions")).ToDictionary(x => Model.NameOf(x)!, x => x);
                var old = Elements(Reflect.Get(container, "Widgets")).ToList();
                var index = edit.TryGetProperty("index", out var ix) ? ix.GetInt32() : old.Count;
                if (index < 0 || index > old.Count) throw new ModelError($"index must be between 0 and {old.Count}");
                var added = new List<object>();
                foreach (var widget in edit.GetProperty("widgets").EnumerateArray())
                    if (Widget(container, widget, label, handlers) is { } made) added.Add(made);
                if (edit.TryGetProperty("index", out _))
                {
                    var insertion = index;
                    foreach (var widget in added) Reflect.Call(widget, "MoveToIndex", index++);
                    var ordered = Elements(Reflect.Get(container, "Widgets")).ToList();
                    if (!ordered.Skip(insertion).Take(added.Count).SequenceEqual(added))
                        throw new ModelError("model did not preserve the requested widget index");
                }
                break;
            }
            case "replaceWidgets":
            {
                // The element at "at" stays (its name, style and events); every widget inside it is replaced.
                var at = Find("at");
                var screen = new[] { at }.Concat(Ancestors(at)).FirstOrDefault(o => o is OutSystems.Model.UI.IScreenSignature or OutSystems.Model.UI.IBlockSignature or OutSystems.Model.UI.IEmail)
                    ?? throw new ModelError("'at' must be the path of a screen, block or email, or of a widget or placeholder inside one");
                var area = edit.TryGetProperty("area", out var a) ? a.GetString()! : "children";
                var container = area != "children" ? Reflect.Get(at, area) ?? throw new ModelError($"no content area '{area}'")
                    : ReferenceEquals(at, screen) ? screen : Content(at);
                if (!edit.TryGetProperty("widgets", out var given) || given.ValueKind != JsonValueKind.Array) throw new ModelError("'widgets' must be an array of widgets");
                if (screen is OutSystems.Model.UI.IEmail) foreach (var w in given.EnumerateArray()) EmailWidget(w);
                if (Reflect.Get(container, "Widgets") is not System.Collections.IEnumerable current || !Reflect.Has(container, "CreateWidget"))
                    throw new ModelError($"'{Required("at")}' holds no widgets; set 'at' to a screen, block, email, widget or placeholder (paths in 'oml tree')");
                var old = current.OfType<IObjectSignature>().ToList();
                var (broken, rebound) = WidgetUsages(old, WidgetNames(given));
                if (broken.Count > 0)
                    throw new ModelError($"refused: replaceWidgets at '{Required("at")}' would break what uses the widgets it removes: {string.Join("; ", broken)}. "
                        + "Change those usages first (edits set or delete), or replace a smaller part with a deeper \"at\".");
                // A removed Label's link to its input, unless a new Label in "widgets" names the same target.
                var links = old.SelectMany(ProductionRules.Walk).Select(w => (label: w, target: Reflect.Get(w, "TargetWidget") as IObjectSignature))
                    .Where(l => l.target is not null).Select(l => (label: Model.NameOf(l.label) ?? "a Label", target: Model.NameOf(l.target!) ?? "")).ToList();
                var targets = WidgetNames(given, "targetWidget");
                foreach (var (labelName, targetName) in links.Where(l => !targets.Contains(l.target)))
                    Warnings.Add($"replaceWidgets at '{Required("at")}' removed the Label {labelName}, which pointed at {targetName}; no new Label has \"targetWidget\": \"{targetName}\", so that input lost its label link (screen readers use it)");
                foreach (var w in old) rules.Delete(w);
                foreach (var w in old) w.Delete();
                var handlers = Elements(Reflect.Get(screen, "ScreenActions")).ToDictionary(x => Model.NameOf(x)!, x => x);
                var made = new List<IObjectSignature>();
                foreach (var widget in given.EnumerateArray()) if (Widget(container, widget, label, handlers) is IObjectSignature w) made.Add(w);
                // An expression finds the new widget by name; it must be the same kind, or Name.Valid, Name.Id ... change meaning.
                var now = made.SelectMany(ProductionRules.Walk).Where(w => Model.NameOf(w) is not null).GroupBy(w => Model.NameOf(w)!).ToDictionary(g => g.Key, g => g.First());
                var retyped = rebound.Where(r => !now.TryGetValue(r.name, out var w) || ElementPaths.Kind(w) != r.kind)
                    .Select(r => now.TryGetValue(r.name, out var w)
                        ? $"{r.name} was {Article(r.kind)} {r.kind}, now {Article(ElementPaths.Kind(w))} {ElementPaths.Kind(w)}; used by {r.users}"
                        : $"{r.name} was {Article(r.kind)} {r.kind} and no new widget has that name; used by {r.users}").ToList();
                if (retyped.Count > 0)
                    throw new ModelError($"refused: replaceWidgets at '{Required("at")}' gives a used widget name to another kind of widget: {string.Join("; ", retyped)}. Keep the kind; if the new kind is intended, rename the widget or update the usages in the same spec.");
                // One entry per element, however the spec writes its path (Home/Box, MainFlow/Home/Box).
                if (replacedAt.Add(at)) Change($"widgets of {Required("at")}");
                break;
            }
            case "addVariables":
            {
                var action = Find("action");
                var s = Read<ActionSpec>();
                Variables(action, "CreateInputParameter", s.Inputs, label);
                Variables(action, "CreateOutputParameter", s.Outputs, label);
                Variables(action, "CreateLocalVariable", s.Locals, label);
                break;
            }
            case "addParts": ScreenParts(Find("screen"), Read<ScreenSpec>(), label); break;
            case "addToAggregate":
            {
                // Adds to an existing aggregate with the creation keys. Change or remove its parts with set and delete.
                var aggregate = Find("aggregate");
                if (Reflect.Get(aggregate, "AsDatabaseAggregate") is null) throw new ModelError("aggregate must be the path of an aggregate, such as Action/GetOrders or Screen/GetOrders");
                if (edit.EnumerateObject().Select(p => p.Name).FirstOrDefault(k => k is not ("op" or "aggregate") && (!QueryKeys.Contains(k) || k is "name" or "maxRecords" or "fetch" or "onAfterFetch" or "timeout" or "cacheInMinutes")) is { } other)
                    throw new ModelError($"addToAggregate does not take '{other}'; change aggregate properties with set");
                Aggregate(aggregate, edit, label);
                break;
            }
            case "setArgs":
            {
                var node = Find("element");
                if (!edit.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object) throw new ModelError("'args' must be an object of parameter name to expression");
                Defer(node, ($"{label}.args", () => Arguments(node, args)));
                break;
            }
            case "set":
            {
                var target = Find("element");
                foreach (var p in edit.GetProperty("properties").EnumerateObject())
                {
                    rules.Set(target, p.Name, p.Value);
                    if (p.Name.Equals("name", StringComparison.OrdinalIgnoreCase)) throw new ModelError("use rename to change names");
                    // At once, not with the expressions: a later edit (replaceWidgets) must see the new target.
                    if (p.Name.Equals("targetWidget", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                        TargetWidget(target, p.Value.GetString()!);
                    else Queue(label, m, target, p.Name, p.Value);
                }
                break;
            }
            case "rename":
            {
                var target = Find("element");
                rules.Rename(target);
                var wanted = Required("to");
                var name = (Reflect.Property(target, "Name", writable: true) ?? Reflect.Property(target, "Label", writable: true)) ?? throw new ModelError("element cannot be renamed");
                if (target.Parent is { } parent && ElementPaths.Children(parent).FirstOrDefault(c => !ReferenceEquals(c, target) && Model.NameOf(c) == wanted) is { } sibling)
                    throw new ModelError($"name '{wanted}' already exists in this parent: {NameClash.Describe(sibling)}");
                name.SetValue(target, wanted);
                if (aggregateNames.ContainsKey(target)) aggregateNames[target] = wanted;
                if (Model.NameOf(target) != wanted) throw new ModelError(ProductionRules.ConsumedClash(target, wanted) ?? $"{NameClash.Taken(target, wanted)}; model renamed to '{Model.NameOf(target)}' instead of '{wanted}'");
                break;
            }
            case "delete":
            {
                var target = Find("element");
                rules.Delete(target);
                if (target is OutSystems.Model.Data.IEntitySignature or OutSystems.Model.Types.IAttributeSignature)
                {
                    dataDeletes.Add((label, () => DeleteData(target, edit)));
                    break;
                }
                if (target is IActionNode)
                {
                    var edges = Edges(target.Parent!).ToList();
                    var incoming = edges.Where(e => ReferenceEquals(e.to, target)).ToList();
                    var outgoing = edges.Where(e => ReferenceEquals(e.from, target)).ToList();
                    if (incoming.Count != 1 || outgoing.Count != 1 || ReferenceEquals(outgoing[0].to, target))
                        throw new ModelError("flow node deletion requires exactly one incoming and one outgoing connector");
                    Connect(incoming[0].from, incoming[0].property, outgoing[0].to);
                }
                // Deleting a filter, sort or group of a grouped aggregate can drop its source, with no validation error.
                // Inside an aggregate, check that every other part is still there.
                var db = Ancestors(target).Select(a => Reflect.Get(a, "AsDatabaseAggregate")).FirstOrDefault(a => a is not null);
                var all = db is null ? new List<(object part, string what)>() : AggregateParts(db).ToList();
                if (db is not null && !all.Any(p => ReferenceEquals(p.part, target)))
                    throw new ModelError($"'{Required("element")}' is internal structure of an aggregate; delete its sources, joins, filters, sorts, groups or attributes instead");
                var parts = all.Where(p => !ReferenceEquals(p.part, target)).ToList();
                try { target.Delete(); }
                catch (Exception e) when (e is not ModelError)
                {
                    throw new ModelError($"the model could not delete '{Required("element")}' ({Innermost(e).Message}); do it in Service Studio");
                }
                if (db is not null)
                {
                    HashSet<object> after;
                    try { after = AggregateParts(db).Select(p => p.part).ToHashSet(); }
                    catch (Exception e) when (e is not ModelError)
                    {
                        throw new ModelError($"deleting '{Required("element")}' left the aggregate unreadable ({Innermost(e).Message}); do it in Service Studio");
                    }
                    if (parts.FirstOrDefault(p => ((IObjectSignature)p.part).WasDeleted || !after.Contains(p.part)) is { what: { } lost })
                        throw new ModelError($"deleting '{Required("element")}' would also remove {lost} (a model side effect); do it in Service Studio");
                }
                break;
            }
            default: throw new ModelError($"unknown edit op '{Required("op")}'");
        }
        created.Add(label);
    }

    // What uses the widgets a replaceWidgets removes, from outside them. An expression finds a widget by name, so a new
    // widget with the same name (and kind, checked after the build) keeps it working: those are "rebound". Any other
    // reference (a Label's target widget, ...) points at the old widget itself and always breaks.
    (List<string> broken, List<(string name, string kind, string users)> rebound) WidgetUsages(List<IObjectSignature> removed, HashSet<string> kept)
    {
        var inside = removed.SelectMany(ProductionRules.Walk).ToHashSet();
        var broken = new SortedSet<string>(StringComparer.Ordinal);
        var rebound = new List<(string, string, string)>();
        foreach (var widget in inside)
        {
            if (Reflect.Property(widget, "Referers") is null) throw new ModelError($"cannot safely inspect usages of {widget.GetType().Name} on this build");
            var users = Elements(Reflect.Get(widget, "Referers")).OfType<IObjectSignature>()
                .Where(u => !u.WasDeleted && !inside.Contains(u) && !Ancestors(u).Any(inside.Contains)).ToList();
            if (users.Count == 0) continue;
            var name = Model.NameOf(widget) ?? ElementPaths.Kind(widget);
            var expressions = users.Where(u => u is IExpression).Select(UsagePath).Distinct().Order(StringComparer.Ordinal).ToList();
            var references = users.Where(u => u is not IExpression).Select(UsagePath).Distinct().Order(StringComparer.Ordinal).ToList();
            if (references.Count > 0)
                broken.Add($"{name} is the target of {string.Join(", ", references)} (a reference to this widget itself, which a new widget of the same name does not take over; first point it at another widget, for a Label with set \"targetWidget\": \"<name>\")");
            if (expressions.Count == 0) continue;
            if (Model.NameOf(widget) is { } kept1 && kept.Contains(kept1)) rebound.Add((kept1, ElementPaths.Kind(widget), string.Join(", ", expressions)));
            else broken.Add($"{name} (used by {string.Join(", ", expressions)}): keep a widget of the same name and kind in \"widgets\"");
        }
        return (broken.ToList(), rebound);
    }

    // The node or widget a usage belongs to, as newErrors name owners (the named path, then Kind[index] when it has no
    // name), when that path means it alone; else the shortest path that does (an unnamed widget's index path may not).
    string UsagePath(IObjectSignature user)
    {
        var real = new[] { user }.Concat(Ancestors(user)).FirstOrDefault(o => o is IActionNode or OutSystems.Model.UI.IWidgetSignature)
            ?? new[] { user }.Concat(Ancestors(user)).FirstOrDefault(o => Model.NameOf(o) is not null) ?? user;
        var named = Model.OwnerName(real) ?? "";
        return ElementPaths.Best(m, real, Model.NameOf(real) is not null || real.Parent is null ? named : $"{Model.OwnerName(real.Parent)}/{ElementPaths.Segment(real, real.Parent)}");
    }

    static string Article(string word) => "AEIOU".Contains(char.ToUpperInvariant(word[0])) ? "an" : "a";

    // The names a widget spec gives (or the values of another key, such as targetWidget), at any depth.
    static HashSet<string> WidgetNames(JsonElement spec, string key = "name")
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Walk(x);
            if (e.ValueKind != JsonValueKind.Object) return;
            if (e.TryGetProperty("type", out _) && e.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.String) names.Add(n.GetString()!);
            foreach (var p in e.EnumerateObject()) Walk(p.Value);
        }
        Walk(spec);
        return names;
    }

    // The parts of an aggregate that its result depends on. A grouped aggregate also has a source for the group
    // itself; its Source getter throws, and it is not a part a person added, so it is left out.
    static IEnumerable<(object part, string what)> AggregateParts(object db)
    {
        foreach (var s in Elements(Reflect.Get(db, "Sources")))
        {
            object? source;
            try { source = Reflect.Get(s, "Source"); }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is InvalidCastException) { continue; }
            yield return (s, $"its source {Model.NameOf(source!)}");
        }
        foreach (var (key, what, text) in new[] { ("Joins", "join", "Condition"), ("Filters", "filter", "Condition"), ("FiltersInGroupBy", "group filter", "Condition"),
                     ("Sorts", "sort", "Attribute"), ("SortsInGroupBy", "group sort", "Attribute"), ("GroupByAttributes", "group", null),
                     ("AggregateAttributes", "aggregate attribute", null), ("CalculatedAttributes", "calculated attribute", null),
                     ("CalculatedAttributesInGroupBy", "group calculated attribute", null) })
            foreach (var p in Elements(Reflect.Get(db, key)))
                yield return (p, $"its {what} {(text is null ? Model.NameOf(p) : (Reflect.Get(p, text) as IExpression)?.Text)}");
    }

    static Exception Innermost(Exception e)
    {
        while (e.InnerException is not null) e = e.InnerException;
        return e;
    }

    static IEnumerable<IObjectSignature> Ancestors(IObjectSignature o)
    {
        for (var p = o.Parent; p is not null; p = p.Parent) yield return p;
    }

    static string ConnectorProperty(string connector) => connector.ToLowerInvariant() switch
    {
        "true" => "TrueTarget", "false" => "FalseTarget", "cycle" => "CycleTarget", "next" or "sequence" => "Target",
        _ => throw new ModelError($"unknown connector '{connector}'"),
    };

    static IEnumerable<(object from, object to, string property)> Edges(IObjectSignature action)
    {
        foreach (var node in Elements(Reflect.Get(action, "Nodes")).OfType<IObjectSignature>())
        foreach (var edge in node.Children.Concat(Elements(Reflect.Get(node, "Connectors")).OfType<IObjectSignature>()).Distinct())
            if (Reflect.Get(edge, "SourceNode") is { } from && Reflect.Get(edge, "TargetNode") is { } to)
                yield return (from, to, ConnectorProperty(edge.GetType().Name));
    }
}
