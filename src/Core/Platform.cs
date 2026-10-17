using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Types;

namespace OmlCli;

record AccessSpec(bool Anonymous = false, bool Registered = false, List<string>? Roles = null);

partial class Builder
{
    static string RequiredName(JsonElement spec) => spec.GetProperty("name").GetString()!;

    object? PlatformElement(string collection, string factory, JsonElement spec, params object[] prefix)
    {
        var name = RequiredName(spec);
        if (Elements(Reflect.Get(m, collection)).Any(e => Model.NameOf(e) == name))
        {
            existing.Add($"{collection} {name}");
            return null;
        }
        var element = Reflect.Call(m, factory, prefix.Concat(new object[] { name, services.NewKey() }).ToArray())!;
        if (!Fresh(element, name, $"{collection} {name}")) return null;
        created.Add($"{collection} {name}");
        return element;
    }

    static void Write(IESpace module, object target, string key, object value) =>
        Reflect.Plan(module, target, key, JsonSerializer.SerializeToElement(value)).apply();

    public void PlatformData(Spec spec)
    {
        // Adapted from OutSystems/outsystems11-mcp examples/AddSiteProperty.cs (MIT).
        var defaults = new List<(object property, string value)>();
        foreach (var s in spec.Roles ?? new()) Try($"role {RequiredName(s)}", () =>
        {
            if (PlatformElement("Roles", "CreateRole", s) is not { } role) return;
            foreach (var p in s.EnumerateObject().Where(p => p.Name != "name"))
                Reflect.Plan(m, role, p.Name == "persistent" ? "IsPersistent" : p.Name, p.Value).apply();
        });
        foreach (var s in spec.SiteProperties ?? new()) Try($"site property {RequiredName(s)}", () =>
        {
            if (PlatformElement("SiteProperties", "CreateSiteProperty", s, false) is not { } property) return;
            var typeName = s.TryGetProperty("type", out var type) ? type.GetString()! : "Text";
            if (Model.Resolve(m, typeName) is not IBasicType basic) throw new ModelError($"site properties need a basic type, got '{typeName}'");
            Reflect.Property(property, "DataType", true)!.SetValue(property, basic);
            foreach (var p in s.EnumerateObject().Where(p => p.Name is not ("name" or "type" or "default")))
                if (p.Name == "multiTenant") Write(m, property, "IsMultiTenant", p.Value.GetBoolean() ? "Yes" : "No");
                else Reflect.Plan(m, property, p.Name, p.Value).apply();
            if (s.TryGetProperty("default", out var value)) defaults.Add((property, value.GetString()!));
        });
        foreach (var (property, value) in defaults)
            Try($"site property {Model.NameOf(property)}.default", () => Reflect.Call(property, "SetDefaultValue", value));
    }

    public void PlatformLogic(Spec spec)
    {
        // Adapted from OutSystems/outsystems11-mcp examples/AddTimer.cs (MIT).
        foreach (var s in spec.Timers ?? new()) Try($"timer {RequiredName(s)}", () =>
        {
            if (PlatformElement("Timers", "CreateTimer", s) is not { } timer) return;
            foreach (var p in s.EnumerateObject().Where(p => p.Name != "name"))
            {
                if (p.Name == "action")
                {
                    var action = m.ServerActions.SingleOrDefault(a => a.Name == p.Value.GetString())
                        ?? throw new ModelError($"no module server action '{p.Value.GetString()}'");
                    Reflect.Property(timer, "Action", true)!.SetValue(timer, action);
                }
                else Queue($"timer {RequiredName(s)}", m, timer, p.Name == "timeout" ? "TimeoutInMinutes" : p.Name, p.Value);
            }
        });
        foreach (var s in spec.Processes ?? new()) Try($"process {RequiredName(s)}", () => Process(s));
    }

    void SetAccess(object owner, AccessSpec access)
    {
        // Adapted from OutSystems/outsystems11-mcp examples/SetScreenAccess.cs (MIT).
        var roles = Reflect.Get(owner, "Roles") ?? throw new ModelError("element has no role access collection");
        var wanted = (access.Roles ?? new()).Select(name => Refs.Find(m, name, typeof(OutSystems.Model.Logic.IRoleSignature))).ToList();
        if (access.Anonymous) wanted.Add(Reflect.Get(m, "AnonymousRole")!);
        if (access.Registered) wanted.Add(Reflect.Get(m, "RegisteredRole")!);
        foreach (var role in Elements(roles).ToList()) Reflect.Call(roles, "Remove", role);
        foreach (var role in wanted.Distinct()) Reflect.Call(roles, "Add", role);
    }

    static Type ProcessNodeType(string kind) => typeof(OutSystems.Model.Processes.IProcess).Assembly.GetTypes()
        .FirstOrDefault(t => t.IsInterface && t.Namespace == "OutSystems.Model.Processes.Nodes"
            && t.Name.Equals($"I{kind}Node", StringComparison.OrdinalIgnoreCase))
        ?? throw new ModelError($"unknown process node kind '{kind}'");

    void Process(JsonElement spec)
    {
        // Adapted from OutSystems/outsystems11-mcp examples/AddProcessWithHumanActivity.cs (MIT).
        if (PlatformElement("Processes", "CreateProcess", spec) is not { } process) return;
        var label = $"process {RequiredName(spec)}";
        if (spec.TryGetProperty("launchOn", out var entity))
        {
            var trigger = Reflect.Get(process, "LaunchOn")!;
            Write(m, trigger, "Entity", entity.GetString()!);
            Write(m, trigger, "Action", "Create");
        }
        if (spec.TryGetProperty("inputs", out var inputs))
        {
            var parameters = JsonSerializer.Deserialize<List<VarSpec>>(inputs.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Variables(process, "CreateInputParameter", parameters, label);
            // Process inputs default to mandatory, unlike action inputs. Launch On allows only its implicit input to be mandatory.
            foreach (var parameter in parameters)
                if (Elements(Reflect.Get(process, "InputParameters")).FirstOrDefault(p => Model.NameOf(p) == parameter.Name) is { } p)
                    Write(m, p, "IsMandatory", parameter.Mandatory);
        }
        foreach (var p in spec.EnumerateObject().Where(p => p.Name is not ("name" or "launchOn" or "inputs" or "flow")))
            Queue(label, m, process, p.Name, p.Value);

        var specs = spec.TryGetProperty("flow", out var flow) ? flow.EnumerateArray().ToList() : new();
        var nodes = new Dictionary<string, object>();
        object Make(string kind, string name)
        {
            if (nodes.ContainsKey(name)) throw new ModelError($"duplicate process node name '{name}'");
            var node = Reflect.CallGeneric(process, "CreateNode", ProcessNodeType(kind), name, services.NewKey())!;
            if (Reflect.Property(node, "Label", true) is { } lp) lp.SetValue(node, name);
            if ((Reflect.Property(node, "Name", true) is not null || Reflect.Property(node, "Label", true) is not null) && !Fresh(node, name, label))
                throw new ModelError($"process node '{name}' was renamed");
            nodes.Add(name, node);
            return node;
        }
        foreach (var s in specs)
            Make(s.GetProperty("kind").GetString()!, s.TryGetProperty("name", out var n) ? n.GetString()! : s.GetProperty("kind").GetString()!);
        var start = nodes.Values.FirstOrDefault(n => ProcessNodeType("start").IsInstanceOfType(n)) ?? Make("start", "Start");
        object Resolve(string name) => nodes.TryGetValue(name, out var node) ? node
            : name == "End" ? Make("end", "End") : throw new ModelError($"no process node '{name}'");
        void Link(object from, object to) => Reflect.Call(Reflect.Get(from, "Targets")
            ?? throw new ModelError("process node has no Targets"), "Add", to);
        if (!specs.Any(s => s.GetProperty("kind").GetString()!.Equals("start", StringComparison.OrdinalIgnoreCase)))
            targets.Add((label + ".start", () => Link(start, specs.Count > 0 ? nodes.Values.First() : Resolve("End"))));
        for (var i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            var index = i;
            var kind = s.GetProperty("kind").GetString()!.ToLowerInvariant();
            var name = s.TryGetProperty("name", out var n) ? n.GetString()! : s.GetProperty("kind").GetString()!;
            var node = nodes[name];
            var where = label + "/" + name;
            if (kind == "automaticactivity")
            {
                var body = s.TryGetProperty("flow", out var bodyFlow) ? bodyFlow.EnumerateArray().ToList() : new();
                if (s.TryGetProperty("action", out var action))
                {
                    var call = new Dictionary<string, object> { ["kind"] = "execute", ["action"] = action.GetString()! };
                    if (s.TryGetProperty("args", out var args)) call["args"] = args;
                    body.Insert(0, JsonSerializer.SerializeToElement(call));
                }
                flows.Add((where, () => Flow(node, body, where)));
            }
            if (s.TryGetProperty("roles", out var roleNames))
                references.Add((where + ".roles", () => SetAccess(node, new(Roles: roleNames.EnumerateArray().Select(r => r.GetString()!).ToList()))));
            if (kind == "humanactivity" && s.TryGetProperty("args", out var screenArgs))
                Defer(node, (where + ".args", () => Arguments(node, screenArgs)));
            foreach (var p in s.EnumerateObject().Where(p => p.Name is not ("kind" or "name" or "next" or "true" or "false" or "flow" or "action" or "args" or "roles" or "condition")))
                Queue(where, m, node, p.Name, p.Value);
            targets.Add((where + ".flow", () =>
            {
                if (kind == "end") return;
                if (kind == "decision")
                {
                    foreach (var (key, outcome) in new[] { ("true", "Yes"), ("false", "No") })
                        Reflect.Call(node, "CreateConnector", outcome, Resolve(s.GetProperty(key).GetString()!));
                    var condition = Elements(Reflect.Get(node, "Nodes")).OfType<OutSystems.Model.Logic.Nodes.IIfNode>().Single();
                    Defer(node, (where + ".condition", () => Reflect.Call(condition, "SetCondition", s.GetProperty("condition").GetString())));
                    return;
                }
                var next = s.TryGetProperty("next", out var target) ? Resolve(target.GetString()!)
                    : index + 1 < specs.Count ? nodes[specs[index + 1].TryGetProperty("name", out var nn) ? nn.GetString()! : specs[index + 1].GetProperty("kind").GetString()!]
                    : Resolve("End");
                Link(node, next);
            }));
        }
    }
}
