using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

static class WebUi
{
    internal static bool IsTraditional(IESpace m) => m.Kind.ToString().StartsWith("Web", StringComparison.OrdinalIgnoreCase);
}

partial class Builder
{
    void CreateWebScreen(ScreenSpec s, bool block)
    {
        var label = $"{(block ? "block" : "screen")} {s.Name}";
        var flow = m.WebFlows.FirstOrDefault(f => f.Name == s.Flow);
        if (flow is null)
        {
            flow = m.CreateWebFlow(s.Flow, services.NewKey());
            if (!Fresh(flow, s.Flow, $"flow {s.Flow}")) return;
            created.Add($"flow {s.Flow}");
        }
        if (flow.Nodes.FirstOrDefault(n => Model.NameOf(n) == s.Name) is { } found) { ReuseScreen(found, s, block, label); existing.Add(label); return; }
        object screen = block ? flow.CreateBlock(s.Name, services.NewKey()) : flow.CreateScreen(s.Name, services.NewKey());
        if (!Fresh(screen, s.Name, label)) return;
        created.Add(label);
        if (s.Public is { } isPublic) Set(screen, "Public", isPublic);
        ScreenParts(screen, s, label);
    }

    // Also for an existing Traditional Web screen or block listed again (ReuseScreen).
    static string? WebLifecycleProblem(ScreenSpec s) =>
        s.OnInitialize is not null || s.OnReady is not null || s.OnRender is not null || s.OnDestroy is not null
            ? "Traditional Web uses preparation instead of Reactive lifecycle events"
        : s.Events is { Count: > 0 } || s.OnParametersChanged is not null
            ? "events and onParametersChanged are supported for Reactive and Mobile blocks only" : null;

    void WebScreenParts(object screen, ScreenSpec s, string label)
    {
        if (s.Template is not null) throw new ModelError("templates are not supported for Traditional Web");
        if (s.Aggregates is { Count: > 0 } || s.DataActions is { Count: > 0 })
            throw new ModelError("Traditional Web uses preparation flow aggregate nodes instead of aggregates/dataActions");
        if (WebLifecycleProblem(s) is { } problem) throw new ModelError(problem);
        if (s.Title is { ValueKind: JsonValueKind.String } title)
            Defer(screen, ($"{label}.title", () => Reflect.Call(screen, "SetTitle", title.GetString())));
        Variables(screen, "CreateInputParameter", s.Inputs, label);
        Variables(screen, "CreateLocalVariable", s.Locals, label);
        // Adapted from OutSystems/outsystems11-mcp examples/AddWebScreenWithListRecords.cs (MIT).
        if (s.Preparation is not null)
        {
            var preparation = Reflect.Get(screen, "Preparation") ?? Reflect.Call(screen, "CreatePreparation")!;
            flows.Add(($"{label}/Preparation", () => Flow(preparation, s.Preparation, $"{label}/Preparation")));
        }
        var actions = new Dictionary<string, object>();
        foreach (var a in s.Actions ?? new())
        {
            var action = Reflect.Call(screen, "CreateScreenAction", a.Name, services.NewKey())!;
            if (!Fresh(action, a.Name, label)) continue;
            actions[a.Name] = action;
            Body(action, a, $"{label}/{a.Name}");
        }
        widgets.Add(($"{label}.widgets", () =>
        {
            if (s.Layout is null)
            {
                if (s.Placeholders is { Count: > 0 } || s.Title is { ValueKind: JsonValueKind.Array })
                    throw new ModelError("placeholder content requires a layout block");
                foreach (var w in s.Widgets ?? new()) WebWidget(screen, w, label, actions);
                return;
            }
            var content = new Dictionary<string, List<JsonElement>>(s.Placeholders ?? new(), StringComparer.OrdinalIgnoreCase);
            if (s.Widgets is not null) content.TryAdd("MainContent", s.Widgets);
            if (s.Title is { ValueKind: JsonValueKind.Array } items) content.TryAdd("Title", items.EnumerateArray().ToList());
            WebWidget(screen, JsonSerializer.SerializeToElement(new { type = "BlockInstance", sourceBlock = s.Layout, placeholders = content }), label, actions);
        }));
    }

    static Type WebWidgetType(string type) => Reflect.Types(typeof(IESpace).Assembly).FirstOrDefault(t =>
        t.IsInterface && t.Namespace == "OutSystems.Model.UI.Web.Widgets"
        && (t.Name.Equals($"I{type}Widget", StringComparison.OrdinalIgnoreCase)
            || t.Name.Equals($"IWeb{type}Widget", StringComparison.OrdinalIgnoreCase)))
        ?? throw new ModelError($"unknown Traditional Web widget '{type}' (see oml api \"OutSystems.Model.UI.Web.Widgets.I*\")");

    object? WebWidget(object parent, JsonElement w, string label, Dictionary<string, object> actions)
    {
        var type = w.GetProperty("type").GetString()!;
        var name = w.TryGetProperty("name", out var nm) ? nm.GetString() : null;
        var where = $"{label}/{name ?? type}";
        object? result = null;
        Try(where, () =>
        {
            var widget = type.ToLowerInvariant() switch
            {
                "headercell" or "rowcell" or "cell" => Reflect.Call(parent, "CreateCell", name, services.NewKey())!,
                "row" => Reflect.Call(parent, "CreateRow", name, services.NewKey())!,
                _ => Reflect.CallGeneric(parent, "CreateWidget", WebWidgetType(type.Equals("Form", StringComparison.OrdinalIgnoreCase) ? "EditRecord" : type), name, services.NewKey())!,
            };
            if (name is not null && !Fresh(widget, name, where)) return;
            result = widget;
            if (w.TryGetProperty("sourceBlock", out var sourceBlock))
                Queue(where, m, widget, "sourceBlock", sourceBlock, referencesDone: true);
            if (w.TryGetProperty("args", out var args))
                Defer(widget, ($"{where}.args", () => Arguments(w.TryGetProperty("onClick", out _)
                    ? Reflect.Get(widget, "OnClick") ?? throw new ModelError("widget has no OnClick event") : widget, args)));
            if (w.TryGetProperty("placeholders", out var content))
                foreach (var area in content.EnumerateObject())
                {
                    var slot = Elements(Reflect.Get(widget, "PlaceholdersContent")).FirstOrDefault(x =>
                        Reflect.Get(x, "Placeholder") is { } p && string.Equals(Model.NameOf(p), area.Name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ModelError($"unknown placeholder '{area.Name}'");
                    foreach (var child in area.Value.EnumerateArray()) WebWidget(slot, child, where, actions);
                }
            foreach (var p in w.EnumerateObject().Where(p => p.Name is not ("type" or "name" or "args" or "sourceBlock" or "placeholders")))
            {
                if (p.Value.ValueKind == JsonValueKind.Array)
                {
                    if (type.Equals("Button", StringComparison.OrdinalIgnoreCase) && p.Name == "children")
                    {
                        var children = p.Value.EnumerateArray().ToList();
                        if (children.Any(c => !string.Equals(c.GetProperty("type").GetString(), "Text", StringComparison.OrdinalIgnoreCase)))
                            throw new ModelError("Traditional Button children must be Text; use label for an expression");
                        var text = string.Concat(children.Select(c => c.GetProperty("text").GetString()));
                        Defer(widget, ($"{where}.label", () => Reflect.Call(widget, "SetLabel", "\"" + text.Replace("\"", "\"\"") + "\"")));
                        continue;
                    }
                    var container = p.Name switch
                    {
                        "children" => widget,
                        "rows" when Reflect.Has(widget, "CreateRow") => widget,
                        "cells" when Reflect.Has(widget, "CreateCell") => widget,
                        "row" when type.Equals("TableRecords", StringComparison.OrdinalIgnoreCase) => Reflect.Get(widget, "DataRow"),
                        _ => Reflect.Get(widget, p.Name),
                    } ?? throw new ModelError($"no content area '{p.Name}' on this build");
                    foreach (var child in p.Value.EnumerateArray()) WebWidget(container, child, where, actions);
                }
                else if (p.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                {
                    var destination = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()!
                        : p.Value.GetProperty("destination").GetString()!;
                    Event(widget, p.Name, actions.GetValueOrDefault(destination)
                        ?? Refs.Find(m, destination, typeof(OutSystems.Model.UI.IScreenSignature)));
                    if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("args", out var eventArgs))
                        Defer(widget, ($"{where}.{p.Name}.args", () => Arguments(Reflect.Get(widget, p.Name)!, eventArgs)));
                }
                else
                {
                    // Web styles are plain CSS classes, unlike Reactive expression properties.
                    if (p.Name == "style")
                    {
                        var style = p.Value.GetString()!;
                        if (style.Length >= 2 && style[0] == '"' && style[^1] == '"')
                            style = style[1..^1].Replace("\"\"", "\"");
                        Set(widget, "StyleClasses", style);
                        continue;
                    }
                    var key = p.Name switch
                    {
                        "source" => Reflect.Has(widget, "SetSourceRecordList") ? "sourceRecordList" : "sourceRecord",
                        _ => p.Name,
                    };
                    Queue(where, m, widget, key, p.Value, referencesDone: true);
                }
            }
        });
        return result;
    }
}
