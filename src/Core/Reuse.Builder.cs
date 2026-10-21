using System.Text.Json;
using System.Text.Json.Nodes;
using OutSystems.Model;

namespace OmlCli;

// Listed elements that already exist (see Reuse): actions, screens, blocks, platform elements, REST, emails, variables.
// Rule 4 for inputs and outputs is checked once, in ProductionRules.Validate, whatever path made the change.
partial class Builder
{
    // The spec's JSON, to tell keys the spec sets from record defaults (Reuse.Item).
    public JsonNode? Raw { get; init; }
    // What a creation list changed on an existing element, for the report's `changed`: lifecycle handlers it did not
    // set yet ("onParametersChanged of block Badge -> SetX") and UI settings replaced (rule 3 allows UI changes).
    public List<string> Changed { get; } = new();
    void Change(string what) { if (!Changed.Contains(what)) Changed.Add(what); }
    readonly HashSet<object> replacedAt = new(ReferenceEqualityComparer.Instance); // the elements replaceWidgets changed

    // serverActions, serviceActions, clientActions.
    void ReuseAction(string kind, object action, ActionSpec s)
    {
        var label = $"{kind} {s.Name}";
        var raw = Reuse.Item(Raw, kind + "s", s.Name);
        SetPublic(action, s.Public);
        if (s.Function && Reflect.Get(action, "Function") is false)
            problems.Add(Reuse.Changed(label, ["function"], "Change it by hand in Service Studio, or create a new action."));
        if (s.Description is not null && Reflect.Property(action, "Description", writable: true) is { } d) d.SetValue(action, s.Description);
        ReuseVariables(action, label, raw, ("inputs", "InputParameters", "CreateInputParameter", s.Inputs),
            ("outputs", "OutputParameters", "CreateOutputParameter", s.Outputs), ("locals", "LocalVariables", "CreateLocalVariable", s.Locals));
        ReuseFlow(action, s.Flow, label);
    }

    // As for a new element; ProductionRules.Validate refuses public to private (rule 4) and lists a server action made public (rule 7).
    static void SetPublic(object element, bool? value)
    {
        if (value is { } isPublic) Reflect.Property(element, "Public", writable: true)?.SetValue(element, isPublic);
    }

    // New inputs, outputs and locals are added as addVariables adds them, and an input's mandatory setting is set
    // as the spec says; rule 4 then refuses what would break a public element. A changed type is a problem.
    void ReuseVariables(object owner, string label, JsonObject? raw, params (string key, string collection, string factory, List<VarSpec>? vars)[] groups)
    {
        foreach (var (key, collection, factory, vars) in groups)
        {
            var fresh = new List<VarSpec>();
            foreach (var v in vars ?? new())
            {
                var old = Elements(Reflect.Get(owner, collection)).FirstOrDefault(x => Model.NameOf(x) == v.Name);
                if (old is null) { fresh.Add(v); continue; }
                var oldType = Reflect.Get(old, "DataType") as OutSystems.Model.Types.ITypeSignature;
                if (Reuse.Has(Reuse.Item(raw, key, v.Name), "type") && Model.Resolve(m, v.Type, new List<string>(), label) is { } type
                    && Model.Describe(m, type) != Model.Describe(m, oldType))
                    problems.Add(Reuse.Changed($"{label}.{key}.{v.Name}", [$"type ({v.Type}; module: {Model.Describe(m, oldType)})"],
                        "Add a new input or output instead, or change the type by hand in Service Studio."));
                if (key == "inputs" && v.Mandatory is { } mandatory && Reflect.Get(old, "IsMandatory") is bool isMandatory && isMandatory != mandatory)
                    Reflect.Property(old, "IsMandatory", writable: true)?.SetValue(old, mandatory);
                existing.Add($"{key[..^1]} {label}.{v.Name}");
            }
            if (fresh.Count == 0) continue;
            Variables(owner, factory, fresh, label);
            created.AddRange(fresh.Select(v => $"{key[..^1]} {label}.{v.Name}"));
        }
    }

    static string Lower(bool value) => value ? "true" : "false";

    // An existing flow is not rebuilt. A spec flow with another number of nodes, or naming a node the flow lacks,
    // is a problem. Comments do not count. Some nodes have no name in the model (Raise Exception, Refresh Data,
    // Download): as many spec names as there are such nodes may go unmatched.
    void ReuseFlow(object action, List<JsonElement>? flow, string label)
    {
        if (flow is not { Count: > 0 }) return;
        var nodes = Elements(Reflect.Get(action, "Nodes"))
            .Where(n => n is not OutSystems.Model.Logic.Nodes.IStartNode and not OutSystems.Model.Logic.Nodes.IEndNode
                && !n.GetType().GetInterfaces().Any(i => i.Name == "IComment")).ToList();
        var names = nodes.Select(n => Model.NameOf(n)).Where(n => n is not null).ToHashSet();
        var unnamed = nodes.Count(n => Model.NameOf(n) is null);
        var given = flow.Where(n => !(n.TryGetProperty("kind", out var k) && k.GetString() is "start" or "end" or "comment")).ToList();
        var missing = given.Select(n => n.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null)
            .Where(n => n is not null && !names.Contains(n)).ToList();
        var what = given.Count != nodes.Count ? $"flow ({given.Count} nodes; module: {nodes.Count})"
            : missing.Count > unnamed ? $"flow (node {missing[0]} is not in it)" : null;
        if (what is not null) problems.Add(Reuse.Changed(label, [what], "Change an existing flow with edits: insertNodes, set, delete."));
    }

    // Screens and blocks: settings must match; new named parts go through the addParts path (rule 4 for events
    // included). A different styleSheet or text title is replaced and missing requiredScripts are added (UI, rule 3);
    // widgets, placeholders, layout and preparation are not compared and are named in the report's warnings.
    void ReuseScreen(object screen, ScreenSpec s, bool block, string label)
    {
        var raw = Reuse.Item(Raw, block ? "blocks" : "screens", s.Name);
        if (WebUi.IsTraditional(m) && WebLifecycleProblem(s) is { } web) { problems.Add($"{label}: {web}"); return; }
        SetPublic(screen, s.Public);
        var changed = new List<string>();
        var wire = new Dictionary<string, string>(); // lifecycle handlers the module does not set yet: new logic, set as addParts sets them
        foreach (var (key, evt, handler) in new[] { ("onInitialize", "OnInitialize", s.OnInitialize), ("onReady", "OnReady", s.OnReady), ("onRender", "OnRender", s.OnRender),
                     ("onDestroy", "OnDestroy", s.OnDestroy), ("onParametersChanged", "OnParametersChanged", s.OnParametersChanged) })
            if (handler is not null && Reflect.Property(screen, evt) is not null)
            {
                var now = Reflect.Get(screen, evt) is { } lifecycle && Reflect.Get(lifecycle, "Destination") is { } target ? Model.NameOf(target) : null;
                if (now is null) { wire[evt] = handler; Change($"{key} of {label} -> {handler}"); }
                else if (now != handler) changed.Add(key);
            }
        if (s.Access is { } access && !SameAccess(screen, access)) changed.Add("access");
        if (s.IsDefault && !ReferenceEquals(Reflect.Get(m, "DefaultScreen"), screen)) changed.Add("isDefault");
        if (changed.Count > 0) problems.Add(Reuse.Changed(label, changed, "Change it with edits: set (lifecycle handlers, default screen), setAccess."));
        if (s.StyleSheet is not null && Reuse.Css(Reflect.Get(screen, "StyleSheet") as string) != Reuse.Css(s.StyleSheet))
        {
            Queue(label, m, screen, "StyleSheet", JsonSerializer.SerializeToElement(s.StyleSheet));
            Change($"styleSheet of {label}");
        }
        if (s.Title is { ValueKind: JsonValueKind.String } title && Reuse.Has(raw, "title")
            && Reuse.Norm(Preview.Text(Reflect.Get(screen, "Title"))) != Reuse.Norm(title.GetString()))
        {
            Defer(screen, ($"{label}.title", () => Reflect.Call(screen, "SetTitle", title.GetString())));
            Change($"title of {label}");
        }
        if (s.RequiredScripts is { Count: > 0 })
            Try($"{label}.requiredScripts", () => { if (OmlCli.Content.RequireScripts(m, screen, s.RequiredScripts) > 0) Change($"requiredScripts of {label}"); });

        ReuseVariables(screen, label, raw, ("inputs", "InputParameters", "CreateInputParameter", s.Inputs), ("locals", "LocalVariables", "CreateLocalVariable", s.Locals));
        var path = Model.NameOf(screen);
        var at = ElementPaths.PathTo(m, (IObjectSignature)screen) ?? path; // for the hints below: a path edits accept
        var actions = new List<ActionSpec>();
        foreach (var a in s.Actions ?? new())
            if (Elements(Reflect.Get(screen, "ScreenActions")).FirstOrDefault(x => Model.NameOf(x) == a.Name) is { } old)
            { ReuseFlow(old, a.Flow, $"{label}/{a.Name}"); existing.Add($"screenAction {path}/{a.Name}"); }
            else actions.Add(a);
        var dataActions = new List<DataActionSpec>();
        foreach (var d in s.DataActions ?? new())
            if (Elements(Reflect.Get(screen, "DataActions")).FirstOrDefault(x => Model.NameOf(x) == d.Name) is { } old)
            { ReuseFlow(old, d.Flow, $"{label}/{d.Name}"); existing.Add($"dataAction {path}/{d.Name}"); }
            else dataActions.Add(d);
        var oldAggregates = Elements(Reflect.Get(screen, "ScreenAggregates")).ToDictionary(x => Model.NameOf(x) ?? "", x => x);
        var aggregates = new List<JsonElement>();
        foreach (var a in s.Aggregates ?? new())
            if (oldAggregates.TryGetValue(a.GetProperty("name").GetString()!, out var old)) ReuseAggregate(old, a, $"{label}/{Model.NameOf(old)}");
            else aggregates.Add(a);
        var events = new List<EventSpec>();
        foreach (var e in s.Events ?? new())
            if (Elements(Reflect.Get(screen, "Events")).FirstOrDefault(x => Model.NameOf(x) == e.Name) is { } old)
            {
                // Making an event optional is safe; making one mandatory breaks the instances without a handler (rule 4).
                if (Reflect.Get(old, "IsMandatory") is bool mandatory && mandatory != e.Mandatory)
                {
                    if (mandatory) Reflect.Property(old, "IsMandatory", writable: true)!.SetValue(old, false);
                    else problems.Add($"{label}.events.{e.Name}: refused: '{e.Name}' is an existing optional event. oml never makes an existing event mandatory (rule 4): block instances without a handler would break. Add a new event instead.");
                }
                // New event inputs are added as addVariables adds them; rule 4 refuses a changed type on a public block.
                ReuseVariables(old, $"{label}.events.{e.Name}", Reuse.Item(raw, "events", e.Name), ("inputs", "InputParameters", "CreateInputParameter", e.Inputs));
                existing.Add($"event {path}.{e.Name}");
            }
            else events.Add(e);
        if (actions.Count + dataActions.Count + aggregates.Count + events.Count + wire.Count > 0)
        {
            ScreenParts(screen, new ScreenSpec(s.Name, s.Flow, Actions: actions, DataActions: dataActions, Aggregates: aggregates, Events: events,
                OnInitialize: wire.GetValueOrDefault("OnInitialize"), OnReady: wire.GetValueOrDefault("OnReady"), OnRender: wire.GetValueOrDefault("OnRender"),
                OnDestroy: wire.GetValueOrDefault("OnDestroy"), OnParametersChanged: wire.GetValueOrDefault("OnParametersChanged")), label);
            // Listed as created so that --describe-created describes them (the screen or block's own path).
            created.AddRange(aggregates.Select(a => $"aggregate {path}/{a.GetProperty("name").GetString()}"));
            created.AddRange(dataActions.Select(d => $"dataAction {path}/{d.Name}"));
            created.AddRange(actions.Select(a => $"screenAction {path}/{a.Name}"));
        }

        // Not compared: each names the edit op that changes it.
        static string Keys(params (bool given, string key)[] keys) => string.Join(" and ", keys.Where(k => k.given).Select(k => k.key));
        if (Keys((s.Widgets is { Count: > 0 }, "widgets"), (s.Layout is not null, "layout")) is { Length: > 0 } content)
            NotChanged(label, content, $"edits: replaceWidgets with \"at\": \"{at}\" (all its widgets) or a widget path, or addWidgets");
        if (Keys((s.Placeholders is { Count: > 0 }, "placeholders"), (s.Title is { ValueKind: JsonValueKind.Array }, "title widgets")) is { Length: > 0 } slots)
            NotChanged(label, slots, $"edits: replaceWidgets with \"at\" set to the placeholder's path in 'oml tree <module> {at}'");
        if (s.Preparation is { Count: > 0 }) NotChanged(label, "preparation", "edits on its Preparation flow (insertNodes, set, delete)");
    }

    // A restated screen or block aggregate: its sources, maxRecords, fetch, timeout and onAfterFetch must match.
    // Filters, joins, sorts and groups are not compared; addToAggregate and set change an existing aggregate.
    void ReuseAggregate(object aggregate, JsonElement spec, string label)
    {
        var changed = new List<string>();
        var db = Reflect.Get(aggregate, "AsDatabaseAggregate") ?? aggregate;
        if (spec.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            var now = Elements(Reflect.Get(db, "Sources")).Select(x => Reflect.Get(x, "Source") is { } e ? Model.NameOf(e) ?? "" : "").Order(StringComparer.Ordinal);
            var given = sources.EnumerateArray().Select(x => (x.GetString() ?? "").Split('.')[^1]).Order(StringComparer.Ordinal);
            if (!now.SequenceEqual(given)) changed.Add("sources");
        }
        if (spec.TryGetProperty("maxRecords", out var max)
            && Reuse.Norm(max.ValueKind == JsonValueKind.String ? max.GetString() : max.GetRawText()) != Reuse.Norm(Preview.Text(Reflect.Get(aggregate, "MaxRecords"))))
            changed.Add("maxRecords");
        if (spec.TryGetProperty("fetch", out var fetch) && !string.Equals(fetch.GetString(), Reflect.Get(aggregate, "Fetch")?.ToString(), StringComparison.OrdinalIgnoreCase))
            changed.Add("fetch");
        if (spec.TryGetProperty("timeout", out var timeout) && timeout.GetRawText() != (Reflect.Get(aggregate, "Timeout")?.ToString() ?? "0"))
            changed.Add("timeout");
        if (spec.TryGetProperty("onAfterFetch", out var after)
            && after.GetString() != (Reflect.Get(aggregate, "OnAfterFetch") is { } handler && Reflect.Get(handler, "Destination") is { } target ? Model.NameOf(target) : null))
            changed.Add("onAfterFetch");
        if (changed.Count > 0) problems.Add(Reuse.Changed(label, changed, "Change an existing aggregate with edits: set on its tree paths, or addToAggregate."));
    }

    // Spec input that is not compared and not applied, in the report's warnings (not a model defect).
    void NotChanged(string label, string what, string how) =>
        Warnings.Add($"{label} exists: its {what} in the spec {(what.Contains(" and ") || what.EndsWith('s') ? "were" : "was")} not compared and not applied; change {(what.Contains(" and ") || what.EndsWith('s') ? "them" : "it")} with {how}, or by hand in Service Studio.");

    // The element kind as spec keys name it: "siteProperty Limit", not the model's collection name.
    static string Kind(string collection) => collection switch
    {
        "Roles" => "role", "SiteProperties" => "siteProperty", "Timers" => "timer", "Processes" => "process",
        "ClientVariables" => "clientVariable", "SessionVariables" => "sessionVariable", _ => collection,
    };

    // The model widens access by itself: Anonymous includes Registered and every role, Registered every role.
    // Both sides are compared after that widening, so only what the spec controls counts.
    bool SameAccess(object screen, AccessSpec access)
    {
        var anonymous = Reflect.Get(m, "AnonymousRole");
        var registered = Reflect.Get(m, "RegisteredRole");
        var all = Elements(Reflect.Get(m, "Roles")).Where(r => !Equals(r, anonymous) && !Equals(r, registered)).Select(r => Model.NameOf(r) ?? "").ToList();
        SortedSet<string> Widen(bool anon, bool reg, IEnumerable<string> roles)
        {
            var set = new SortedSet<string>(roles, StringComparer.Ordinal);
            if (anon) { reg = true; set.Add("(Anonymous)"); }
            if (reg) { set.Add("(Registered)"); set.UnionWith(all); }
            return set;
        }
        var current = Elements(Reflect.Get(screen, "Roles")).ToList();
        var now = Widen(anonymous is not null && current.Contains(anonymous), registered is not null && current.Contains(registered),
            current.Where(r => !Equals(r, anonymous) && !Equals(r, registered)).Select(r => Model.NameOf(r) ?? ""));
        var wanted = Widen(access.Anonymous, access.Registered, (access.Roles ?? new()).Select(r => r.Split('.')[^1]));
        return now.SetEquals(wanted);
    }

    // Roles, site properties and timers: every key the spec gives must equal the element's value.
    void ReusePlatform(string collection, object element, JsonElement spec)
    {
        var label = $"{Kind(collection)} {RequiredName(spec)}";
        var changed = new List<string>();
        foreach (var p in spec.EnumerateObject().Where(p => p.Name != "name"))
        {
            if (p.Name.Equals("public", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) { SetPublic(element, p.Value.GetBoolean()); continue; }
            var property = p.Name switch
            {
                "persistent" => "IsPersistent", "timeout" => "TimeoutInMinutes", "multiTenant" => "IsMultiTenant",
                "type" => "DataType", "default" => "DefaultValue", _ => char.ToUpperInvariant(p.Name[0]) + p.Name[1..],
            };
            if (Reflect.Property(element, property) is null) continue;
            var now = Reflect.Get(element, property) switch
            {
                null => "",
                IExpression expression => expression.Text ?? "",
                OutSystems.Model.Types.ITypeSignature type => Model.Describe(m, type),
                IObjectSignature named => Model.NameOf(named) ?? "",
                bool b => Lower(b),
                var other => other.ToString() ?? "",
            };
            var given = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString()!, JsonValueKind.True => "true", JsonValueKind.False => "false",
                _ => p.Value.GetRawText(),
            };
            static string Flag(string v) => v.Equals("yes", StringComparison.OrdinalIgnoreCase) ? "true" : v.Equals("no", StringComparison.OrdinalIgnoreCase) ? "false" : v;
            if (p.Name == "type" ? Model.Resolve(m, given) is not { } t || Model.Describe(m, t) != now : Reuse.Norm(Flag(given)) != Reuse.Norm(Flag(now)))
                changed.Add(p.Name);
        }
        if (changed.Count > 0) problems.Add(Reuse.Changed(label, changed, "Change it with an edits \"set\" op, or by hand in Service Studio."));
    }

    // A listed REST integration that exists: new methods are added as for a new integration; an existing method
    // with another verb or URL is a problem; its flow (exposed methods) is compared like an action's.
    void ReuseRest(object integration, RestSpec s, bool service, string label)
    {
        if (s.BaseUrl is not null && (Reflect.Get(integration, "BaseURL")?.ToString() ?? "") != s.BaseUrl)
            problems.Add(Reuse.Changed(label, ["baseUrl"], "Change it with an edits \"set\" op, or by hand in Service Studio."));
        foreach (var a in s.Actions ?? new())
        {
            var old = RestModel.Actions(integration).FirstOrDefault(x => Model.NameOf(x) == a.Name);
            if (old is null) { Try($"{label}.{a.Name}", () => RestAction(integration, a, service, label)); continue; }
            var changed = new List<string>();
            // Only keys the spec gives: an exposed method without "url" gets one from its name.
            var raw = Reuse.Item(Reuse.Item(Raw, service ? "restServices" : "restClients", s.Name), "actions", a.Name);
            if (Reuse.Has(raw, "method") && !string.Equals(Reflect.Get(old, "HTTPMethod")?.ToString(), a.Method, StringComparison.OrdinalIgnoreCase)) changed.Add("method");
            if (Reuse.Has(raw, "url") && (Reflect.Get(old, "URLPath")?.ToString() ?? "") != RestUrl(a, service)) changed.Add("url");
            if (changed.Count > 0) problems.Add(Reuse.Changed($"{label}.{a.Name}", changed, "Add a new method instead, or change it by hand in Service Studio."));
            if (service) ReuseFlow(old, a.Flow, $"{label}.{a.Name}");
            existing.Add($"{label}.{a.Name}");
        }
    }

    // Client and session variables: type and default must match.
    void ReuseVariable(string collection, object variable, ModuleVariableSpec s, JsonObject? raw)
    {
        var changed = new List<string>();
        if (Reuse.Has(raw, "type") && Model.Resolve(m, s.Type) is { } type && Model.Describe(m, type) != Model.Describe(m, Reflect.Get(variable, "DataType") as OutSystems.Model.Types.ITypeSignature))
            changed.Add("type");
        if (s.Default is not null && Reuse.Norm(s.Default) != Reuse.Norm(Preview.Text(Reflect.Get(variable, "DefaultValue")))) changed.Add("default");
        if (s.IsSecure is { } secure && Reflect.Get(variable, "IsSecure") is bool isSecure && isSecure != secure) changed.Add("isSecure");
        if (changed.Count > 0) problems.Add(Reuse.Changed($"{Kind(collection)} {s.Name}", changed, "Change it with an edits \"set\" op, or by hand in Service Studio."));
    }
}
