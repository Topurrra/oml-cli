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
        s = s with { Flow = FlowFor(Flows(m, web: true), s.Name, n => IsScreenOrBlock(n, block), s.Flow, "MainFlow", label) };
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
            var content = new Dictionary<string, List<JsonElement>>(s.Placeholders ?? new(), StringComparer.OrdinalIgnoreCase);
            if (s.Widgets is not null) content.TryAdd("MainContent", s.Widgets);
            if (s.Title is { ValueKind: JsonValueKind.Array } items) content.TryAdd("Title", items.EnumerateArray().ToList());
            if (s.Layout is null)
            {
                // A new Traditional screen comes with the module's default layout (LayoutTopMenu with header, menu and login).
                // Widgets go into its MainContent, as Service Studio does when you drop them on the screen; beside the layout
                // instance they are not shown (only the header was).
                var seeded = Elements(Reflect.Get(screen, "Widgets")).FirstOrDefault(x => Elements(Reflect.Get(x, "PlaceholdersContent")).Any());
                if (seeded is not null && content.Count > 0)
                {
                    foreach (var (area, children) in content)
                    {
                        var slot = Elements(Reflect.Get(seeded, "PlaceholdersContent")).FirstOrDefault(x =>
                            Reflect.Get(x, "Placeholder") is { } p && string.Equals(Model.NameOf(p), area, StringComparison.OrdinalIgnoreCase))
                            ?? throw new ModelError($"unknown placeholder '{area}' in the screen's layout {Model.NameOf(Reflect.Get(seeded, "SourceBlock") ?? seeded)}");
                        foreach (var child in children) WebWidget(slot, child, label, actions);
                    }
                    return;
                }
                if (s.Placeholders is { Count: > 0 } || s.Title is { ValueKind: JsonValueKind.Array })
                    throw new ModelError("placeholder content requires a layout block");
                foreach (var w in s.Widgets ?? new()) WebWidget(screen, w, label, actions);
                return;
            }
            WebWidget(screen, JsonSerializer.SerializeToElement(new { type = "BlockInstance", sourceBlock = s.Layout, placeholders = content }), label, actions);
        }));
    }

    // Traditional widgets without a public model interface. Image and List Box are Service Studio's own widget classes (made as its
    // toolbox makes them: the class's constructor adds the widget to its parent); Label and Editable Table come from the Widgets plugin.
    static readonly string[] BuiltinWebWidgets = ["Image", "ListBox"];
    static readonly string[] PluginWebWidgets = ["Label", "EditableTable", "EditableTableCell"];

    static object? OtherWebWidget(object parent, string type, string? name)
    {
        if (BuiltinWebWidgets.FirstOrDefault(t => t.Equals(type, StringComparison.OrdinalIgnoreCase)) is { } builtin)
        {
            var cls = parent.GetType().Assembly.GetType($"ServiceStudio.Model.WebWidgets+{builtin}")
                ?? throw new ModelError($"this Service Studio build has no Traditional {builtin} widget");
            return Construct(cls, parent, name, builtin);
        }
        if (PluginWebWidgets.FirstOrDefault(t => t.Equals(type, StringComparison.OrdinalIgnoreCase)) is { } plugin)
        {
            var descriptor = ServiceStudio.PluginAPI.PluginProvider.Implementations.FirstOrDefault(d =>
                d.GetType().Name == plugin + "Descriptor" && d.GetType().Namespace == "ServiceStudio.Plugin.Widgets")
                ?? throw new ModelError($"the Widgets plugin with {plugin} is not loaded on this build");
            // A Traditional screen does not make plugin widgets itself (its CreateWidget throws); Service Studio's own helper does.
            var helper = parent.GetType().Assembly.GetType("ServiceStudio.Model.PluginExtensions")?.GetMethods()
                .FirstOrDefault(mi => mi.Name == "CreateWidget" && mi.GetParameters().Length == 3)
                ?? throw new ModelError("this Service Studio build cannot make plugin widgets in Traditional Web");
            try { return helper.Invoke(null, [parent, descriptor, name])!; }
            catch (System.Reflection.TargetInvocationException e) { throw new ModelError($"{plugin}: {(e.InnerException ?? e).Message}"); }
        }
        return null;
    }

    // A property of a widget without a public model interface that names a model element (an image, an attribute of the source
    // record list, ...): no Set<Key>(string) for an expression and no plain value (text, number, flag, list value).
    static bool Picked(object widget, string key)
    {
        var property = char.ToUpperInvariant(key[0]) + key[1..];
        if (Reflect.Has(widget, "Set" + property)) return false;
        var type = widget.GetType().GetProperty(property)?.PropertyType;
        return type is not null && !(type == typeof(string) || type == typeof(bool) || type == typeof(int) || type.IsEnum);
    }

    // Sets a reference property the way Service Studio's properties panel does: from the values the property offers (its
    // dropdown), the one whose text is `text` (an image name, Entity.Attribute of the source record list, ...).
    static void PanelPick(object o, string key, string text)
    {
        var property = char.ToUpperInvariant(key[0]) + key[1..];
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy;
        var descriptor = o.GetType().GetField(property + "PropertyDescriptor", all)?.GetValue(null)
            ?? o.GetType().GetProperty(property + "PropertyDescriptor", all)?.GetValue(null)
            ?? throw new ModelError($"{ElementPaths.Kind(o)} has no property '{key}'");
        var get = descriptor.GetType().GetMethods().First(mi => mi.Name == "GetPredefinedValues" && mi.GetParameters().Length == 1);
        var values = ((System.Collections.IEnumerable)get.Invoke(descriptor, [o])!).Cast<object>()
            .Where(v => Field(v, "IsRealValue") is true && Field(v, "IsCategory") is not true).ToList();
        static object? Field(object v, string name) => v.GetType().GetField(name)?.GetValue(v);
        var hits = values.Where(v => string.Equals(Field(v, "StringRepresentation") as string, text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count != 1)
        {
            var offered = values.Select(v => Field(v, "StringRepresentation") as string).Where(s => !string.IsNullOrEmpty(s)).Distinct().Take(25).ToList();
            throw new ModelError(hits.Count == 0
                ? $"{key}: '{text}' is not one of the values Service Studio offers here{(offered.Count > 0 ? ": " + string.Join(", ", offered) : " (it offers none: the source is missing, or has no attribute of the needed type)")}"
                : $"{key}: '{text}' matches {hits.Count} values");
        }
        var set = descriptor.GetType().GetMethods().First(mi => mi.Name == "SetValue" && mi.GetParameters().Length == 2 && mi.GetParameters()[1].ParameterType == typeof(object));
        try { set.Invoke(descriptor, [o, Field(hits[0], "Object")]); }
        catch (System.Reflection.TargetInvocationException e) { throw new ModelError($"{key}: {(e.InnerException ?? e).Message}"); }
        if (o.GetType().GetProperty(property)?.GetValue(o) is null) throw new ModelError($"{key}: Service Studio did not take '{text}'");
    }

    // A Traditional plugin widget's placeholder by name ("children": its only or "content" placeholder).
    static object? PluginSlot(object widget, string key)
    {
        var slots = Elements(Reflect.Get(widget, "ChildPlaceholders")).ToList();
        return key == "children"
            ? slots.FirstOrDefault(x => string.Equals(Model.NameOf(x), "content", StringComparison.OrdinalIgnoreCase)) ?? (slots.Count == 1 ? slots[0] : null)
            : slots.FirstOrDefault(x => string.Equals(Model.NameOf(x), key, StringComparison.OrdinalIgnoreCase));
    }

    // Service Studio's widget class `cls` made in `parent` by its (parent, name) constructor, as its toolbox does.
    static object Construct(Type cls, object parent, string? name, string what)
    {
        var ctor = cls.GetConstructors().FirstOrDefault(c => c.GetParameters() is [{ } p0, { } p1] && p1.ParameterType == typeof(string) && p0.ParameterType.IsInstanceOfType(parent))
            ?? throw new ModelError($"a {what} cannot go in {ElementPaths.Kind(parent)}");
        try { return ctor.Invoke([parent, name]); }
        catch (System.Reflection.TargetInvocationException e) { throw new ModelError($"{what}: {(e.InnerException ?? e).Message}"); }
    }

    // A widget of a public type in a parent without the public factory (a plugin widget's placeholder, such as a Label's content).
    static object PublicWebWidget(object parent, Type type, string? name)
    {
        if (Reflect.HasGeneric(parent, "CreateWidget", 2))
            return Reflect.CallGeneric(parent, "CreateWidget", type, name, Model.Services().NewKey())!;
        var framework = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(IESpace).Assembly)!.Assemblies.First(a => a.GetName().Name == "ServiceStudio.Framework");
        var cls = framework.GetTypes().FirstOrDefault(t => t.IsClass && !t.IsAbstract && t.FullName!.StartsWith("ServiceStudio.Model.WebWidgets+", StringComparison.Ordinal)
                && !t.Name.StartsWith("Reference", StringComparison.Ordinal) && type.IsAssignableFrom(t))
            ?? throw new ModelError($"no Service Studio class makes a {type.Name} in {parent.GetType().FullName}");
        return Construct(cls, parent, name, cls.Name);
    }

    static Type WebWidgetType(string type) => Reflect.Types(typeof(IESpace).Assembly).FirstOrDefault(t =>
        t.IsInterface && t.Namespace == "OutSystems.Model.UI.Web.Widgets"
        && (t.Name.Equals($"I{type}Widget", StringComparison.OrdinalIgnoreCase)
            || t.Name.Equals($"IWeb{type}Widget", StringComparison.OrdinalIgnoreCase)))
        ?? throw new ModelError($"unknown Traditional Web widget '{type}' (see oml model-types \"OutSystems.Model.UI.Web.Widgets.I*\")");

    object? WebWidget(object parent, JsonElement w, string label, Dictionary<string, object> actions)
    {
        var type = w.GetProperty("type").GetString()!;
        var name = w.TryGetProperty("name", out var nm) ? nm.GetString() : null;
        var where = $"{label}/{name ?? type}";
        object? result = null;
        Try(where, () =>
        {
            var other = false;   // a widget without a public model interface: its references are picked as the properties panel does
            object Other(object made) { other = true; return made; }
            var widget = type.ToLowerInvariant() switch
            {
                "headercell" or "rowcell" or "cell" => Reflect.Call(parent, "CreateCell", name, services.NewKey())!,
                "row" => Reflect.Call(parent, "CreateRow", name, services.NewKey())!,
                _ => OtherWebWidget(parent, type, name) is { } made ? Other(made) : PublicWebWidget(parent, WebWidgetType(type.ToLowerInvariant() switch
                    { "form" => "EditRecord", "upload" => "InputFilename", "textarea" => throw new ModelError("Traditional Web has no TextArea widget: use an Input with \"textLines\" above 1 (Service Studio: Text Area)"), _ => type }), name),
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
                    if (type.Equals("Label", StringComparison.OrdinalIgnoreCase) && p.Name == "children")
                        throw new ModelError("a Traditional Label shows an expression: use \"value\" (for example \"\\\"Name\\\"\") instead of children");
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
                        _ when other && PluginSlot(widget, p.Name) is { } slot => slot,   // a plugin widget's placeholder: Editable Table headerCells, bodyCells; a cell's children
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
                        ?? Refs.Destination(m, destination));
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
                        "image" when other => "actualImage",
                        "imageType" when other => "type",
                        _ => p.Name,
                    };
                    if (key == "targetWidget" && p.Value.ValueKind == JsonValueKind.String)
                    {
                        var targetName = p.Value.GetString()!;
                        Defer(widget, ($"{where}.targetWidget", () => TargetWidget(widget, targetName)));
                        continue;
                    }
                    // A Combo Box's Source Entity (its model name is SourceAttributeParent) and attributes are picked as its properties panel does.
                    var comboPick = widget.GetType().Name == "ComboBox" && key is "sourceEntity" or "sourceAttribute" or "sourceIdentifierAttribute";
                    if ((other || comboPick) && p.Value.ValueKind == JsonValueKind.String && (comboPick || Picked(widget, key)))
                    {
                        var text = p.Value.GetString()!;
                        var pickKey = key == "sourceEntity" && comboPick ? "sourceAttributeParent" : key;
                        picks.Add(($"{where}.{p.Name}", () => PanelPick(widget, pickKey, text)));
                        continue;
                    }
                    // A Traditional Button or Link keeps its confirmation text on its On Click event, not on the widget.
                    var target = key.Equals("confirmationMessage", StringComparison.OrdinalIgnoreCase) && Reflect.Get(widget, "OnClick") is { } onClick ? onClick : widget;
                    Queue(where, m, target, key, p.Value, referencesDone: true);
                }
            }
        });
        return result;
    }
}
