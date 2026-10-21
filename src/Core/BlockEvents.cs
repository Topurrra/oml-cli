using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Logic.Nodes;
using OutSystems.Model.UI;

namespace OmlCli;

record EventSpec(string Name, bool Mandatory = true, List<VarSpec>? Inputs = null, string? Description = null);

// Block events: a block declares events, fires them with a Trigger Event node in its own client actions,
// and the screen or block that uses it handles them on the block instance (Event + Handler + arguments).
partial class Builder
{
    void BlockEvents(object block, List<EventSpec>? events, string label)
    {
        if (events is not { Count: > 0 }) return;
        if (!Inspect.IsBlock(block)) throw new ModelError("events are only for blocks; a screen cannot fire events");
        if (events.GroupBy(e => e.Name).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new ModelError($"event '{twice.Key}' is listed more than once; give each event of a block a unique name");
        // Rule 4: consumers of an existing public block break on any new event; existing instances of any
        // existing block have no handler, so a new event there must be optional.
        var existed = rules.Existed(block);
        if (rules.WasPublic(block))
            throw new ModelError($"refused: '{Model.NameOf(block)}' is an existing public block. A new event changes its signature and can break the modules that use it; create a new block instead, or add the event by hand in Service Studio.");
        foreach (var e in events)
        {
            var where = $"{label}.events.{e.Name}";
            if (Elements(Reflect.Get(block, "Events")).Any(x => Model.NameOf(x) == e.Name)) { existing.Add($"event {Model.NameOf(block)}.{e.Name}"); continue; }
            if (existed && e.Mandatory)
            { problems.Add($"{where}: refused: '{Model.NameOf(block)}' is an existing block, and its existing instances have no handler for a new mandatory event; add it with \"mandatory\": false"); continue; }
            var evt = Reflect.Call(block, "CreateEvent", e.Name, services.NewKey())!;
            if (!Fresh(evt, e.Name, where)) continue;
            Reflect.Property(evt, "IsMandatory", writable: true)!.SetValue(evt, e.Mandatory);
            if (e.Description is not null) Reflect.Property(evt, "Description", writable: true)!.SetValue(evt, e.Description);
            Variables(evt, "CreateInputParameter", e.Inputs, where);
            created.Add($"event {Model.NameOf(block)}.{e.Name}");
        }
    }

    // A Trigger Event node fires an event of the block that owns the action. Checked before the node is created,
    // because the model refuses the node elsewhere with no useful message.
    static object TriggerBlock(object action) =>
        action is IObjectSignature { Parent: { } parent } && Inspect.IsBlock(parent)
        && Elements(Reflect.Get(parent, "ScreenActions")).Contains(action) ? parent
        : throw new ModelError("triggerEvent is only allowed in a client action of a block; it fires one of that block's events");

    static void TriggerEvent(object action, object node, JsonElement spec)
    {
        var block = TriggerBlock(action);
        var events = Elements(Reflect.Get(block, "Events")).ToList();
        var known = events.Count == 0 ? $"block '{Model.NameOf(block)}' has no events; add them with \"events\""
            : $"events of block '{Model.NameOf(block)}': {string.Join(", ", events.Select(Model.NameOf))}";
        if (!spec.TryGetProperty("event", out var name) || name.ValueKind != JsonValueKind.String)
            throw new ModelError($"triggerEvent requires event ({known})");
        var evt = events.FirstOrDefault(x => Model.NameOf(x) == name.GetString()) ?? throw new ModelError($"no event '{name.GetString()}' ({known})");
        Reflect.Property(node, "Event", writable: true)!.SetValue(node, evt);
    }

    // "events": { "OnSelect": "Handler" } or { "OnSelect": { "action": "Handler", "args": { "Id": "Id" } } } on a block instance.
    // A handler input with no arg gets the event input of the same name. Mandatory events must be handled.
    void BlockInstanceEvents(object widget, JsonElement w, string where, Dictionary<string, object> screenActions)
    {
        var handlers = Elements(Reflect.Get(widget, "EventHandlers")).ToList();
        var block = Reflect.Get(widget, "SourceBlock");
        string Known() => handlers.Count == 0 ? $"block '{Model.NameOf(block!)}' has no events"
            : $"events of block '{Model.NameOf(block!)}': {string.Join(", ", handlers.Select(h => Model.NameOf(Reflect.Get(h, "Event")!)))}";
        if (w.TryGetProperty("events", out var events))
        {
            if (block is null) throw new ModelError("events need a block instance with sourceBlock");
            if (events.ValueKind != JsonValueKind.Object) throw new ModelError("events must be an object: { \"<Event>\": \"<screen action>\" }");
            foreach (var e in events.EnumerateObject()) Try($"{where}.events.{e.Name}", () =>
            {
                var handler = handlers.FirstOrDefault(h => Model.NameOf(Reflect.Get(h, "Event")!) == e.Name)
                    ?? throw new ModelError($"no event '{e.Name}' ({Known()})");
                var (actionName, args) = e.Value.ValueKind switch
                {
                    JsonValueKind.String => (e.Value.GetString()!, (JsonElement?)null),
                    JsonValueKind.Object when e.Value.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
                        => (a.GetString()!, e.Value.TryGetProperty("args", out var given) ? given : (JsonElement?)null),
                    _ => throw new ModelError("a handler is a screen action name, or { \"action\": \"<screen action>\", \"args\": { ... } }"),
                };
                var action = screenActions.GetValueOrDefault(actionName)
                    ?? throw new ModelError($"no screen action '{actionName}' in this screen or block; the handler must be one of its client actions");
                // Set before the input check, so a mismatch is reported once and not also as a missing mandatory handler.
                Reflect.Property(handler, "Handler", writable: true)!.SetValue(handler, action);
                var eventInputs = Elements(Reflect.Get(Reflect.Get(handler, "Event")!, "InputParameters")).Select(Model.NameOf).ToHashSet();
                var values = new Dictionary<string, JsonElement>();
                if (args is { ValueKind: JsonValueKind.Object } explicitArgs)
                    foreach (var a in explicitArgs.EnumerateObject()) values[a.Name] = a.Value;
                var unmatched = new List<string>();
                foreach (var input in Elements(Reflect.Get(action, "InputParameters")).Select(p => Model.NameOf(p)!))
                    if (!values.ContainsKey(input))
                    {
                        if (eventInputs.Contains(input)) values[input] = JsonSerializer.SerializeToElement(input);
                        else unmatched.Add(input);
                    }
                if (unmatched.Count > 0)
                    throw new ModelError($"handler '{actionName}' has input {string.Join(", ", unmatched.Select(u => $"'{u}'"))} that event '{e.Name}' does not have"
                        + $" (event inputs: {(eventInputs.Count == 0 ? "none" : string.Join(", ", eventInputs))}); rename the input or give it in \"args\"");
                if (values.Count > 0) Defer(widget, ($"{where}.events.{e.Name}.args", () => Arguments(handler, JsonSerializer.SerializeToElement(values))));
            });
        }
        foreach (var h in handlers)
            if (Reflect.Get(h, "Event") is { } evt && Reflect.Get(evt, "IsMandatory") is true && Reflect.Get(h, "Handler") is null)
                problems.Add($"{where}: event '{Model.NameOf(evt)}' of block '{Model.NameOf(block!)}' is mandatory; handle it with \"events\": {{ \"{Model.NameOf(evt)}\": \"<screen action>\" }}");
    }
}
