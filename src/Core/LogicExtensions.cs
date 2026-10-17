using System.Text.Json;
using OutSystems.Model;
using OutSystems.Model.Logic.Nodes;
using OutSystems.Model.UI;

namespace OmlCli;

record ModuleVariableSpec(string Name, string Type = "Text", string? Default = null, bool? IsSecure = null);
record OnExceptionSpec(string Name = "OnException", string Flow = "MainFlow", List<JsonElement>? Nodes = null, List<VarSpec>? Locals = null);

partial class Builder
{
    public void LogicExtensions(Spec spec)
    {
        ModuleVariables(spec.ClientVariables, true);
        ModuleVariables(spec.SessionVariables, false);
        if (spec.OnException is { } handler) Try("onException", () => ExceptionAction(handler));
        if (spec.GlobalExceptionHandler is { } name) Try("globalExceptionHandler", () =>
        {
            if (WebUi.IsTraditional(m) || m.Kind.ToString() == "Service")
                throw new ModelError("globalExceptionHandler requires a Reactive or Mobile module");
            var found = ElementPaths.Resolve(m, name);
            if (found is not IOnExceptionAction)
            {
                if (!m.ClientActions.Any(a => ReferenceEquals(a, found)))
                    throw new ModelError("expected an OnException action or module client action");
                found = (IObjectSignature)ExceptionAction(new OnExceptionSpec("Handle" + Model.NameOf(found), Nodes: new()
                {
                    JsonSerializer.SerializeToElement(new { kind = "exceptionHandler", name = "OnAllExceptions", exception = "AllExceptions" }),
                    JsonSerializer.SerializeToElement(new { kind = "execute", action = name })
                }));
            }
            // The handler is nested in a UI flow, so assign the resolved object directly.
            Reflect.Property(m, "GlobalExceptionHandler", true)!.SetValue(m, found);
        });
        foreach (var (key, destination) in spec.SystemEvents ?? new()) Try("systemEvents." + key, () =>
        {
            var property = new[] { "OnApplicationReady", "OnApplicationResume", "OnSessionStart", "OnBeginWebRequest" }
                .SingleOrDefault(n => n.Equals(key, StringComparison.OrdinalIgnoreCase))
                ?? throw new ModelError($"unknown system event '{key}'");
            var client = property is "OnApplicationReady" or "OnApplicationResume";
            var wanted = client ? typeof(OutSystems.Model.Logic.IClientActionSignature) : typeof(OutSystems.Model.Logic.IServerActionSignature);
            Refs.Find(m, destination, wanted);
            if (Reflect.Get(m, property) is not null)
                throw new ModelError($"{property} already exists; edit its flow instead");
            var ev = Reflect.Call(m, "Create" + property)!;
            flows.Add((property, () => Flow(ev, new() { JsonSerializer.SerializeToElement(new { kind = "execute", action = destination }) }, property)));
            created.Add("system event " + property);
        });
    }

    void ModuleVariables(List<ModuleVariableSpec>? specs, bool client)
    {
        var collection = client ? "ClientVariables" : "SessionVariables";
        foreach (var s in specs ?? new()) Try(collection + "." + s.Name, () =>
        {
            if (Elements(Reflect.Get(m, collection)).Any(v => Model.NameOf(v) == s.Name))
            { existing.Add(collection + " " + s.Name); return; }
            var variable = client ? Reflect.Call(m, "CreateClientVariable", s.Name, services.NewKey())!
                : Reflect.Call(m, "CreateSessionVariable", false, s.Name, services.NewKey())!;
            if (!Fresh(variable, s.Name, collection)) return;
            var type = Model.Resolve(m, s.Type) ?? throw new ModelError(Model.UnknownType(s.Type));
            Reflect.Property(variable, "DataType", true)!.SetValue(variable, type);
            if (s.IsSecure is { } secure) Write(m, variable, "IsSecure", secure);
            if (s.Default is { } value) Defer(variable, (collection + "." + s.Name + ".default", () => Reflect.Call(variable, "SetDefaultValue", value)));
            created.Add(collection + " " + s.Name);
        });
    }

    object ExceptionAction(OnExceptionSpec s)
    {
        if (m.Kind.ToString() == "Service")
            throw new ModelError("onException requires a module with UI flows (Reactive, Mobile or Traditional Web)");
        // Traditional Web UI flows have the same OnException handler slot as Reactive ones.
        var web = WebUi.IsTraditional(m);
        object? flow = web ? m.WebFlows.FirstOrDefault(f => f.Name == s.Flow) : m.MobileFlows.FirstOrDefault(f => f.Name == s.Flow);
        if (flow is null)
        {
            flow = web ? m.CreateWebFlow(s.Flow, services.NewKey()) : m.CreateMobileFlow(s.Flow, services.NewKey());
            if (!Fresh(flow, s.Flow, "onException flow")) throw new ModelError("UI flow name is taken");
            created.Add("flow " + s.Flow);
        }
        var handler = Reflect.Get(flow, "OnExceptionHandler");
        if (handler is not null)
        {
            if (Model.NameOf(handler) != s.Name) throw new ModelError($"flow {s.Flow} already has an exception handler");
            existing.Add("onException " + s.Name);
            return handler;
        }
        handler = Reflect.Call(flow, "CreateOnExceptionHandler", s.Name)!;
        if (!Fresh(handler, s.Name, "onException")) throw new ModelError("handler name is taken");
        Body(handler, new ActionSpec(s.Name, Locals: s.Locals, Flow: s.Nodes), "onException " + s.Name);
        created.Add("onException " + s.Name);
        return handler;
    }

    void CheckLogicNode(object action, object node, JsonElement spec, string kind, string where)
    {
        if (spec.TryGetProperty("attributeSelection", out var attributes))
            Defer(node, (where + ".attributeSelection", () =>
            {
                foreach (var attribute in attributes.EnumerateArray()) Reflect.Call(node, "AddAttributeSelection", attribute.GetString());
            }));
        if (node is IExceptionHandlerNode)
        {
            if (!spec.TryGetProperty("exception", out _)) throw new ModelError("exceptionHandler requires exception");
            targets.Add((where + ".exception", () =>
            {
                var exception = Reflect.Get(node, "Exception");
                if (exception is not null && Elements(Reflect.Get(action, "Nodes"))
                    .Count(n => n is IExceptionHandlerNode && ReferenceEquals(Reflect.Get(n, "Exception"), exception)) > 1)
                    throw new ModelError("only one handler per exception is allowed in an action");
            }));
        }
        if (kind.Equals("sendEmail", StringComparison.OrdinalIgnoreCase) && !spec.TryGetProperty("email", out _))
            throw new ModelError("sendEmail requires email: an existing Email screen name; this node does not create an Email screen");
    }
}
