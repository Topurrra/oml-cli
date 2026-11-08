using System.Text.Json;
using System.Text.Json.Nodes;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Types;

namespace OmlCli;

record RestSpec(string Name, string? BaseUrl = null, List<RestActionSpec>? Actions = null,
    string? Authentication = null, bool? InternalAccessOnly = null, RestCallbackSpec? OnAuthentication = null,
    RestCallbackSpec? OnBeforeRequest = null, RestCallbackSpec? OnAfterResponse = null);
record RestActionSpec(string Name, string Method = "GET", string Url = "",
    List<RestParamSpec>? Inputs = null, List<RestParamSpec>? Outputs = null, List<JsonElement>? Flow = null, List<VarSpec>? Locals = null);
// The flow of a REST callback: consumed OnBeforeRequest and OnAfterResponse, exposed OnAuthentication. Their inputs and
// outputs come from Service Studio (Request, CustomizedRequest, Response, CustomizedResponse, Username, Password).
record RestCallbackSpec(List<VarSpec>? Locals = null, List<JsonElement>? Flow = null);
record RestParamSpec(string Name, string Type = "Text", string? Location = null, string? HeaderName = null,
    bool Mandatory = false);

static class RestModel
{
    public static Type Contract(bool service) => System.Reflection.Assembly.Load(
        "ServiceStudio.Plugin." + (service ? "RESTService" : "REST"))
        .GetType(service ? "ServiceStudio.Plugin.RESTService.IRestService" : "ServiceStudio.Plugin.REST.IRestClient", true)!;

    public static IEnumerable<object> Integrations(IESpace m, bool service) => m.Integrations.Where(Contract(service).IsInstanceOfType);
    public static IEnumerable<object> Actions(object integration) => Reflect.Get(integration, "Actions") is System.Collections.IEnumerable list
        ? list.Cast<object>() : Enumerable.Empty<object>();

    // Structures owned by consumed REST APIs, with the client's name. They hold the request and response types of the methods.
    public static IEnumerable<(string client, IStructureSignature structure)> Structures(IESpace m)
    {
        var contract = Contract(false);
        foreach (var client in m.Integrations.Where(contract.IsInstanceOfType))
            if (Reflect.Get(client, "Structures") is System.Collections.IEnumerable structures)
                foreach (var s in structures.OfType<IStructureSignature>())
                    yield return (Model.NameOf(client)!, s);
    }

    public static object? FindAction(IESpace m, string name, Type wanted)
    {
        var hits = Integrations(m, false).SelectMany(i => Actions(i).Where(a => wanted.IsInstanceOfType(a)
            && (Model.NameOf(a) == name || $"{Model.NameOf(i)}.{Model.NameOf(a)}" == name))).ToList();
        if (hits.Count > 1) throw new ModelError($"ambiguous REST method '{name}'; qualify it as Client.Method");
        return hits.SingleOrDefault();
    }

    public static IEnumerable<object> Inspect(IESpace m, bool service, History.View h) => OmlCli.Inspect.ByName(h.Keep(Integrations(m, service))).Select(i => new
    {
        name = Model.NameOf(i), baseUrl = Reflect.Get(i, "BaseURL"),
        methods = OmlCli.Inspect.ByName(Actions(i)).Select(a => new { name = Model.NameOf(a), method = Reflect.Get(a, "HTTPMethod")?.ToString(), url = Reflect.Get(a, "URLPath") }),
        lastModifiedBy = h.By(i), lastModifiedDate = h.On(i),
    });
}

partial class Builder
{
    readonly Dictionary<(object client, ITypeSignature source), ITypeSignature> restTypes = new();
    readonly HashSet<string> sentDefaults = new(); // structure.attribute with sendDefault that a REST copy took

    public void Rest(List<RestSpec>? clients, List<RestSpec>? exposed)
    {
        foreach (var s in clients ?? new()) Try($"restClient {s.Name}", () => RestIntegration(s, false));
        foreach (var s in exposed ?? new()) Try($"restService {s.Name}", () => RestIntegration(s, true));
        // sendDefault is a property of a REST copy; a structure no REST method of this spec uses gets no copy.
        foreach (var structure in (Reuse.Get(Raw as JsonObject, "structures") as JsonArray ?? new()).OfType<JsonObject>())
            foreach (var attribute in (Reuse.Get(structure, "attributes") as JsonArray ?? new()).OfType<JsonObject>())
                if (Reuse.Has(attribute, "sendDefault") && $"{Reuse.Get(structure, "name")}.{Reuse.Get(attribute, "name")}" is var full && !sentDefaults.Contains(full))
                    Warnings.Add($"{full}: sendDefault was not applied. Send Default Value belongs to the copy of a structure inside a consumed REST API; no REST method in this spec uses it.");
    }

    void RestIntegration(RestSpec s, bool service)
    {
        var label = $"{(service ? "restService" : "restClient")} {s.Name}";
        var same = m.Integrations.Where(i => Model.NameOf(i) == s.Name).ToList();
        if (same.Count > 0)
        {
            if (same.Count == 1 && RestModel.Contract(service).IsInstanceOfType(same[0])) { ReuseRest(same[0], s, service, label); existing.Add(label); }
            else problems.Add($"{label}: name taken by {NameClash.Describe(same[0])}");
            return;
        }
        // Adapted from OutSystems/outsystems11-mcp examples/AddRestClient.cs (MIT).
        // Headless CreateIntegration<T> cannot discover plugin concrete classes on this build.
        // Use the registered descriptor's model Kind factory, just as plugin widgets use descriptors.
        var contract = RestModel.Contract(service);
        var descriptor = ServiceStudio.PluginAPI.PluginProvider.Implementations.FirstOrDefault(d =>
            Reflect.Get(d, "ConcreteInterfaceType") as Type == contract)
            ?? throw new ModelError($"no registered descriptor for {contract.FullName}");
        var concrete = Reflect.Get(descriptor, "ConcreteClassType") as Type
            ?? throw new ModelError($"descriptor for {contract.FullName} has no concrete class on this build");
        var kindType = concrete.GetNestedType("Kind")
            ?? throw new ModelError($"{concrete.Name} has no Kind factory on this build");
        var kind = kindType.GetField("Instance")?.GetValue(null) ?? kindType.GetProperty("Instance")?.GetValue(null)
            ?? throw new ModelError($"{concrete.Name}.Kind has no Instance on this build");
        var integration = Reflect.Call(kind, "CreateInstanceInParent", m, s.Name, descriptor)!;
        if (!Fresh(integration, s.Name, label)) return;
        if (s.BaseUrl is not null) RestSet(integration, "BaseURL", s.BaseUrl);
        created.Add(label);
        foreach (var a in s.Actions ?? new()) Try($"{label}.{a.Name}", () => RestAction(integration, a, service, label));
        if (service) Try($"{label}.authentication", () => RestSecurity(integration, s, label));
        else Try($"{label}.callbacks", () => RestCallbacks(integration, s, label));
    }

    // authentication and internalAccessOnly of a new exposed API. Setting Authentication makes Service Studio create the
    // OnAuthentication callback, as in the IDE: Basic with Username and Password inputs, Custom without inputs.
    void RestSecurity(object integration, RestSpec s, string label)
    {
        if (s.OnBeforeRequest is not null || s.OnAfterResponse is not null)
            throw new ModelError("onBeforeRequest and onAfterResponse are for consumed REST APIs (restClients); an exposed API has onAuthentication");
        if (s.InternalAccessOnly is { } only) RestSet(integration, "InternalAccess", only ? "Yes" : "No");
        var kind = (s.Authentication ?? "none").ToLowerInvariant();
        if (kind is not ("none" or "basic" or "custom")) throw new ModelError($"authentication must be none, basic or custom, not '{s.Authentication}'");
        if (kind == "none")
        {
            if (s.OnAuthentication is not null) throw new ModelError("onAuthentication needs authentication basic or custom");
            // An explicit "none" is the spec's decision (for example a gateway, or the API's own key check); only a left-out key warns.
            if (s.InternalAccessOnly != true && s.Authentication is null)
                RestNotices.Add(("ExposedRestApiWithoutAuthentication", s.Name, $"exposed REST API {s.Name} has no authentication: anyone who can reach it can call it. Set \"authentication\" to basic or custom, or \"internalAccessOnly\": true, unless a gateway protects it."));
            return;
        }
        // In a module that references Users, Service Studio fills the Basic flow from a template the headless model does not have.
        try { RestSet(integration, "Authentication", kind == "basic" ? "Basic" : "Custom"); }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is InvalidOperationException)
        {
            throw new ModelError($"Basic authentication cannot be set outside Service Studio in a module that references Users: Service Studio fills its flow from a template the model does not have here. Leave \"authentication\" out and set Basic on {s.Name} in Service Studio, or use \"custom\" with \"onAuthentication\".");
        }
        var flow = OnAuthenticationFlow(integration) ?? throw new ModelError("the model did not create OnAuthentication on this build");
        if (s.OnAuthentication is { } callback)
        {
            CallbackFlow(flow, callback, $"{label}.onAuthentication");
            return;
        }
        RestNotices.Add(("RestAuthenticationNotImplemented", s.Name, $"exposed REST API {s.Name}: OnAuthentication has no logic yet and refuses every call (it raises an invalid login). Give \"onAuthentication\" a flow."));
    }

    // Warnings about exposed APIs; Apply lists them in newWarnings.
    internal readonly List<(string id, string owner, string message)> RestNotices = new();

    // The OnAuthentication callback of an exposed API.
    static object? OnAuthenticationFlow(object integration) => Elements(Reflect.Get(integration, "CallbackFlows"))
        .Concat(Elements(Reflect.Get(integration, "ActionFlows")))
        .FirstOrDefault(f => Model.NameOf(f) == "OnAuthentication");

    // A callback's flow: the model seeds Start and End (Basic: a raise node); the spec's nodes replace them.
    void CallbackFlow(object callback, RestCallbackSpec s, string label)
    {
        Variables(callback, "CreateLocalVariable", s.Locals, label);
        // Start stays: the callback's other seeded nodes go, and the flow builder connects Start to the spec's first node.
        foreach (var node in Elements(Reflect.Get(callback, "Nodes")).OfType<IObjectSignature>().Where(n => n is not OutSystems.Model.Logic.Nodes.IStartNode).ToList()) node.Delete();
        flows.Add((label, () => Flow(callback, s.Flow ?? new(), label)));
    }

    // onBeforeRequest and onAfterResponse of a consumed API, created when the client has none.
    void RestCallbacks(object integration, RestSpec s, string label)
    {
        if (s.Authentication is not null || s.InternalAccessOnly is not null || s.OnAuthentication is not null)
            throw new ModelError("authentication, internalAccessOnly and onAuthentication are for exposed REST APIs (restServices)");
        foreach (var (key, property, factory, callback) in new[]
                 { ("onBeforeRequest", "OnBeforeRequestCallback", "CreateOnBeforeRequestCallback", s.OnBeforeRequest),
                   ("onAfterResponse", "OnAfterResponseCallback", "CreateOnAfterResponseCallback", s.OnAfterResponse) })
        {
            if (callback is null) continue;
            var where = $"{label}.{key}";
            if (Reflect.Get(integration, property) is { } old)
            {
                ReuseFlow(old, callback.Flow, where);
                existing.Add(where);
                continue;
            }
            var made = Reflect.Call(integration, factory) ?? throw new ModelError($"the model did not create {key} on this build");
            CallbackFlow(made, callback, where);
            created.Add(where);
        }
    }

    static void RestSet(object owner, string key, string value)
    {
        var p = Reflect.Property(owner, key, writable: true) ?? throw new ModelError($"no writable {key} on this build");
        if (p.PropertyType.IsEnum && !Enum.GetNames(p.PropertyType).Any(n => n.Equals(value, StringComparison.OrdinalIgnoreCase)))
            throw new ModelError($"unsupported {key} '{value}' on this build");
        p.SetValue(owner, p.PropertyType.IsEnum ? Enum.Parse(p.PropertyType, value, true) : value);
    }

    // The URLPath a method gets: a consumed method's query inputs that are not in its URL are appended.
    static string RestUrl(RestActionSpec s, bool service)
    {
        var url = s.Url;
        foreach (var p in s.Inputs ?? new())
        {
            var location = (p.Location ?? "query").ToLowerInvariant();
            // Exposed URLPath contains only the path. URL inputs absent from its tokens are query inputs.
            if (!service && location == "query" && !url.Contains("{" + p.Name + "}", StringComparison.Ordinal))
                url += (url.Contains('?') ? "&" : "?") + p.Name + "={" + p.Name + "}";
        }
        return url;
    }

    void RestAction(object integration, RestActionSpec s, bool service, string parent)
    {
        var label = $"{parent}.{s.Name}";
        if (RestModel.Actions(integration).Any(a => Model.NameOf(a) == s.Name))
            throw new ModelError($"duplicate method '{s.Name}'");
        var action = Reflect.Call(integration, "CreateAction", s.Name, services.NewKey())!;
        if (!Fresh(action, s.Name, label)) return;
        RestSet(action, "HTTPMethod", s.Method);
        RestSet(action, "URLPath", RestUrl(s, service));
        if (!service)
        {
            RestSet(action, "RequestFormat", RestFormat((s.Inputs ?? new()).Where(p => p.Location?.Equals("body", StringComparison.OrdinalIgnoreCase) == true)));
            RestSet(action, "ResponseFormat", RestFormat((s.Outputs ?? new()).Where(p => p.Location is null || p.Location.Equals("body", StringComparison.OrdinalIgnoreCase))));
        }
        foreach (var p in s.Inputs ?? new()) RestParameter(integration, action, p, false, service, label, s.Url);
        foreach (var p in s.Outputs ?? new()) RestParameter(integration, action, p, true, service, label, s.Url);
        if (service)
        {
            // Adapted from OutSystems/outsystems11-mcp examples/AddExposedRestApi.cs (MIT).
            // REST methods seed a Start and End; remove them before using the shared flow builder.
            foreach (var node in Elements(Reflect.Get(action, "Nodes")).OfType<IObjectSignature>().ToList()) node.Delete();
            Variables(action, "CreateLocalVariable", s.Locals, label);
            flows.Add((label, () => Flow(action, s.Flow ?? new(), label)));
        }
        else if (s.Flow is not null || s.Locals is not null) throw new ModelError("consumed REST methods cannot have a flow or locals");
        created.Add(label);
    }

    string RestFormat(IEnumerable<RestParamSpec> parameters)
    {
        var body = parameters.ToList();
        if (body.Count == 0) return "None";
        if (body.Count > 1) throw new ModelError("REST supports a single body parameter; use a structure for multiple fields");
        var type = Model.Resolve(m, body[0].Type) ?? throw new ModelError(Model.UnknownType(body[0].Type));
        return ReferenceEquals(type, m.TextType) ? "PlainText" : ReferenceEquals(type, m.BinaryDataType) ? "Binary" : "JSON";
    }

    ITypeSignature RestType(object client, ITypeSignature source)
    {
        if (source is IListType list) return m.GetOrCreateListType(RestType(client, list.ElementType));
        if (source is not IStructureSignature structure) return source;
        if (restTypes.TryGetValue((client, source), out var prior)) return prior;
        var name = Model.NameOf(client) + structure.Name;
        var copy = (IStructure)Reflect.Call(client, "CreateStructure", name, services.NewKey())!;
        if (!Fresh(copy, name, $"REST DTO {name}")) throw new ModelError($"cannot create REST DTO '{name}'");
        restTypes[(client, source)] = copy;
        foreach (var attr in structure.Attributes)
        {
            var made = (IAttribute)Reflect.Call(copy, "CreateAttribute", attr.Name, services.NewKey())!;
            if (!Fresh(made, attr.Name, $"REST DTO {structure.Name}.{attr.Name}")) continue;
            Reflect.Property(made, "DataType", writable: true)!.SetValue(made, RestType(client, attr.DataType));
            made.Length = attr.Length;
            made.Decimals = attr.Decimals;
            made.IsMandatory = attr.IsMandatory;
            // Name in JSON comes with the copy; Send Default Value exists only on REST copies, so the spec's key lands here.
            if (attr.NameInJSON is { Length: > 0 } json && Reflect.Property(made, "NameInJSON", writable: true) is { } jsonName) jsonName.SetValue(made, json);
            if (Reuse.Get(Reuse.Item(Reuse.Item(Raw, "structures", structure.Name), "attributes", attr.Name), "sendDefault") is JsonValue flag && flag.TryGetValue<bool>(out var send))
            {
                RestSet(made, "DefaultValueBehavior", send ? "Send" : "DontSend");
                sentDefaults.Add($"{structure.Name}.{attr.Name}");
            }
        }
        created.Add($"REST DTO {name}");
        return copy;
    }

    void RestParameter(object integration, object action, RestParamSpec s, bool output, bool service, string parent, string url)
    {
        var label = $"{parent}.{s.Name}";
        var type = Model.Resolve(m, s.Type) ?? throw new ModelError($"{label}: {Model.UnknownType(s.Type)}");
        var location = (s.Location ?? (output ? "body" : "query")).ToLowerInvariant();
        if (output ? location is not ("body" or "header") : location is not ("url" or "query" or "body" or "header"))
            throw new ModelError($"{label}: unsupported location '{location}'");
        if (!output && location == "url" && !url.Contains("{" + s.Name + "}", StringComparison.Ordinal))
            throw new ModelError($"{label}: url input requires a matching {{{s.Name}}} placeholder");
        var p = Reflect.Call(action, output ? "CreateOutputParameter" : "CreateInputParameter", s.Name, services.NewKey())!;
        if (!Fresh(p, s.Name, label)) return;
        Reflect.Property(p, "DataType", writable: true)!.SetValue(p, service ? type : RestType(integration, type));
        if (!output) Reflect.Property(p, "IsMandatory", writable: true)!.SetValue(p, s.Mandatory || location == "url");
        var placement = location is "query" or "url" ? (service ? "URL" : "UrlPlacement")
            : service ? location : location + "Placement";
        RestSet(p, service ? (output ? "SendIn" : "ReceiveIn") : (output ? "OutputPlacement" : "InputPlacement"), placement);
        RestSet(p, service ? (output ? "SendAs" : "ReceiveAs") : "OriginalName", s.HeaderName ?? s.Name);
    }
}
