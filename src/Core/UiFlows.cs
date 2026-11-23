using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

// A UI flow's own nodes, besides screens, blocks, emails and External Sites: comments (any module kind) and, in Traditional Web,
// entries (Service Studio: an Entry, a named way into the flow that goes to a screen; the default one is the module's start).
//   { "uiFlows": [{ "name": "MainFlow", "comments": ["Public pages", { "text": "Fix before release", "isReminder": true }],
//       "entries": [{ "name": "Start", "screen": "Home", "isDefault": true }] }] }
// Listed again, a comment with the same text and an entry with the same name are the ones there (an entry is updated).
partial class Builder
{
    // Traditional Web: "isDefault": true on a screen. The module starts at the default Entry of a UI flow, never at
    // DefaultScreen (the Traditional compiler fails on a module that has one). The screen's own flow gets an Entry that goes
    // to it (an existing one, else "Start" or <Screen>Entry), and it becomes the module's only default Entry. A DefaultScreen
    // an older oml set is cleared, so applying the spec again repairs such a module.
    void DefaultEntry(object screen, string label)
    {
        var flow = m.WebFlows.FirstOrDefault(f => Elements(Reflect.Get(f, "Nodes")).Any(n => ReferenceEquals(n, screen)))
            ?? throw new ModelError($"{label}: isDefault needs the screen in a UI flow");
        var nodes = Elements(Reflect.Get(flow, "Nodes")).ToList();
        var entry = nodes.OfType<OutSystems.Model.UI.Web.IEntry>().FirstOrDefault(e => ReferenceEquals(e.Target, screen));
        if (entry is null)
        {
            var name = nodes.Any(n => Model.NameOf(n) == "Start") ? Model.NameOf(screen) + "Entry" : "Start";
            entry = (OutSystems.Model.UI.Web.IEntry)Reflect.CallGeneric(flow, "CreateNode", typeof(OutSystems.Model.UI.Web.IEntry), name, services.NewKey())!;
            entry.Target = (OutSystems.Model.UI.IUIFlowNode)screen;
            created.Add($"entry {Model.NameOf(entry)} -> {Model.NameOf(screen)}");
        }
        foreach (var other in m.WebFlows.SelectMany(f => Elements(Reflect.Get(f, "Nodes")).OfType<OutSystems.Model.UI.Web.IEntry>()))
            if (!ReferenceEquals(other, entry) && other.IsDefault) other.IsDefault = false;
        entry.IsDefault = true;
        if (Reflect.Property(m, "DefaultScreen", writable: true) is { } defaultScreen && defaultScreen.GetValue(m) is not null) defaultScreen.SetValue(m, null);
    }

    // Is the screen already the start of this Traditional module (a default Entry that goes to it)?
    bool IsDefaultEntry(object screen) =>
        m.WebFlows.SelectMany(f => Elements(Reflect.Get(f, "Nodes")).OfType<OutSystems.Model.UI.Web.IEntry>())
            .Any(e => e.IsDefault && ReferenceEquals(e.Target, screen));

    public void UiFlows(List<JsonElement>? flows)
    {
        foreach (var f in flows ?? new()) Try($"uiFlow {RequiredName(f)}", () =>
        {
            var name = RequiredName(f);
            var label = $"uiFlow {name}";
            var web = WebUi.IsTraditional(m);
            object flow = web
                ? m.WebFlows.FirstOrDefault(x => x.Name == name) ?? (object)m.CreateWebFlow(name, services.NewKey())
                : m.MobileFlows.FirstOrDefault(x => x.Name == name) ?? (object)NewMobileFlow(name);
            var nodes = Elements(Reflect.Get(flow, "Nodes")).ToList();
            foreach (var p in f.EnumerateObject().Where(p => p.Name is not ("name" or "comments" or "entries")))
                throw new ModelError($"unknown key '{p.Name}' (a UI flow takes comments and entries)");

            var commentType = web ? typeof(OutSystems.Model.UI.Web.IComment) : typeof(OutSystems.Model.UI.Mobile.IComment);
            foreach (var c in f.TryGetProperty("comments", out var comments) ? comments.EnumerateArray() : Enumerable.Empty<JsonElement>())
            {
                var text = c.ValueKind == JsonValueKind.String ? c.GetString()!
                    : c.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : throw new ModelError("a comment is its text, or { \"text\": ..., \"isReminder\": true }");
                var comment = nodes.FirstOrDefault(n => commentType.IsInstanceOfType(n) && Reflect.Get(n, "Text") as string == text);
                if (comment is null)
                {
                    comment = Reflect.CallGeneric(flow, "CreateNode", commentType, "Comment", services.NewKey())!;
                    Reflect.Property(comment, "Text", writable: true)!.SetValue(comment, text);
                    created.Add($"{label} comment");
                }
                if (c.ValueKind == JsonValueKind.Object)
                    foreach (var p in c.EnumerateObject().Where(p => p.Name != "text")) Queue($"{label} comment", m, comment, p.Name, p.Value);
            }

            if (f.TryGetProperty("entries", out var entries))
            {
                if (!web) throw new ModelError("entries are a Traditional Web UI flow node; a Reactive app starts at its default screen (\"isDefault\": true on the screen)");
                foreach (var e in entries.EnumerateArray())
                {
                    var entryName = RequiredName(e);
                    var where = $"{label}/{entryName}";
                    var entry = nodes.OfType<OutSystems.Model.UI.Web.IEntry>().FirstOrDefault(n => n.Name == entryName);
                    if (entry is null)
                    {
                        entry = (OutSystems.Model.UI.Web.IEntry)Reflect.CallGeneric(flow, "CreateNode", typeof(OutSystems.Model.UI.Web.IEntry), entryName, services.NewKey())!;
                        if (!Fresh(entry, entryName, where)) continue;
                        created.Add($"entry {entryName}");
                    }
                    else existing.Add($"entry {entryName}");
                    if (e.TryGetProperty("screen", out var screen))
                    {
                        var target = Refs.Find(m, screen.GetString()!, typeof(OutSystems.Model.UI.IScreenSignature)) as OutSystems.Model.UI.IUIFlowNode
                            ?? throw new ModelError($"{where}: an entry goes to a screen of this module; '{screen.GetString()}' is not one");
                        entry.Target = target;
                    }
                    else if (entry.Target is null) throw new ModelError($"{where}: an entry takes screen: the screen it goes to");
                    foreach (var p in e.EnumerateObject().Where(p => p.Name is not ("name" or "screen"))) Queue(where, m, entry, p.Name, p.Value);
                }
            }
        });
    }
}
