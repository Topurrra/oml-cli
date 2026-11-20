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
        if (Elements(Reflect.Get(m, collection)).FirstOrDefault(e => Model.NameOf(e) == name) is { } found)
        {
            if (collection == "Processes") { if (spec.EnumerateObject().Count() > 1) NotChanged($"{Kind(collection)} {name}", "settings and activities", "edits on its flow (insertNodes, set, delete)"); }
            else ReusePlatform(collection, found, spec);
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

    static Type ProcessNodeType(string kind) => Reflect.Types(typeof(OutSystems.Model.Processes.IProcess).Assembly)
        .FirstOrDefault(t => t.IsInterface && t.Namespace == "OutSystems.Model.Processes.Nodes"
            && t.Name.Equals($"I{kind}Node", StringComparison.OrdinalIgnoreCase))
        ?? throw new ModelError($"unknown process node kind '{kind}'");

    static readonly string[] ProcessCallbacks = ["onProcessStart", "onActivityOpen", "onActivityStart", "onActivityReady", "onActivityClose", "onActivitySkip"];
    static readonly string[] NodeCallbacks = ["onOpen", "onReady", "onStart", "onClose", "onSkip"];

    // A process or process node callback: { "locals": [...], "flow": [...] } or the flow array alone. The model creates it (CreateOnProcessStart,
    // CreateOnClose, ...); a second apply reuses the one there.
    void Callback(object owner, string key, JsonElement value, string where)
    {
        var property = char.ToUpperInvariant(key[0]) + key[1..];
        if (Reflect.Property(owner, property) is null) throw new ModelError($"{key} is not a callback of this element (process: {string.Join(", ", ProcessCallbacks)}; nodes: {string.Join(", ", NodeCallbacks)})");
        var callback = Reflect.Get(owner, property) ?? Reflect.Call(owner, "Create" + property) ?? throw new ModelError($"the model did not create {key} on this build");
        var flow = value.ValueKind == JsonValueKind.Array ? value : value.TryGetProperty("flow", out var f) ? f : default;
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("locals", out var locals))
            Variables(callback, "CreateLocalVariable", JsonSerializer.Deserialize<List<VarSpec>>(locals.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!, where);
        var body = flow.ValueKind == JsonValueKind.Array ? flow.EnumerateArray().ToList() : new();
        foreach (var node in Elements(Reflect.Get(callback, "Nodes")).OfType<IObjectSignature>().Where(n => n is not OutSystems.Model.Logic.Nodes.IStartNode).ToList()) node.Delete();
        flows.Add((where, () => Flow(callback, body, where)));
    }

    // An entity event that launches a process, or closes or starts a node: "Order" (Create) or { "entity": "Order", "event": "Update" }.
    void Trigger(object owner, string property, JsonElement value, string where)
    {
        var trigger = Reflect.Get(owner, property) ?? throw new ModelError($"{where}: no {property} on this build");
        var (entityName, evt) = value.ValueKind == JsonValueKind.String ? (value.GetString()!, "Create")
            : (value.GetProperty("entity").GetString()!, value.TryGetProperty("event", out var e) ? e.GetString()! : "Create");
        if (evt is not ("Create" or "Update")) throw new ModelError($"{where}: {property} event must be Create or Update, not '{evt}'");
        Write(m, trigger, "Entity", entityName);
        Write(m, trigger, "Action", evt);
    }
}
