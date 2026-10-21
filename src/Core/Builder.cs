using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Enumerations;
using OutSystems.Model.UI.Mobile;

namespace OmlCli;

// Mandatory: null keeps the model default (mandatory, as in Service Studio); true or false sets it.
record VarSpec(string Name, string Type = "Text", bool? Mandatory = null, string? Default = null, string? Description = null);
record ActionSpec(string Name, List<VarSpec>? Inputs = null, List<VarSpec>? Outputs = null, List<VarSpec>? Locals = null,
    List<JsonElement>? Flow = null, bool? Public = null, bool Function = false, string? Description = null);
record ScreenSpec(string Name, string Flow = "MainFlow", JsonElement? Title = null,
    List<VarSpec>? Inputs = null, List<VarSpec>? Locals = null, List<JsonElement>? Aggregates = null,
    List<DataActionSpec>? DataActions = null, List<ActionSpec>? Actions = null, List<JsonElement>? Widgets = null,
    string? OnInitialize = null, string? OnReady = null, string? OnRender = null, string? OnDestroy = null,
    string? Layout = null, Dictionary<string, List<JsonElement>>? Placeholders = null, string? Template = null, string? Entity = null,
    AccessSpec? Access = null, List<JsonElement>? Preparation = null, string? StyleSheet = null, bool IsDefault = false,
    List<string>? RequiredScripts = null,
    List<EventSpec>? Events = null, string? OnParametersChanged = null, bool? Public = null);

/// Builds actions, flows, screens and widgets in the order the model needs (skill §9.2):
/// create everything -> wire references and types -> wire connectors -> widgets -> expressions and arguments.
partial class Builder(IESpace m, IModelServices services, List<string> created, List<string> existing, List<string> problems, ProductionRules rules)
{
    readonly Dictionary<object, string?> aggregateNames = new();
    readonly List<(string label, Action apply)> flows = new(), references = new(), targets = new(), widgets = new(), expressions = new();

    public void Run(List<JsonElement>? edits = null)
    {
        // Flows run after every action signature exists, so a flow can call an action defined later in the spec.
        foreach (var stage in new[] { flows, references, targets, widgets })
            for (var i = 0; i < stage.Count; i++) // widgets append expressions while running
                Try(stage[i].label, stage[i].apply);
        Edit(edits);
    }

    void Defer(object owner, (string label, Action apply) step) => expressions.Add((step.label, () =>
    {
        if (owner is IObjectSignature o && o.WasDeleted) return;
        step.apply();
    }));

    void Try(string label, Action apply)
    {
        try { apply(); }
        catch (ModelError e) { problems.Add($"{label}: {e.Message}"); }
        // The model refuses some edits by throwing (e.g. a service action in a module type that cannot hold one).
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or KeyNotFoundException or JsonException or FormatException or OverflowException or System.Reflection.TargetInvocationException)
        {
            problems.Add($"{label}: {(e.InnerException ?? e).Message}");
        }
        // Any other model failure (an internal Service Studio exception) is a problem too, never a crash without a report.
        catch (Exception e) when (e is not CliError)
        {
            problems.Add($"{label}: the model could not do this ({Innermost(e).GetType().Name}: {Innermost(e).Message}); do it in Service Studio");
        }
    }

    // References apply in the reference stage, or at once when that stage has already run (widgets).
    void Queue(string label, IESpace module, object target, string key, JsonElement value, bool referencesDone = false)
    {
        try
        {
            rules.Set(target, key, value);
            var (stage, apply) = Reflect.Plan(module, target, key, value);
            if (stage == Reflect.Stage.Expression) Defer(target, ($"{label}.{key}", apply));
            else if (referencesDone) Try($"{label}.{key}", apply);
            else references.Add(($"{label}.{key}", apply));
        }
        catch (ModelError e) { problems.Add($"{label}.{key}: {e.Message}"); }
    }

    bool Fresh(object created, string requested, string label)
    {
        var actual = Model.NameOf(created);
        if (actual == requested) return true;
        var taken = NameClash.Taken(created, requested);
        ((IObjectSignature)created).Delete();
        problems.Add($"{label}: {taken}; the model would rename it {actual}");
        return false;
    }

    // Fresh for a step that cannot go on: the same message, as an error of the step.
    static void FreshOrThrow(object created, string requested, string what)
    {
        var actual = Model.NameOf(created);
        if (actual == requested) return;
        var taken = NameClash.Taken(created, requested);
        ((IObjectSignature)created).Delete();
        throw new ModelError($"{what} {requested}: {taken}; the model would rename it {actual}");
    }

    // ---- actions ----

    public void Action(string kind, ActionSpec s) => Try($"{kind} {s.Name}", () => CreateAction(kind, s));

    void CreateAction(string kind, ActionSpec s)
    {
        var all = kind switch { "serverAction" => m.ServerActions.Cast<object>(), "serviceAction" => m.ServiceActions, _ => m.ClientActions };
        if (all.FirstOrDefault(a => Model.NameOf(a) == s.Name) is { } found) { ReuseAction(kind, found, s); existing.Add($"{kind} {s.Name}"); return; }
        object action = kind switch
        {
            "serverAction" => m.CreateServerAction(s.Name, services.NewKey()),
            "serviceAction" => m.CreateServiceAction(s.Name, services.NewKey()),
            _ => m.CreateClientAction(s.Name, services.NewKey()),
        };
        if (!Fresh(action, s.Name, $"{kind} {s.Name}")) return;
        if (s.Public is { } isPublic && Reflect.Property(action, "Public", writable: true) is { } pub) pub.SetValue(action, isPublic);
        if (s.Function && Reflect.Property(action, "Function", writable: true) is { } fn) fn.SetValue(action, true);
        Body(action, s, $"{kind} {s.Name}");
        created.Add($"{kind} {s.Name}");
    }

    void Body(object action, ActionSpec s, string label)
    {
        if (s.Description is not null && Reflect.Property(action, "Description", writable: true) is { } d) d.SetValue(action, s.Description);
        Variables(action, "CreateInputParameter", s.Inputs, label);
        Variables(action, "CreateOutputParameter", s.Outputs, label);
        Variables(action, "CreateLocalVariable", s.Locals, label);
        flows.Add((label, () => Flow(action, s.Flow ?? new(), label)));
    }

    void Variables(object owner, string factory, List<VarSpec>? vars, string label)
    {
        foreach (var v in vars ?? new())
        {
            var where = $"{label}.{v.Name}";
            if (Model.Resolve(m, v.Type, problems, where) is not { } type) continue;
            try
            {
                var p = Reflect.Call(owner, factory, v.Name, services.NewKey())!;
                // "block LatticeLoader: local Status: name taken by ...": the label first, as in every problem.
                var what = factory switch { "CreateInputParameter" => "input", "CreateOutputParameter" => "output", _ => "local" };
                if (!Fresh(p, v.Name, $"{label}: {what} {v.Name}")) continue;
                Reflect.Property(p, "DataType", writable: true)!.SetValue(p, type);
                if (v.Mandatory is { } mandatory && Reflect.Property(p, "IsMandatory", writable: true) is { } mand) mand.SetValue(p, mandatory);
                if (v.Description is not null && Reflect.Property(p, "Description", writable: true) is { } desc) desc.SetValue(p, v.Description);
                if (v.Default is not null) Defer(p, ($"{where}.default", () => Reflect.Call(p, "SetDefaultValue", v.Default)));
            }
            catch (ModelError e) { problems.Add($"{where}: {e.Message}"); }
        }
    }

    // ---- flows ----

    static readonly HashSet<string> FlowKeys = new(StringComparer.OrdinalIgnoreCase)
        { "kind", "name", "next", "true", "false", "cycle", "assign", "action", "args", "sources", "filters", "sorts", "maxRecords", "joins", "groupBy", "aggregates", "calculated", "groupFilters", "groupSorts", "groupCalculated", "fetch", "onAfterFetch", "timeout", "cacheInMinutes", "screen", "dataSource", "cases", "otherwise", "sql", "params", "outputs", "inputs", "attributeSelection" };

    static Type NodeType(string kind) =>
        Reflect.Types(typeof(OutSystems.Model.Logic.Nodes.IActionNode).Assembly).FirstOrDefault(t =>
            t.IsInterface && t.Namespace == "OutSystems.Model.Logic.Nodes"
            && t.Name.Equals($"I{kind}Node", StringComparison.OrdinalIgnoreCase))
        ?? throw new ModelError($"unknown node kind '{kind}' (see 'oml api \"OutSystems.Model.Logic.Nodes.I*Node\"'){SpecHelp.Hint(kind, NodeKinds())}");

    static IEnumerable<string> NodeKinds() => Reflect.Types(typeof(OutSystems.Model.Logic.Nodes.IActionNode).Assembly)
        .Where(t => t.IsInterface && t.Namespace == "OutSystems.Model.Logic.Nodes" && t.Name.StartsWith("I") && t.Name.EndsWith("Node"))
        .Select(t => char.ToLowerInvariant(t.Name[1]) + t.Name[2..^4]).Where(k => k.Length > 0).Append("triggerEvent").Distinct();

    void Flow(object action, List<JsonElement> spec, string label, object? after = null, string connector = "Target", object? successor = null, bool connectEntry = true)
    {
        var nodes = ((System.Collections.IEnumerable)Reflect.Get(action, "Nodes")!).Cast<object>().ToList();
        var exceptionAction = action is OutSystems.Model.UI.IOnExceptionAction;
        var start = exceptionAction ? null : nodes.OfType<OutSystems.Model.Logic.Nodes.IStartNode>().FirstOrDefault()
                    ?? (object)Reflect.CallGeneric(action, "CreateNode", typeof(OutSystems.Model.Logic.Nodes.IStartNode), "Start", services.NewKey())!;
        var named = nodes.Where(n => Model.NameOf(n) is not null).GroupBy(n => Model.NameOf(n)!).ToList();
        var byName = named.Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var ambiguous = named.Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        if (start is not null) byName.TryAdd("Start", start);
        object End()
        {
            if (byName.TryGetValue("End", out var e)) return e;
            return byName["End"] = nodes.OfType<OutSystems.Model.Logic.Nodes.IEndNode>().FirstOrDefault()
                   ?? Reflect.CallGeneric(action, "CreateNode", typeof(OutSystems.Model.Logic.Nodes.IEndNode), "End", services.NewKey())!;
        }

        var built = new List<(JsonElement spec, object node, string name, string kind)>();
        foreach (var original in spec)
        {
            var n = original;
            var kind = n.GetProperty("kind").GetString()!;
            var name = n.TryGetProperty("name", out var nm) ? nm.GetString()! : kind;
            var where = $"{label}/{name}";
            try
            {
                if (kind.Equals("sendEmail", StringComparison.OrdinalIgnoreCase) && !WebUi.IsTraditional(m))
                    (n, kind) = (ReactiveSendEmail(action, n), "execute");
                object? callee = null;
                Type type;
                if (kind.Equals("execute", StringComparison.OrdinalIgnoreCase))
                {
                    var target = n.GetProperty("action").GetString()!;
                    callee = Refs.Find(m, target, typeof(IObjectSignature));
                    var client = callee.GetType().GetInterfaces().Any(i => i.Name == "IClientActionSignature");
                    type = client ? typeof(OutSystems.Model.Logic.Nodes.IExecuteClientActionNode) : typeof(OutSystems.Model.Logic.Nodes.IExecuteServerActionNode);
                    if (!n.TryGetProperty("name", out _)) name = Model.NameOf(callee) ?? target;
                }
                else type = kind.Equals("triggerEvent", StringComparison.OrdinalIgnoreCase) ? typeof(OutSystems.Model.Logic.Nodes.ITriggerNode) : NodeType(kind);

                if (type == typeof(OutSystems.Model.Logic.Nodes.ITriggerNode)) TriggerBlock(action);
                if ((byName.ContainsKey(name) || ambiguous.Contains(name))
                    && !(name == "End" && type == typeof(OutSystems.Model.Logic.Nodes.IEndNode)))
                    throw new ModelError($"node name '{name}' already exists in this flow{(byName.GetValueOrDefault(name) is IObjectSignature held ? ": " + NameClash.Describe(held) : "")}");
                // An "End" node reuses the action's existing End, which bare flows and implicit ends also use.
                var node = name == "End" && type == typeof(OutSystems.Model.Logic.Nodes.IEndNode)
                    ? End() : Reflect.CallGeneric(action, "CreateNode", type, name, services.NewKey())!;
                if (Reflect.Property(node, "Label", writable: true) is { } labelProperty) labelProperty.SetValue(node, name);
                if ((Reflect.Property(node, "Name", true) is not null || Reflect.Property(node, "Label", true) is not null)
                    && !ReferenceEquals(node, byName.GetValueOrDefault(name)) && !Fresh(node, name, where)) continue;
                byName[name] = node;
                built.Add((n, node, name, kind.ToLowerInvariant()));
                if (kind.Equals("javaScript", StringComparison.OrdinalIgnoreCase))
                {
                    List<VarSpec>? Vars(string key) => n.TryGetProperty(key, out var v)
                        ? JsonSerializer.Deserialize<List<VarSpec>>(v.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
                    Variables(node, "CreateInputParameter", Vars("inputs"), where);
                    Variables(node, "CreateOutputParameter", Vars("outputs"), where);
                }

                if (callee is not null)
                {
                    Reflect.Property(node, "Action", writable: true)!.SetValue(node, callee);
                    if (n.TryGetProperty("args", out var args))
                        Defer(node, ($"{where}.args", () => Arguments(node, args)));
                }
                else if (n.TryGetProperty("args", out var nodeArgs))
                    Defer(node, ($"{where}.args", () => Arguments(node, nodeArgs)));
                if (n.TryGetProperty("assign", out var assign))
                    Defer(node, ($"{where}.assign", () =>
                    {
                        foreach (var a in assign.EnumerateObject())
                        {
                            var assignment = Reflect.Call(node, "CreateAssignment")!;
                            Reflect.Call(assignment, "SetVariable", a.Name);
                            Reflect.Call(assignment, "SetValue", a.Value.GetString());
                        }
                    }));
                if (kind.Equals("aggregate", StringComparison.OrdinalIgnoreCase)) Aggregate(node, n, where);
                if (kind.Equals("sql", StringComparison.OrdinalIgnoreCase)) Sql(node, n, where);
                QueryNode(action, node, n, kind, where);
                if (node is OutSystems.Model.Logic.Nodes.ITriggerNode) TriggerEvent(action, node, n);
                foreach (var p in n.EnumerateObject().Where(p => !FlowKeys.Contains(p.Name) && !(node is OutSystems.Model.Logic.Nodes.ITriggerNode && p.Name == "event")))
                    Queue(where, m, node, p.Name, p.Value);
                CheckLogicNode(action, node, n, kind, where);
            }
            catch (Exception e) when (e is ModelError or KeyNotFoundException)
            {
                problems.Add($"{where}: {e.Message}");
            }
        }

        // Connectors: Target reads back null (skill §6.4), so wire every one explicitly.
        if (connectEntry && start is not null) targets.Add(($"{label}.flow", () =>
        {
            var entry = built.TakeWhile(b => b.kind != "exceptionhandler").FirstOrDefault(b => b.kind != "comment").node;
            Connect(after ?? start, connector, entry ?? successor ?? End());
        }));
        var handlerEnds = new Dictionary<int, object>();
        var region = -1;
        for (var index = 0; index < built.Count; index++)
        {
            var i = index;
            if (built[i].kind == "exceptionhandler") region++;
            var handlerRegion = region;
            object Finish(bool explicitEnd = false)
            {
                if (handlerRegion < 0) return explicitEnd ? End() : successor ?? End();
                if (!handlerEnds.TryGetValue(handlerRegion, out var end))
                    handlerEnds[handlerRegion] = end = Reflect.CallGeneric(action, "CreateNode", typeof(OutSystems.Model.Logic.Nodes.IEndNode), "HandlerEnd", services.NewKey())!;
                return end;
            }
            targets.Add(($"{label}/{built[i].name}.flow", () =>
            {
                var (n, node, name, kind) = built[i];
                if (kind is "end" or "raiseexception" or "raise" or "destination" or "comment") return;
                string? Named(string key) => n.TryGetProperty(key, out var v) ? v.GetString() : null;
                object Resolve(string? target) => target is null ? (built.Skip(i + 1).SkipWhile(b => b.kind == "comment").FirstOrDefault() is var following
                        && following.node is not null && following.kind != "exceptionhandler" ? following.node : Finish())
                    : target == "End" ? Finish(explicitEnd: true)
                    : ambiguous.Contains(target) ? throw new ModelError($"ambiguous node target '{target}'")
                    : byName.TryGetValue(target, out var t) ? t is OutSystems.Model.Logic.Nodes.IExceptionHandlerNode or OutSystems.Model.Logic.Nodes.ICommentNode
                        ? throw new ModelError($"{name}: cannot connect to an exception handler or comment") : t
                    : throw new ModelError($"{name}: no node named '{target}'");
                if (kind == "switch")
                {
                    SwitchTargets(node, n, Resolve);
                    return;
                }
                if (Reflect.Property(node, "TrueTarget") is not null)
                {
                    Connect(node, "TrueTarget", Resolve(Named("true") ?? throw new ModelError($"{name}: 'true' target required")));
                    Connect(node, "FalseTarget", Resolve(Named("false") ?? throw new ModelError($"{name}: 'false' target required")));
                    return;
                }
                if (Reflect.Property(node, "CycleTarget") is not null)
                    Connect(node, "CycleTarget", Resolve(Named("cycle") ?? throw new ModelError($"{name}: 'cycle' target required")));
                if (Reflect.Property(node, "Target") is not null) Connect(node, "Target", Resolve(Named("next")));
            }));
        }
    }

    static void Connect(object from, string property, object to) =>
        Reflect.Property(from, property, writable: true)!.SetValue(from, to);

    // Bind by parameter name, never by position (skill §9).
    static void Arguments(object owner, JsonElement args)
    {
        var values = Reflect.Get(owner, "Arguments") as System.Collections.IEnumerable
            ?? throw new ModelError("node does not accept arguments");
        var list = values.Cast<object>().ToList();
        foreach (var a in args.EnumerateObject())
        {
            var arg = list.FirstOrDefault(x => Reflect.Get(x, "Parameter") is { } p && Model.NameOf(p) == a.Name)
                      ?? throw new ModelError($"no parameter '{a.Name}'");
            Reflect.Call(arg, "SetValue", a.Value.ValueKind switch
            {
                JsonValueKind.String => a.Value.GetString(),
                JsonValueKind.Number => a.Value.GetRawText(), // a number is its own expression
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                _ => throw new ModelError($"argument '{a.Name}' must be an expression string, a number or true/false"),
            });
        }
    }

    void Aggregate(object aggregate, JsonElement spec, string where)
    {
        var db = Reflect.Get(aggregate, "AsDatabaseAggregate") ?? throw new ModelError("not a database aggregate");
        if (QueryShapes.Problems(spec) is { Count: > 0 } shape) throw new ModelError(string.Join("; ", shape));
        Numbers(aggregate, spec, where);
        // Sources must exist before join endpoints and query expressions are bound.
        foreach (var source in spec.TryGetProperty("sources", out var ss) ? ss.EnumerateArray() : Enumerable.Empty<JsonElement>())
        {
            var name = source.GetString()!;
            references.Add(($"{where}.sources", () =>
                Reflect.Call(db, "CreateSource", Refs.Find(m, name, typeof(OutSystems.Model.Logic.Aggregates.IDataSource)), name)));
        }
        QueryAttributes(db, spec, where);
        foreach (var filter in spec.TryGetProperty("filters", out var fs) ? fs.EnumerateArray() : Enumerable.Empty<JsonElement>())
            Defer(aggregate, ($"{where}.filters", () => Reflect.Call(db, "CreateFilter", filter.GetString())));
        Sorts(db, spec, "sorts", "CreateSort", where);
        if (spec.TryGetProperty("fetch", out var fetch)) Fetch(aggregate, fetch.GetString(), where);
        if (spec.TryGetProperty("onAfterFetch", out var after)) AfterFetch(aggregate, after.GetString(), where);
        if (spec.TryGetProperty("maxRecords", out var max))
            Defer(aggregate, ($"{where}.maxRecords", () => Reflect.Call(aggregate, "SetMaxRecords", max.ToString())));
        // The model auto-names an aggregate from its source and filters (GetOrders -> GetOrdersByCustomerId), as
        // Service Studio does. Restore the spec's name before any later expression refers to it.
        aggregateNames[aggregate] = Model.NameOf(aggregate);
        Defer(aggregate, ($"{where}.name", () =>
        {
            var wanted = aggregateNames[aggregate];
            if (Model.NameOf(aggregate) != wanted) Reflect.Property(aggregate, "Name", writable: true)!.SetValue(aggregate, wanted);
        }));
    }

    // ---- screens and blocks ----

    public void Screen(ScreenSpec s, bool block) => Try($"{(block ? "block" : "screen")} {s.Name}", () => CreateScreen(s, block));

    void CreateScreen(ScreenSpec s, bool block)
    {
        if (WebUi.IsTraditional(m)) { CreateWebScreen(s, block); return; }
        if (s.Preparation is not null) throw new ModelError("preparation requires a Traditional Web module; use aggregates or lifecycle actions for Reactive/Mobile");
        var label = $"{(block ? "block" : "screen")} {s.Name}";
        var flow = m.MobileFlows.FirstOrDefault(f => f.Name == s.Flow) ?? NewMobileFlow(s.Flow);
        if (flow.Nodes.FirstOrDefault(n => Model.NameOf(n) == s.Name) is { } found) { ReuseScreen(found, s, block, label); existing.Add(label); return; }
        object screen = block ? flow.CreateBlock(s.Name, services.NewKey()) : flow.CreateScreen(s.Name, services.NewKey());
        if (!Fresh(screen, s.Name, label)) return;
        created.Add(label);
        if (s.Public is { } isPublic) Set(screen, "Public", isPublic);
        // A themed screen factory may seed a layout; explicit content replaces that starter tree.
        if (s.Layout is not null || s.Widgets is not null)
            foreach (var seeded in Elements(Reflect.Get(screen, "Widgets")).OfType<IObjectSignature>().ToList()) seeded.Delete();

        ScreenParts(screen, s, label);
    }

    // The model silently renames a flow whose name is taken, for example by a referenced module's MainFlow.
    IMobileFlow NewMobileFlow(string name)
    {
        var flow = m.CreateMobileFlow(name, services.NewKey());
        if (flow.Name != name)
        {
            var given = flow.Name;
            var holder = NameClash.Holder(flow, name) ?? "an element oml cannot see, for example a flow of a referenced module";
            flow.Delete();
            throw new ModelError($"UI flow name '{name}' is taken by {holder}; the model would name it {given}. Set \"flow\" to a free name.");
        }
        created.Add($"flow {name}");
        return flow;
    }

    void ScreenParts(object screen, ScreenSpec s, string label)
    {
        OmlCli.Content.RequireScripts(m, screen, s.RequiredScripts);
        if (s.StyleSheet is not null) Queue(label, m, screen, "StyleSheet", JsonSerializer.SerializeToElement(s.StyleSheet));
        if (s.IsDefault) references.Add(($"{label}.isDefault", () => Set(m, "DefaultScreen", screen)));
        if (s.Access is { } access) references.Add(($"{label}.access", () => SetAccess(screen, access)));
        if (WebUi.IsTraditional(m)) { WebScreenParts(screen, s, label); return; }
        if (s.Title is { ValueKind: JsonValueKind.String } title)
            Defer(screen, ($"{label}.title", () => Reflect.Call(screen, "SetTitle", title.GetString())));
        Variables(screen, "CreateInputParameter", s.Inputs, label);
        Variables(screen, "CreateLocalVariable", s.Locals, label);
        BlockEvents(screen, s.Events, label);

        foreach (var a in s.Aggregates ?? new())
        {
            var name = a.GetProperty("name").GetString()!;
            try
            {
                var aggregate = Reflect.Call(screen, "CreateScreenAggregate", false, name, services.NewKey())!;
                if (!Fresh(aggregate, name, label)) continue;
                Aggregate(aggregate, a, $"{label}/{name}");
                foreach (var p in a.EnumerateObject().Where(p => !QueryKeys.Contains(p.Name)))
                    Queue($"{label}/{name}", m, aggregate, p.Name, p.Value);
            }
            catch (ModelError e) { problems.Add($"{label}/{name}: {e.Message}"); }
        }

        foreach (var d in s.DataActions ?? new())
        {
            var action = Reflect.Call(screen, "CreateDataAction", d.Name, services.NewKey())!;
            if (!Fresh(action, d.Name, label)) continue;
            // The factory seeds an Out1 output (skill §8.1); the spec's outputs replace it.
            foreach (var seeded in ((System.Collections.IEnumerable)Reflect.Get(action, "OutputParameters")!).Cast<IObjectSignature>().ToList())
                seeded.Delete();
            Body(action, d, $"{label}/{d.Name}");
            if (d.Fetch is not null) Fetch(action, d.Fetch, $"{label}/{d.Name}");
            if (d.Timeout is { } timeout) Set(action, "ServerRequestTimeout", timeout);
            if (d.OnAfterFetch is not null) AfterFetch(action, d.OnAfterFetch, $"{label}/{d.Name}");
        }

        var screenActions = Elements(Reflect.Get(screen, "ScreenActions")).ToDictionary(a => Model.NameOf(a)!, a => a);
        foreach (var a in s.Actions ?? new())
        {
            var action = Reflect.Call(screen, "CreateScreenAction", a.Name, services.NewKey())!;
            if (!Fresh(action, a.Name, label)) continue;
            screenActions[a.Name] = action;
            Body(action, a, $"{label}/{a.Name}");
        }

        foreach (var (evt, handler) in new[] { ("OnInitialize", s.OnInitialize), ("OnReady", s.OnReady), ("OnRender", s.OnRender), ("OnDestroy", s.OnDestroy), ("OnParametersChanged", s.OnParametersChanged) })
            if (handler is not null)
                references.Add(($"{label}.{evt}", () =>
                {
                    if (evt == "OnParametersChanged" && !Inspect.IsBlock(screen)) throw new ModelError("onParametersChanged is only for blocks");
                    var lifecycle = Reflect.Get(screen, evt) ?? throw new ModelError($"no {evt} on this build");
                    var target = screenActions.GetValueOrDefault(handler) ?? throw new ModelError($"no screen action '{handler}'");
                    Reflect.Property(lifecycle, "Destination", writable: true)!.SetValue(lifecycle, target);
                }));

        widgets.Add(($"{label}.widgets", () =>
        {
            if (s.Layout is null)
            {
                if (s.Placeholders is { Count: > 0 } || s.Title is { ValueKind: JsonValueKind.Array })
                    throw new ModelError("placeholder content requires a layout block");
                foreach (var w in s.Widgets ?? new()) Widget(screen, w, label, screenActions);
                return;
            }
            var content = new Dictionary<string, List<JsonElement>>(s.Placeholders ?? new(), StringComparer.OrdinalIgnoreCase);
            if (s.Widgets is not null) content.TryAdd("MainContent", s.Widgets);
            if (s.Title is { ValueKind: JsonValueKind.Array } items) content.TryAdd("Title", items.EnumerateArray().ToList());
            Widget(screen, JsonSerializer.SerializeToElement(new { type = "BlockInstance", sourceBlock = s.Layout, placeholders = content }), label, screenActions);
        }));
    }

    static Type? BuiltinWidget(string type) =>
        Reflect.Types(typeof(OutSystems.Model.UI.Mobile.Widgets.IIfWidget).Assembly).FirstOrDefault(t =>
            t.IsInterface && t.Namespace == "OutSystems.Model.UI.Mobile.Widgets"
            && (t.Name.Equals($"I{type}Widget", StringComparison.OrdinalIgnoreCase)
                || t.Name.Equals($"IMobile{type}Widget", StringComparison.OrdinalIgnoreCase)));

    object? Widget(object parent, JsonElement w, string label, Dictionary<string, object> screenActions)
    {
        if (WebUi.IsTraditional(m)) return WebWidget(parent, w, label, screenActions);
        var type = w.GetProperty("type").GetString()!;
        var name = w.TryGetProperty("name", out var nm) ? nm.GetString() : null;
        var where = $"{label}/{name ?? type}";
        object widget;
        try
        {
            // Built-in widgets (If, Text, BlockInstance) come from the typed factory; Reactive plugin widgets from
            // their descriptor (skill §11.2), whose factory takes no name or key.
            if (BuiltinWidget(type) is { } builtin)
                widget = Reflect.CallGeneric(parent, "CreateWidget", builtin, name, services.NewKey())!;
            else
            {
                var descriptor = ServiceStudio.PluginAPI.PluginProvider.Implementations.FirstOrDefault(d =>
                    d.GetType().Name.Equals(type + "Descriptor", StringComparison.OrdinalIgnoreCase)
                    && d.GetType().Assembly == typeof(ServiceStudio.Plugin.NRWidgets.IExpression).Assembly)
                    ?? throw new ModelError($"unknown widget type '{type}' (see 'oml widgets')");
                widget = Reflect.Call(parent, "CreateWidget", descriptor)!;
                if (name is not null) Reflect.Property(widget, "Name", writable: true)!.SetValue(widget, name);
                // Like Service Studio's toolbox, a descriptor seeds default content (a Link gets a Text "link").
                // When the spec gives children, they replace that content instead of rendering after it.
                if (w.TryGetProperty("children", out var given) && given.ValueKind == JsonValueKind.Array)
                    foreach (var seeded in Elements(Reflect.Get(Content(widget), "Widgets")).OfType<IObjectSignature>().ToList())
                        seeded.Delete();
            }
        }
        catch (ModelError e) { problems.Add($"{where}: {e.Message}"); return null; }

        if (name is not null && !Fresh(widget, name, where)) return null;
        if (w.TryGetProperty("args", out var args))
            Defer(widget, ($"{where}.args", () => Arguments(
                w.TryGetProperty("onClick", out var click) && click.ValueKind == JsonValueKind.String
                    ? Reflect.Get(widget, "OnClick") ?? throw new ModelError("widget has no OnClick event") : widget, args)));
        // Setting SourceBlock materializes PlaceholdersContent; do it before filling any content.
        if (w.TryGetProperty("sourceBlock", out var sourceBlock))
            Queue(where, m, widget, "sourceBlock", sourceBlock, referencesDone: true);
        if (w.TryGetProperty("placeholders", out var content))
            foreach (var area in content.EnumerateObject())
            {
                var slots = Reflect.Get(widget, "PlaceholdersContent") as System.Collections.IEnumerable;
                var slot = slots?.Cast<object>().FirstOrDefault(x => Reflect.Get(x, "Placeholder") is { } p
                    && string.Equals(Model.NameOf(p), area.Name, StringComparison.OrdinalIgnoreCase));
                if (slot is null) { problems.Add($"{where}: unknown placeholder '{area.Name}' in block '{sourceBlock}'"); continue; }
                foreach (var child in area.Value.EnumerateArray()) Widget(slot, child, label, screenActions);
            }
        if (widget is OutSystems.Model.UI.Mobile.Widgets.IMobileBlockInstanceWidget)
            Try($"{where}.events", () => BlockInstanceEvents(widget, w, where, screenActions));
        else if (w.TryGetProperty("events", out _)) problems.Add($"{where}: events is only for BlockInstance; use onClick, onChange or another event key on other widgets");
        foreach (var p in w.EnumerateObject().Where(p => p.Name is not ("type" or "name" or "args" or "sourceBlock" or "placeholders" or "events")))
        {
            if (p.Value.ValueKind == JsonValueKind.Array)
            {
                // "children" go into the widget, or its first placeholder for plugin widgets (skill §11.1);
                // any other array names a content area (trueBranch, row, headerRow, ...).
                var container = p.Name == "children" ? Content(widget) : Reflect.Get(widget, p.Name);
                if (container is null) { problems.Add($"{where}: no content area '{p.Name}' on this build"); continue; }
                foreach (var child in p.Value.EnumerateArray()) Widget(container, child, label, screenActions);
            }
            // An event names a screen action, or a screen to navigate to (with "args" on the widget).
            else if (p.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                Try($"{where}.{p.Name}", () =>
                {
                    var handler = screenActions.GetValueOrDefault(p.Value.GetString()!)
                        ?? Refs.Find(m, p.Value.GetString()!, typeof(OutSystems.Model.UI.IScreenSignature));
                    Event(widget, p.Name, handler);
                });
            // Object form: { "destination": "<Screen>", "args": { ... } }.
            else if (p.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object)
                Try($"{where}.{p.Name}", () =>
                {
                    var evt = Reflect.Get(widget, p.Name) ?? throw new ModelError($"no event '{p.Name}'");
                    var destination = Refs.Find(m, p.Value.GetProperty("destination").GetString()!, typeof(IObjectSignature));
                    Reflect.Property(evt, "Destination", writable: true)!.SetValue(evt, destination);
                    if (p.Value.TryGetProperty("args", out var eventArgs)) Defer(widget, ($"{where}.{p.Name}.args", () => Arguments(evt, eventArgs)));
                });
            else if (p.Name.Equals("targetWidget", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                Defer(widget, ($"{where}.targetWidget", () => TargetWidget(widget, p.Value.GetString()!)));
            else
                Queue(where, m, widget, p.Name, p.Value, referencesDone: true);
        }
        return widget;
    }

    // A Label's target widget, by name: a widget of the same screen, block or email, found after every widget exists.
    // An empty name clears the target.
    static void TargetWidget(object widget, string name)
    {
        var property = Reflect.Property(widget, "TargetWidget", writable: true) ?? throw new ModelError("this widget has no target widget (only a Label has one)");
        if (name.Length == 0) { property.SetValue(widget, null); return; }
        var owner = Ancestors((IObjectSignature)widget).FirstOrDefault(o => o is OutSystems.Model.UI.IScreenSignature or OutSystems.Model.UI.IBlockSignature or OutSystems.Model.UI.IEmail)
            ?? throw new ModelError("the widget is not inside a screen, block or email");
        var hits = ProductionRules.Walk(owner).Where(o => o is OutSystems.Model.UI.IWidgetSignature && !ReferenceEquals(o, widget) && Model.NameOf(o) == name).ToList();
        if (hits.Count != 1) throw new ModelError(hits.Count == 0 ? $"targetWidget: no widget named '{name}' in {Model.NameOf(owner)}" : $"targetWidget: several widgets named '{name}'");
        if (!property.PropertyType.IsInstanceOfType(hits[0])) throw new ModelError($"targetWidget: '{name}' is {ElementPaths.Kind(hits[0])}, which a Label cannot target; name an input widget");
        property.SetValue(widget, hits[0]);
    }

    static object Content(object widget) =>
        Reflect.Get(widget, "Placeholders") is System.Collections.IEnumerable placeholders
        && placeholders.Cast<object>().FirstOrDefault() is { } first ? first : widget;

    // Prefer the typed event; retain the descriptor fallback for builds that expose only a handler setter.
    static void Event(object widget, string evt, object handler)
    {
        if (Reflect.Get(widget, evt) is { } builtin
            && Reflect.Property(builtin, "Destination", writable: true) is { } destination)
        {
            destination.SetValue(builtin, handler);
            return;
        }
        var descriptor = Reflect.Get(widget, "CustomWidgetDescriptor");
        var setter = descriptor?.GetType().GetMethods().FirstOrDefault(x =>
            x.Name.Equals($"Set{evt}Handler", StringComparison.OrdinalIgnoreCase) && x.GetParameters().Length == 2);
        if (setter is null) throw new ModelError($"no {evt} handler setter on this build");
        try { setter.Invoke(descriptor, new[] { widget, handler }); }
        catch (Exception e) when (e is ArgumentException or System.Reflection.TargetInvocationException)
        {
            throw new ModelError($"{evt}: {(e.InnerException ?? e).Message}");
        }
    }
}
