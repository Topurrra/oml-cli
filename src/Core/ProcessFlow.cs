using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Processes.Nodes;

namespace OmlCli;

// Process (BPT) flows: built by `processes`, changed by edits. A process node goes to the next one through a connector: most nodes
// have one (Targets), a Decision one per outcome (Yes, No). Every change here moves connectors, so the flow stays connected:
// - processes on an existing process adds the nodes it lacks, updates the listed ones and connects them as listed;
// - insertNodes with a process as "action" puts new nodes between "after" and where it went (a Decision: "connector" true or false);
// - replaceNode rebuilds a process node in place (same kind and name: updated; else a new node takes its connectors);
// - delete of a process node joins the node before it to the node after it;
// - connect sets where a node goes next.
partial class Builder
{
    static string NodeName(JsonElement s) => s.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : s.GetProperty("kind").GetString()!;
    static bool IsKind(object node, string kind) => ProcessNodeType(kind).IsInstanceOfType(node);
    static IEnumerable<object> ProcessNodes(object process) => Elements(Reflect.Get(process, "Nodes"));
    static List<object> Outgoing(object node) => Elements(Reflect.Get(node, "Connectors")).ToList();
    static object? TargetOf(object connector) => Reflect.Get(connector, "Target");
    static string Outcome(object connector) => Model.NameOf(connector) ?? Reflect.Get(connector, "Label") as string ?? "";
    static string OutcomeOf(string? connector, string where) => connector?.ToLowerInvariant() switch
    {
        "true" or "yes" => "Yes",
        "false" or "no" => "No",
        null => throw new ModelError($"{where}: a Decision has two exits; say which with connector true or false"),
        _ => connector,
    };

    // A node of the process by name; Start and End are found by kind (the process makes them on first use).
    object ProcessNode(object process, string name)
    {
        foreach (var kind in new[] { "start", "end" })
            if (name.Equals(kind, StringComparison.OrdinalIgnoreCase))
                return ProcessNodes(process).FirstOrDefault(n => IsKind(n, kind) && (Model.NameOf(n) is null || Model.NameOf(n)!.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    ?? ProcessNodes(process).FirstOrDefault(n => IsKind(n, kind))
                    ?? Reflect.CallGeneric(process, "CreateNode", ProcessNodeType(kind), kind == "start" ? "Start" : "End", services.NewKey())!;
        return ProcessNodes(process).FirstOrDefault(n => string.Equals(Model.NameOf(n), name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ModelError($"no process node '{name}' in {Model.NameOf(process)} (oml tree lists them)");
    }

    object MakeNode(object process, string kind, string name, string label)
    {
        var node = Reflect.CallGeneric(process, "CreateNode", ProcessNodeType(kind), name, services.NewKey())!;
        if (Reflect.Property(node, "Label", true) is { } lp) lp.SetValue(node, name);
        if ((Reflect.Property(node, "Name", true) is not null || Reflect.Property(node, "Label", true) is not null) && !Fresh(node, name, label))
            throw new ModelError($"process node '{name}' was renamed");
        return node;
    }

    // Where `from` goes next: a Decision by its outcome ("connector" true or false), any other node to its one next node.
    void SetNext(object from, object to, string? connector, string where)
    {
        if (IsKind(from, "end")) throw new ModelError($"{where}: an End node goes nowhere");
        if (IsKind(from, "decision"))
        {
            var outcome = OutcomeOf(connector, where);
            if (Outgoing(from).FirstOrDefault(c => Outcome(c).Equals(outcome, StringComparison.OrdinalIgnoreCase)) is { } c)
                Reflect.Property(c, "Target", writable: true)!.SetValue(c, to);
            else Reflect.Call(from, "CreateConnector", outcome, to);
            return;
        }
        SetNexts(from, [to], where);
    }

    // Where a node (not a Decision) goes next: one node, or several that run at the same time (parallel branches, as Service Studio
    // allows from Start or any activity).
    void SetNexts(object from, List<object> tos, string where)
    {
        if (IsKind(from, "end")) throw new ModelError($"{where}: an End node goes nowhere");
        if (IsKind(from, "decision")) throw new ModelError($"{where}: a Decision goes to one node per exit; connect each with connector true or false");
        if (tos.Count == 0) throw new ModelError($"{where}: name at least one next node");
        var targetList = Reflect.Get(from, "Targets") ?? throw new ModelError($"{where}: this process node has no next node");
        Reflect.Call(targetList, "Clear");
        foreach (var to in tos.Distinct()) Reflect.Call(targetList, "Add", to);
    }

    // A "next" or "to" value: one node name, or a list of them (parallel branches).
    static List<string> Names(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Select(v => v.GetString()!).ToList() : [value.GetString()!];

    // The settings of one listed node: an Automatic Activity's flow or action, roles, a Human Activity's screen arguments, callbacks,
    // the entity event that closes or starts it, and its other properties.
    void ConfigureProcessNode(object node, string kind, JsonElement s, string where)
    {
        kind = kind.ToLowerInvariant();
        if (kind == "automaticactivity" && (s.TryGetProperty("flow", out _) || s.TryGetProperty("action", out _)))
        {
            var body = s.TryGetProperty("flow", out var bodyFlow) ? bodyFlow.EnumerateArray().ToList() : new();
            if (s.TryGetProperty("action", out var action))
            {
                var call = new Dictionary<string, object> { ["kind"] = "execute", ["action"] = action.GetString()! };
                if (s.TryGetProperty("args", out var args)) call["args"] = args;
                body.Insert(0, JsonSerializer.SerializeToElement(call));
            }
            // An existing activity gets the new flow: its old nodes go, Start stays.
            foreach (var old in Elements(Reflect.Get(node, "Nodes")).OfType<IObjectSignature>().Where(n => n is not OutSystems.Model.Logic.Nodes.IStartNode).ToList()) old.Delete();
            flows.Add((where, () => Flow(node, body, where)));
        }
        if (s.TryGetProperty("roles", out var roleNames))
            references.Add((where + ".roles", () => SetAccess(node, new(Roles: roleNames.EnumerateArray().Select(r => r.GetString()!).ToList()))));
        if (kind is "humanactivity" or "executeprocess" && s.TryGetProperty("args", out var screenArgs))   // the screen's inputs, or the child process's
            Defer(node, (where + ".args", () => Arguments(node, screenArgs)));
        foreach (var key in NodeCallbacks)
            if (s.TryGetProperty(key, out var cb)) Callback(node, key, cb, $"{where}.{key}");
        foreach (var key in new[] { "closeOn", "startOn" })
            if (s.TryGetProperty(key, out var on)) Trigger(node, char.ToUpperInvariant(key[0]) + key[1..], on, where);
        if (kind == "decision" && s.TryGetProperty("condition", out var cond))
        {
            // The Decision's If exists once its connectors do (they are made with the connections): look it up when the expressions are set.
            Defer(node, (where + ".condition", () => Reflect.Call(Elements(Reflect.Get(node, "Nodes")).OfType<OutSystems.Model.Logic.Nodes.IIfNode>().Single(), "SetCondition", cond.GetString())));
        }
        foreach (var p in s.EnumerateObject().Where(p => p.Name is not ("kind" or "name" or "next" or "true" or "false" or "flow" or "action" or "args" or "roles" or "condition" or "closeOn" or "startOn") && !NodeCallbacks.Contains(p.Name)))
            Queue(where, m, node, p.Name, p.Value);
    }

    // The listed nodes: made, or on an existing process (allowExisting) found by name and updated; then each goes to its "next" (a
    // Decision to its true and false), else the next listed node, else `exit` (End). Returns the first listed node.
    object? ProcessFlow(object process, List<JsonElement> specs, string label, object? exit = null, bool allowExisting = true)
    {
        var nodes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in specs)
        {
            var kind = s.GetProperty("kind").GetString()!;
            var name = NodeName(s);
            if (nodes.ContainsKey(name)) throw new ModelError($"duplicate process node name '{name}'");
            var found = kind.Equals("start", StringComparison.OrdinalIgnoreCase) ? ProcessNodes(process).FirstOrDefault(n => IsKind(n, "start"))
                : ProcessNodes(process).FirstOrDefault(n => string.Equals(Model.NameOf(n), name, StringComparison.OrdinalIgnoreCase))
                  // An End keeps no name in the model and has no settings: listed again, it is an End of the process not used yet in this list.
                  ?? (kind.Equals("end", StringComparison.OrdinalIgnoreCase) && allowExisting ? ProcessNodes(process).FirstOrDefault(n => IsKind(n, "end") && !nodes.Values.Contains(n)) : null);
            if (found is not null && !allowExisting && !kind.Equals("start", StringComparison.OrdinalIgnoreCase))
                throw new ModelError($"{label}: the process already has a node named '{name}'; names must be free (change that node with set or replaceNode)");
            if (found is not null && !IsKind(found, kind)) throw new ModelError($"{label}: the process has a node '{name}' of another kind; change it with replaceNode");
            nodes[name] = found ?? MakeNode(process, kind, name, label);
        }
        object Resolve(string n) => nodes.TryGetValue(n, out var x) ? x : ProcessNode(process, n);
        for (var i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            var index = i;
            var kind = s.GetProperty("kind").GetString()!.ToLowerInvariant();
            var name = NodeName(s);
            var node = nodes[name];
            var where = label + "/" + name;
            ConfigureProcessNode(node, kind, s, where);
            targets.Add((where + ".flow", () =>
            {
                if (kind == "end") return;
                if (kind == "decision")
                {
                    foreach (var key in new[] { "true", "false" })
                        if (s.TryGetProperty(key, out var t)) SetNext(node, Resolve(t.GetString()!), key, where);
                    return;
                }
                var nexts = s.TryGetProperty("next", out var target) ? Names(target).Select(Resolve).ToList()
                    : [index + 1 < specs.Count ? nodes[NodeName(specs[index + 1])] : exit ?? ProcessNode(process, "End")];
                SetNexts(node, nexts, where);
            }));
        }
        return specs.Count > 0 ? nodes[NodeName(specs[0])] : null;
    }

    void Process(JsonElement spec)
    {
        // Adapted from OutSystems/outsystems11-mcp examples/AddProcessWithHumanActivity.cs (MIT).
        var name = RequiredName(spec);
        var label = $"process {name}";
        var process = Elements(Reflect.Get(m, "Processes")).FirstOrDefault(e => Model.NameOf(e) == name);
        var isNew = process is null;
        if (process is not null) existing.Add($"Processes {name}");
        else if (PlatformElement("Processes", "CreateProcess", spec) is { } made) process = made;
        else return;
        if (spec.TryGetProperty("launchOn", out var entity)) Trigger(process, "LaunchOn", entity, label);
        // The process callbacks (Service Studio: the process's On Process Start and On Activity ... handlers), each an action flow.
        foreach (var key in ProcessCallbacks)
            if (spec.TryGetProperty(key, out var cb)) Callback(process, key, cb, $"{label}.{key}");
        if (spec.TryGetProperty("inputs", out var inputs))
        {
            var parameters = JsonSerializer.Deserialize<List<VarSpec>>(inputs.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
                .Where(p => !Elements(Reflect.Get(process, "InputParameters")).Any(x => Model.NameOf(x) == p.Name)).ToList();   // an existing process keeps its inputs
            Variables(process, "CreateInputParameter", parameters, label);
            // Process inputs default to mandatory, unlike action inputs. Launch On allows only its implicit input to be mandatory.
            foreach (var parameter in parameters)
                if (Elements(Reflect.Get(process, "InputParameters")).FirstOrDefault(p => Model.NameOf(p) == parameter.Name) is { } p)
                    Write(m, p, "IsMandatory", parameter.Mandatory ?? false);
        }
        foreach (var p in spec.EnumerateObject().Where(p => p.Name is not ("name" or "launchOn" or "inputs" or "flow") && !ProcessCallbacks.Contains(p.Name)))
            Queue(label, m, process, p.Name, p.Value);

        var specs = spec.TryGetProperty("flow", out var flow) ? flow.EnumerateArray().ToList() : new();
        if (!isNew && specs.Count == 0) return;   // an existing process without a flow keeps its connections
        var first = ProcessFlow(process, specs, label);
        if (!specs.Any(s => s.GetProperty("kind").GetString()!.Equals("start", StringComparison.OrdinalIgnoreCase)))
        {
            var start = ProcessNode(process, "Start");
            targets.Add((label + ".start", () => SetNext(start, first ?? ProcessNode(process, "End"), null, label)));
        }
    }

    // insertNodes with a process: the new nodes go between "after" and where it went.
    void ProcessInsert(object process, JsonElement edit, string label, string afterName)
    {
        if (!edit.TryGetProperty("flow", out var flow) || flow.ValueKind != JsonValueKind.Array || flow.GetArrayLength() == 0)
            throw new ModelError("insertNodes requires a nonempty flow");
        var specs = flow.EnumerateArray().ToList();
        var after = ProcessNode(process, afterName);
        var connector = edit.TryGetProperty("connector", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (connector == "next") connector = null;
        object? old;
        if (IsKind(after, "decision"))
        {
            var outcome = OutcomeOf(connector, label);
            old = Outgoing(after).Where(o => Outcome(o).Equals(outcome, StringComparison.OrdinalIgnoreCase)).Select(TargetOf).FirstOrDefault();
        }
        else
        {
            var outs = Outgoing(after).Select(TargetOf).Where(t => t is not null).Distinct().ToList();
            var branch = edit.TryGetProperty("branch", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            if (branch is not null)
            {
                old = outs.FirstOrDefault(t => string.Equals(Model.NameOf(t!), branch, StringComparison.OrdinalIgnoreCase) || (IsKind(t!, "end") && branch.Equals("End", StringComparison.OrdinalIgnoreCase)))
                    ?? throw new ModelError($"{afterName} does not go to '{branch}' (it goes to {string.Join(", ", outs.Select(t => Model.NameOf(t!) ?? "End"))})");
                var first0 = ProcessFlow(process, specs, label, exit: old, allowExisting: false)!;
                targets.Add((label + ".after", () => SetNexts(after, outs.Select(t => ReferenceEquals(t, old) ? first0 : t!).ToList(), label)));   // the other branches stay
                return;
            }
            if (outs.Count > 1) throw new ModelError($"{afterName} goes to {outs.Count} nodes at the same time ({string.Join(", ", outs.Select(t => Model.NameOf(t!) ?? "End"))}); say which branch with \"branch\": \"<next node>\"");
            old = outs.FirstOrDefault();
        }
        var first = ProcessFlow(process, specs, label, exit: old ?? ProcessNode(process, "End"), allowExisting: false)!;
        targets.Add((label + ".after", () => SetNext(after, first, connector, label)));
    }

    // delete of a process node: the nodes that came to it go where it went.
    void ProcessDeleteReconnect(IObjectSignature node, string path)
    {
        if (IsKind(node, "start")) throw new ModelError($"'{path}' is the Start of the process; it cannot be deleted");
        var incoming = Elements(Reflect.Get(node, "IncomingConnectors")).ToList();
        if (IsKind(node, "decision"))
        {
            if (incoming.Count > 0) throw new ModelError($"'{path}' is a Decision with two exits: first connect the nodes before it to the one that should follow (connect), then delete it");
            return;
        }
        if (IsKind(node, "end"))
        {
            if (incoming.Count > 0 && !ProcessNodes(node.Parent!).Any(n => IsKind(n, "end") && !ReferenceEquals(n, node)))
                throw new ModelError($"'{path}' is the only End: connect the nodes before it elsewhere first");
            var other = ProcessNodes(node.Parent!).FirstOrDefault(n => IsKind(n, "end") && !ReferenceEquals(n, node));
            foreach (var c in incoming) Reflect.Property(c, "Target", writable: true)!.SetValue(c, other);
            return;
        }
        var outs = Outgoing(node).Select(TargetOf).Where(t => t is not null && !ReferenceEquals(t, node)).Distinct().ToList();
        if (outs.Count > 1) throw new ModelError($"'{path}' goes to {outs.Count} nodes; connect the nodes before it first, then delete it");
        var next = outs.FirstOrDefault() ?? ProcessNode(node.Parent!, "End");
        foreach (var c in incoming) Reflect.Property(c, "Target", writable: true)!.SetValue(c, next);
    }

    // replaceNode on a process node. The same kind and name: the node is updated where it is. Another kind or name: a new node takes its
    // place (what came to the old one comes to it), goes where the old one went unless the spec says, and the old one goes.
    void ProcessReplace(IObjectSignature old, JsonElement spec, string label, string path)
    {
        var process = old.Parent!;
        var kind = spec.TryGetProperty("kind", out var k) ? k.GetString()! : throw new ModelError("'node' needs a kind");
        var oldName = Model.NameOf(old) ?? "";
        var name = spec.TryGetProperty("name", out var n) ? n.GetString()! : oldName;
        string? Next(string key) => spec.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (IsKind(old, kind) && name.Equals(oldName, StringComparison.OrdinalIgnoreCase))
        {
            ConfigureProcessNode(old, kind, spec, path);
            if (spec.TryGetProperty("next", out var nextList)) targets.Add(($"{path}.next", () => SetNexts(old, Names(nextList).Select(t => ProcessNode(process, t)).ToList(), path)));
            foreach (var (key, connector) in new[] { ("true", "true"), ("false", "false") })
                if (Next(key) is { } to) targets.Add(($"{path}.{key}", () => SetNext(old, ProcessNode(process, to), connector, path)));
            return;
        }
        if (IsKind(old, "start") || IsKind(old, "end")) throw new ModelError($"'{path}' is the {(IsKind(old, "start") ? "Start" : "End")} of the process; it is not replaced");
        var keepAll = IsKind(old, "decision") ? [] : Outgoing(old).Select(TargetOf).Where(t => t is not null).Distinct().Select(t => t!).ToList();
        var keep = keepAll.FirstOrDefault();
        var sameName = name.Equals(oldName, StringComparison.OrdinalIgnoreCase);
        var made = MakeNode(process, kind, sameName ? name + "_replacing" : name, label);
        foreach (var c in Elements(Reflect.Get(old, "IncomingConnectors")).ToList()) Reflect.Property(c, "Target", writable: true)!.SetValue(c, made);
        ConfigureProcessNode(made, kind, spec, path);
        var wiring = new List<(string key, string? to, string? connector)> { ("next", Next("next"), null), ("true", Next("true"), "true"), ("false", Next("false"), "false") };
        targets.Add(($"{path}.flow", () =>
        {
            if (IsKind(made, "decision"))
            {
                foreach (var (key, to, connector) in wiring.Where(w => w.connector is not null))
                    SetNext(made, to is null ? keep ?? ProcessNode(process, "End") : ProcessNode(process, to), connector, path);
            }
            else if (!IsKind(made, "end"))
                SetNexts(made, spec.TryGetProperty("next", out var nx) ? Names(nx).Select(t => ProcessNode(process, t)).ToList() : keepAll.Count > 0 ? keepAll : [ProcessNode(process, "End")], path);
        }));
        old.Delete();
        if (sameName && Reflect.Property(made, "Label", true) is { } lp) lp.SetValue(made, name);
        if (sameName && Reflect.Property(made, "Name", true) is { } np) np.SetValue(made, name);
    }
}
