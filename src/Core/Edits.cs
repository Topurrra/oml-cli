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
        IObjectSignature Find(string key) => ElementPaths.Resolve(m, Required(key));
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
            case "set":
            {
                var target = Find("element");
                foreach (var p in edit.GetProperty("properties").EnumerateObject())
                {
                    rules.Set(target, p.Name, p.Value);
                    if (p.Name.Equals("name", StringComparison.OrdinalIgnoreCase)) throw new ModelError("use rename to change names");
                    Queue(label, m, target, p.Name, p.Value);
                }
                break;
            }
            case "rename":
            {
                var target = Find("element");
                rules.Rename(target);
                var wanted = Required("to");
                var name = (Reflect.Property(target, "Name", writable: true) ?? Reflect.Property(target, "Label", writable: true)) ?? throw new ModelError("element cannot be renamed");
                if (target.Parent is { } parent && ElementPaths.Children(parent).Any(c => !ReferenceEquals(c, target) && Model.NameOf(c) == wanted))
                    throw new ModelError($"name '{wanted}' already exists in this parent");
                name.SetValue(target, wanted);
                if (aggregateNames.ContainsKey(target)) aggregateNames[target] = wanted;
                if (Model.NameOf(target) != wanted) throw new ModelError($"name taken; model renamed to '{Model.NameOf(target)}' instead of '{wanted}'");
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
                target.Delete();
                break;
            }
            default: throw new ModelError($"unknown edit op '{Required("op")}'");
        }
        created.Add(label);
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
