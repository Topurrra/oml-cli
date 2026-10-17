using OutSystems.Model;
using System.Text.Json;
using OutSystems.Model.Applications;
using OutSystems.Model.Logic;
using OutSystems.Model.Types;
using OutSystems.Model.UI;

namespace OmlCli;

static class Inspect
{
    public static object Module(IESpace m)
    {
        object Attr(IAttributeSignature a) => new
        {
            name = a.Name,
            type = Model.Describe(m, a.DataType),
            length = a.Length,
            decimals = a.Decimals,
            mandatory = a.IsMandatory,
        };
        object Params(IActionSignature a) => new
        {
            name = Model.NameOf(a),
            @public = Reflect.Get(a, "Public") as bool?,
            inputs = a.InputParameters.Select(p => new { name = p.Name, type = Model.Describe(m, p.DataType) }),
            outputs = a.OutputParameters.Select(p => new { name = p.Name, type = Model.Describe(m, p.DataType) }),
        };

        return new
        {
            name = ((IModule)m).Name,
            module = ModuleSettings.Inspect(m),
            kind = m.Kind.ToString(),
            content = Content.Inspect(m),
            defaultScreen = m.DefaultScreen is { } landing ? Model.NameOf(landing) : null,
            entities = m.Entities.Select(e => new { name = e.Name, @public = e.Public, attributes = e.Attributes.Select(Attr) }),
            staticEntities = m.Entities.Where(e => Reflect.Get(e, "EntityKind")?.ToString() == "Static")
                .Cast<OutSystems.Model.Data.IStaticEntity>().Select(e => new { name = e.Name, @public = e.Public,
                    records = e.Records.Select(r => new { identifier = r.Identifier,
                        values = r.AttributeValues.ToDictionary(v => v.Attribute.Name, v => v.Value?.Text) }) }),
            structures = m.Structures.Select(s => new { name = s.Name, @public = s.Public, attributes = s.Attributes.Select(Attr) }),
            serverActions = m.ServerActions.Select(Params),
            serviceActions = m.ServiceActions.Select(Params),
            clientActions = m.ClientActions.Select(Params),
            restClients = RestModel.Inspect(m, service: false),
            restServices = RestModel.Inspect(m, service: true),
            screens = Screens(m).Where(s => !IsBlock(s.screen)).Select(s => ScreenRow(s.flow, s.screen)),
            blocks = Screens(m).Where(s => IsBlock(s.screen)).Select(s => ScreenRow(s.flow, s.screen)),
            timers = m.Timers.Select(t => new { name = Model.NameOf(t), action = t.Action is { } a ? Model.NameOf(a) : null, schedule = t.Schedule,
                timeout = t.TimeoutInMinutes, priority = t.Priority.ToString(), description = t.Description }),
            processes = m.Processes.Select(p => new { name = Model.NameOf(p), launchOn = Reflect.Get(p.LaunchOn, "Entity") is { } e ? Model.NameOf(e) : null,
                inputs = p.InputParameters.Select(i => new { name = i.Name, type = Model.Describe(m, i.DataType) }),
                nodes = p.Nodes.Select(n => new { name = Model.NameOf(n), kind = ElementPaths.Kind(n) }) }),
            roles = m.Roles.Select(r => new { name = Model.NameOf(r), @public = Reflect.Get(r, "Public") as bool?, description = r.Description, persistent = r.IsPersistent,
                actions = new[] { r.CheckRoleAction, r.GrantRoleAction, r.RevokeRoleAction }.Where(a => a is not null).Select(a => Model.NameOf(a!)) }),
            siteProperties = m.SiteProperties.Select(p => new { name = p.Name, type = Model.Describe(m, p.DataType),
                @default = p.DefaultValue?.Text, multiTenant = p.IsMultiTenant.ToString(), description = p.Description }),
            exceptions = m.UserExceptions.Select(e => new { name = e.Name, @public = Reflect.Get(e, "Public") as bool? }),
            references = m.References.Select(r => new
            {
                module = r.Name,
                entities = r.Entities.Select(e => e.Name),
                structures = r.Structures.Select(s => s.Name),
                serverActions = r.ServerActions.Select(a => Model.NameOf(a)),
                clientActions = r.ClientActions.Select(a => Model.NameOf(a)),
                serviceActions = r.ServiceActions.Select(a => Model.NameOf(a)),
                elements = ModuleReferences.Elements(r).Select(e => new { name = Model.NameOf(e), kind = e.GetInterface().Name }),
            }),
        };
    }

    internal static bool IsBlock(object o) => o.GetType().GetInterfaces().Any(i => i.Name is "IBlockSignature" or "IMobileBlockSignature" or "IWebBlockSignature");

    static IEnumerable<(string flow, IObjectSignature screen)> Screens(IESpace m) =>
        m.MobileFlows.SelectMany(f => f.Nodes.Where(s => s is IScreenSignature || IsBlock(s)).Select(s => (f.Name, (IObjectSignature)s)))
        .Concat(m.WebFlows.SelectMany(f => f.Nodes.Where(s => s is IScreenSignature || IsBlock(s)).Select(s => (f.Name, (IObjectSignature)s))));

    static object ScreenRow(string flow, IObjectSignature s) => new
    {
        flow, name = Model.NameOf(s), @public = Reflect.Get(s, "Public") as bool?,
        inputs = Describe.List(s, "InputParameters").Select(Model.NameOf), roles = Roles(s), anonymousAccess = Reflect.Get(s, "AnonymousAccess") as bool?,
    };

    public static object Filter(IESpace m, bool own, string? only)
    {
        var result = JsonSerializer.SerializeToNode(Module(m))!.AsObject();
        if (only is not null)
        {
            var groups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["actions"] = ["serverActions", "serviceActions", "clientActions"],
                ["rest"] = ["restClients", "restServices"], ["static"] = ["staticEntities"],
            };
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in only.Split(',', StringSplitOptions.TrimEntries))
            {
                if (groups.TryGetValue(key, out var sections)) keep.UnionWith(sections);
                else if (new[] { "entities", "structures", "screens", "blocks", "references", "timers", "roles", "processes", "siteProperties" }.Contains(key, StringComparer.OrdinalIgnoreCase)) keep.Add(key);
                else throw new CliError($"Unknown inspect section '{key}'.", 2);
            }
            foreach (var key in result.Select(p => p.Key).ToArray())
                if (!keep.Contains(key)) result.Remove(key);
        }
        if (own) result.Remove("references");
        return result;
    }

    // The object tree under the shallowest element with this name: concrete type, name, and expression text.
    public static object Tree(IESpace m, string name)
    {
        try
        {
            var root = ElementPaths.Resolve(m, name);
            return Node(root, 0, name);
        }
        catch (ModelError e) { throw new CliError(e.Message, 2); }
    }

    static string? TreeName(object o) => Model.NameOf(o) ?? Reflect.Get(o, "DisplayName") as string;

    // A switch condition's connector is its Link child.
    static string? ConnectorName(object o, string property)
    {
        if (o is OutSystems.Model.Processes.IProcessConnector)
            property = property == "SourceNode" ? "Source" : "Target";
        var connector = o is OutSystems.Model.Logic.Nodes.ISwitchCondition && Reflect.Get(o, "Link") is { } link ? link : o;
        return Reflect.Get(connector, property) is IObjectSignature node
            ? TreeName(node) ?? (node.Parent is { } parent ? ElementPaths.Segment(node, parent) : null) : null;
    }

    static object Node(IObjectSignature o, int depth, string path) => new
    {
        type = o.GetType().Name,
        kind = ElementPaths.Kind(o),
        name = Model.NameOf(o),
        folder = Reflect.Get(o, "Folder") is { } folder ? Model.NameOf(folder) : null,
        requiredScripts = Content.Items(o, "RequiredScripts").Select(Model.NameOf).ToArray(),
        path,
        horizontalPosition = o is IFlowNode positioned ? (int?)positioned.HorizontalPosition : null,
        verticalPosition = o is IFlowNode positionedY ? (int?)positionedY.VerticalPosition : null,
        widgetOrder = Reflect.Get(o, "Widgets") is System.Collections.IEnumerable widgets
            ? widgets.Cast<object>().Select(w => Model.NameOf(w) ?? ElementPaths.Kind(w)).ToArray() : null,
        roles = Reflect.Get(o, "Roles") is System.Collections.IEnumerable roles
            ? roles.Cast<object>().Select(Model.NameOf).ToArray() : null,
        text = o is IExpression e ? e.Text : Reflect.Get(o, "Text") as string,
        // What an execute node, event handler, lifecycle event, refresh or argument points at.
        @ref = new[] { "Action", "Handler", "Destination", "Exception", "DataSource", "Source", "Parameter", "SourceBlock", "Email", "OnExceptionHandler", "GlobalExceptionHandler", "Image" }
            .Select(p => Reflect.Get(o, p))
            .Where(v => v is not null).Select(v => Model.NameOf(v!)).FirstOrDefault(),
        source = ConnectorName(o, "SourceNode"),
        target = ConnectorName(o, "TargetNode"),
        left = Reflect.Get(o, "LeftSource") is { } left ? Model.NameOf(left) : null,
        right = Reflect.Get(o, "RightSource") is { } right ? Model.NameOf(right) : null,
        joinType = Reflect.Get(o, "JoinType")?.ToString(),
        function = Reflect.Get(o, "AggregationType")?.ToString(),
        children = depth > 12 ? null : ElementPaths.Children(o).Where(c => c is not IExpression || ((IExpression)c).Text.Length > 0)
            .Select(c => Node(c, depth + 1, path + "/" + ElementPaths.Segment(c, o))),
    };

    // Who may open a screen: Anonymous, Registered and/or custom roles.
    static string?[] Roles(object screen) => Reflect.Get(screen, "Roles") is System.Collections.IEnumerable roles
        ? roles.Cast<object>().Select(Model.NameOf).ToArray() : Array.Empty<string?>();

    // Object counts by concrete type: tells you whether a job is 20 objects or 1,000 before you start it.
    public static object Census(IESpace m)
    {
        var counts = new Dictionary<string, int>();
        var stack = new Stack<IObjectSignature>(((IObjectSignature)m).Children);
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            var t = o.GetType().Name;
            counts[t] = counts.GetValueOrDefault(t) + 1;
            foreach (var c in o.Children) stack.Push(c);
        }
        return new { total = counts.Values.Sum(), byType = counts.OrderByDescending(kv => kv.Value).ToDictionary() };
    }
}
