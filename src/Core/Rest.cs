using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Types;

namespace OmlCli;

record RestSpec(string Name, string? BaseUrl = null, List<RestActionSpec>? Actions = null);
record RestActionSpec(string Name, string Method = "GET", string Url = "",
    List<RestParamSpec>? Inputs = null, List<RestParamSpec>? Outputs = null, List<JsonElement>? Flow = null);
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

    public static object? FindAction(IESpace m, string name, Type wanted)
    {
        var hits = Integrations(m, false).SelectMany(i => Actions(i).Where(a => wanted.IsInstanceOfType(a)
            && (Model.NameOf(a) == name || $"{Model.NameOf(i)}.{Model.NameOf(a)}" == name))).ToList();
        if (hits.Count > 1) throw new ModelError($"ambiguous REST method '{name}'; qualify it as Client.Method");
        return hits.SingleOrDefault();
    }

    public static IEnumerable<object> Inspect(IESpace m, bool service) => Integrations(m, service).Select(i => new
    {
        name = Model.NameOf(i), baseUrl = Reflect.Get(i, "BaseURL"),
        methods = Actions(i).Select(a => new { name = Model.NameOf(a), method = Reflect.Get(a, "HTTPMethod")?.ToString(), url = Reflect.Get(a, "URLPath") }),
    });
}

partial class Builder
{
    readonly Dictionary<(object client, ITypeSignature source), ITypeSignature> restTypes = new();

    public void Rest(List<RestSpec>? clients, List<RestSpec>? exposed)
    {
        foreach (var s in clients ?? new()) Try($"restClient {s.Name}", () => RestIntegration(s, false));
        foreach (var s in exposed ?? new()) Try($"restService {s.Name}", () => RestIntegration(s, true));
    }

    void RestIntegration(RestSpec s, bool service)
    {
        var label = $"{(service ? "restService" : "restClient")} {s.Name}";
        var same = m.Integrations.Where(i => Model.NameOf(i) == s.Name).ToList();
        if (same.Count > 0)
        {
            if (same.Count == 1 && RestModel.Contract(service).IsInstanceOfType(same[0])) existing.Add(label);
            else problems.Add($"{label}: name taken by another integration");
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
    }

    static void RestSet(object owner, string key, string value)
    {
        var p = Reflect.Property(owner, key, writable: true) ?? throw new ModelError($"no writable {key} on this build");
        if (p.PropertyType.IsEnum && !Enum.GetNames(p.PropertyType).Any(n => n.Equals(value, StringComparison.OrdinalIgnoreCase)))
            throw new ModelError($"unsupported {key} '{value}' on this build");
        p.SetValue(owner, p.PropertyType.IsEnum ? Enum.Parse(p.PropertyType, value, true) : value);
    }

    void RestAction(object integration, RestActionSpec s, bool service, string parent)
    {
        var label = $"{parent}.{s.Name}";
        if (RestModel.Actions(integration).Any(a => Model.NameOf(a) == s.Name))
            throw new ModelError($"duplicate method '{s.Name}'");
        var action = Reflect.Call(integration, "CreateAction", s.Name, services.NewKey())!;
        if (!Fresh(action, s.Name, label)) return;
        RestSet(action, "HTTPMethod", s.Method);
        var url = s.Url;
        foreach (var p in s.Inputs ?? new())
        {
            var location = (p.Location ?? "query").ToLowerInvariant();
            // Exposed URLPath contains only the path. URL inputs absent from its tokens are query inputs.
            if (!service && location == "query" && !url.Contains("{" + p.Name + "}", StringComparison.Ordinal))
                url += (url.Contains('?') ? "&" : "?") + p.Name + "={" + p.Name + "}";
        }
        RestSet(action, "URLPath", url);
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
            flows.Add((label, () => Flow(action, s.Flow ?? new(), label)));
        }
        else if (s.Flow is not null) throw new ModelError("consumed REST methods cannot have a flow");
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
