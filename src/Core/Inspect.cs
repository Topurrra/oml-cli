using OutSystems.Model;
using System.Text.Json;
using System.Text.Json.Nodes;
using OutSystems.Model.Applications;
using OutSystems.Model.Logic;
using OutSystems.Model.Types;
using OutSystems.Model.UI;

namespace OmlCli;

static class Inspect
{
    public static object Module(IESpace m, History.View h)
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
            lastModifiedBy = h.By(a), lastModifiedDate = h.On(a),
        };

        return new
        {
            name = ((IModule)m).Name,
            module = One("module", () => ModuleSettings.Inspect(m)),
            kind = m.Kind.ToString(),
            content = One("content", () => Content.Inspect(m)),
            defaultScreen = One("defaultScreen", () => m.DefaultScreen is { } landing ? Model.NameOf(landing) : null),
            entities = Rows("entities", h.Keep(m.Entities), e => new { name = e.Name, @public = e.Public, attributes = e.Attributes.Select(Attr), lastModifiedBy = h.By(e), lastModifiedDate = h.On(e) }),
            staticEntities = Rows("staticEntities", h.Keep(m.Entities).Where(e => Reflect.Get(e, "EntityKind")?.ToString() == "Static")
                .Cast<OutSystems.Model.Data.IStaticEntity>(), e => new { name = e.Name, @public = e.Public,
                    records = e.Records.Select(r => new { identifier = r.Identifier,
                        values = r.AttributeValues.ToDictionary(v => v.Attribute.Name, v => v.Value?.Text) }), lastModifiedBy = h.By(e), lastModifiedDate = h.On(e) }),
            structures = Rows("structures", h.Keep(m.Structures), s => new { name = s.Name, @public = s.Public, attributes = s.Attributes.Select(Attr), lastModifiedBy = h.By(s), lastModifiedDate = h.On(s) }),
            serverActions = Rows("serverActions", h.Keep(m.ServerActions), Params),
            serviceActions = Rows("serviceActions", h.Keep(m.ServiceActions), Params),
            clientActions = Rows("clientActions", h.Keep(m.ClientActions), Params),
            restClients = One("restClients", () => RestModel.Inspect(m, service: false, h)),
            restServices = One("restServices", () => RestModel.Inspect(m, service: true, h)),
            screens = Rows("screens", Screens(m).Where(s => !IsBlock(s.screen) && h.Pass(s.screen)), s => ScreenRow(s.flow, s.screen, h), s => s.screen),
            blocks = Rows("blocks", Screens(m).Where(s => IsBlock(s.screen) && h.Pass(s.screen)), s => ScreenRow(s.flow, s.screen, h), s => s.screen),
            timers = Rows("timers", h.Keep(m.Timers), t => new { name = Model.NameOf(t), action = t.Action is { } a ? Model.NameOf(a) : null, schedule = t.Schedule,
                timeout = t.TimeoutInMinutes, priority = t.Priority.ToString(), description = t.Description, lastModifiedBy = h.By(t), lastModifiedDate = h.On(t) }),
            processes = Rows("processes", h.Keep(m.Processes), p => new { name = Model.NameOf(p), launchOn = Reflect.Get(p.LaunchOn, "Entity") is { } e ? Model.NameOf(e) : null,
                inputs = p.InputParameters.Select(i => new { name = i.Name, type = Model.Describe(m, i.DataType) }),
                nodes = ElementPaths.Ordered(p.Nodes.OfType<IObjectSignature>()).Select(n => new { name = Model.NameOf(n), kind = ElementPaths.Kind(n) }), lastModifiedBy = h.By(p), lastModifiedDate = h.On(p) }),
            roles = Rows("roles", h.Keep(m.Roles), r => new { name = Model.NameOf(r), @public = Reflect.Get(r, "Public") as bool?, description = r.Description, persistent = r.IsPersistent,
                actions = new[] { r.CheckRoleAction, r.GrantRoleAction, r.RevokeRoleAction }.Where(a => a is not null).Select(a => Model.NameOf(a!)), lastModifiedBy = h.By(r), lastModifiedDate = h.On(r) }),
            siteProperties = Rows("siteProperties", h.Keep(m.SiteProperties), p => new { name = p.Name, type = Model.Describe(m, p.DataType),
                @default = p.DefaultValue?.Text, multiTenant = p.IsMultiTenant.ToString(), description = p.Description, lastModifiedBy = h.By(p), lastModifiedDate = h.On(p) }),
            exceptions = Rows("exceptions", h.Keep(m.UserExceptions), e => new { name = e.Name, @public = Reflect.Get(e, "Public") as bool?, lastModifiedBy = h.By(e), lastModifiedDate = h.On(e) }),
            references = Rows("references", m.References, r => new
            {
                module = r.Name,
                entities = ByName(r.Entities).Select(e => e.Name),
                structures = ByName(r.Structures).Select(s => s.Name),
                serverActions = ByName(r.ServerActions).Select(a => Model.NameOf(a)),
                clientActions = ByName(r.ClientActions).Select(a => Model.NameOf(a)),
                serviceActions = ByName(r.ServiceActions).Select(a => Model.NameOf(a)),
                elements = ByName(ModuleReferences.Elements(r)).Select(e => new { name = Model.NameOf(e), kind = e.GetInterface().Name }),
            }),
        };
    }

    // Each row is read on its own (serialized here, not later): a row that cannot be read is left out and listed in unreadable.
    // Rows are sorted by name: the model's lists come in another order on each load of the same module.
    static JsonNode?[] Rows<T>(string section, IEnumerable<T> items, Func<T, object> row, Func<T, object>? element = null)
    {
        using var _ = Unreadable.In(section);
        return Unreadable.Try(section, () => ByName(items, element).ToArray(), [])
            .Select((x, i) => Unreadable.Try(() => $"{section}[{i}] {Model.NameOf((element ?? (y => y!))(x)) ?? "?"}", () => Labelled(section, JsonSerializer.SerializeToNode(row(x)), (element ?? (y => y!))(x)), null))
            .Where(n => n is not null).ToArray();
    }

    // A kind is printed only where the section name does not already say it (a static entity listed under entities).
    static bool Implied(string section, string kind)
    {
        var one = section.EndsWith("ies") ? section[..^3] + "y" : section.TrimEnd('s');
        return section == "entities" ? kind == "ServerEntity" : kind.EndsWith(one, StringComparison.OrdinalIgnoreCase);
    }

    // The kind and the description (when there is one) of each row, so two elements that share a name can be told
    // apart before a spec names one. Long descriptions are cut: inspect stays an overview.
    const int DescriptionChars = 120;
    static JsonNode? Labelled(string section, JsonNode? row, object element)
    {
        if (row is not JsonObject o || element is not IObjectSignature) return row;
        var result = new JsonObject();
        foreach (var (key, value) in o)
        {
            if (key == "name") { result["name"] = value?.DeepClone(); if (Describe.Kind(element) is var kind && !Implied(section, kind)) result["kind"] = kind; }
            else if (key != "description" && key != "kind") result[key] = value?.DeepClone();
        }
        var description = System.Text.RegularExpressions.Regex.Replace((o["description"]?.GetValue<string>() ?? Reflect.Get(element, "Description") as string) ?? "", @"\s*[\r\n]+\s*", " ").Trim();
        if (description.Length > 0)
        {
            var cut = DescriptionChars - 3;
            if (description.Length > DescriptionChars && char.IsHighSurrogate(description[cut - 1])) cut--; // keep a surrogate pair whole
            result["description"] = description.Length > DescriptionChars ? description[..cut] + "..." : description;
        }
        return result;
    }

    static JsonNode? One(string section, Func<object?> read)
    {
        using var _ = Unreadable.In(section);
        return Unreadable.Try(section, () => JsonSerializer.SerializeToNode(read()), null);
    }

    // By name (ignoring case, then exact), then by GlobalKey, so a list is the same on every load of the module.
    internal static IEnumerable<T> ByName<T>(IEnumerable<T> items, Func<T, object>? element = null)
    {
        element ??= x => x!;
        return items.Select(x => (x, e: element(x))).Select(p => (p.x, name: Model.NameOf(p.e) ?? "", key: (p.e as IObjectSignature)?.GlobalKey.ToString() ?? ""))
            .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.name, StringComparer.Ordinal).ThenBy(p => p.key, StringComparer.Ordinal)
            .Select(p => p.x);
    }

    internal static bool IsBlock(object o) => o.GetType().GetInterfaces().Any(i => i.Name is "IBlockSignature" or "IMobileBlockSignature" or "IWebBlockSignature");

    internal static IEnumerable<(string flow, IObjectSignature screen)> Screens(IESpace m) =>
        m.MobileFlows.SelectMany(f => f.Nodes.Where(s => s is IScreenSignature || IsBlock(s)).Select(s => (f.Name, (IObjectSignature)s)))
        .Concat(m.WebFlows.SelectMany(f => f.Nodes.Where(s => s is IScreenSignature || IsBlock(s)).Select(s => (f.Name, (IObjectSignature)s))));

    static object ScreenRow(string flow, IObjectSignature s, History.View h) => new
    {
        flow, name = Model.NameOf(s), @public = Reflect.Get(s, "Public") as bool?,
        inputs = Describe.List(s, "InputParameters").Select(Model.NameOf), roles = Roles(s), anonymousAccess = Reflect.Get(s, "AnonymousAccess") as bool?,
        lastModifiedBy = h.By(s), lastModifiedDate = h.On(s),
    };

    public static object Filter(IESpace m, bool own, string? only, bool history = false, string? changedSince = null)
    {
        var since = changedSince is null ? null : History.ParseSince(changedSince);
        using var unreadable = new Unreadable();
        var result = JsonSerializer.SerializeToNode(Module(m, new History.View(history, since)))!.AsObject();
        // A date filter lists changed elements only: module settings, content and references have no change date.
        if (since is not null) foreach (var key in new[] { "module", "content", "defaultScreen", "references" }) result.Remove(key);
        // The change fields are null when not asked for, and --pretty would print them.
        if (!history)
            foreach (var row in result.Select(p => p.Value).OfType<JsonArray>().SelectMany(a => a.OfType<JsonObject>()))
            { row.Remove("lastModifiedBy"); row.Remove("lastModifiedDate"); }
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
            if (since is not null && keep.All(k => k.Equals("references", StringComparison.OrdinalIgnoreCase)))
                throw new CliError("--changed-since cannot list references: they have no change date. Name other sections with --only.", 2);
            foreach (var key in result.Select(p => p.Key).ToArray())
                if (!keep.Contains(key)) result.Remove(key);
        }
        if (since is not null) result["changedSince"] = since;
        if (own) result.Remove("references");
        // Only the entries of the sections still in the output.
        if (unreadable.Items(result.ContainsKey) is { } items) result["unreadable"] = JsonSerializer.SerializeToNode(items);
        return result;
    }

    // How much of a tree is printed: levels below the element (--depth), and at most TreeNodes parts, so a big screen
    // does not print hundreds of kilobytes. A part whose children are not printed says how many it has (`more`).
    internal const int TreeNodes = 250;
    sealed class TreeLimit(int depth) { public readonly int Depth = depth; public int Left = TreeNodes; public bool Cut; }

    // The object tree under the shallowest element with this name: concrete type, name, and expression text.
    public static object Tree(IESpace m, string name, int depth = 13)
    {
        try
        {
            var root = ElementPaths.Resolve(m, name);
            using var unreadable = new Unreadable();
            var limit = new TreeLimit(depth);
            var tree = JsonSerializer.SerializeToNode(Unreadable.Try(name, () => Node(root, 0, name, limit), new { kind = ElementPaths.Kind(root), name = Model.NameOf(root), path = name }))!.AsObject();
            if (limit.Cut)
                tree["truncated"] = $"Cut at {TreeNodes} parts; parts with `more` have children not shown. Narrow it: oml tree <module.oml> {name}/<path> --depth 2.";
            if (unreadable.Items() is { } items) tree["unreadable"] = JsonSerializer.SerializeToNode(items);
            return tree;
        }
        catch (ModelError e) { throw new CliError(e.Message, 2); }
    }

    // The parts to print under o, or null (and a count) when the depth or the part budget stops here.
    static (object[]? children, int? more) TreeChildren(IObjectSignature o, int depth, string path, TreeLimit limit)
    {
        var all = Unreadable.Try("children of " + path, () => ElementPaths.Children(o).Where(c => c is not IExpression { Text.Length: 0 }).ToArray(), []);
        if (all.Length == 0) return ([], null);
        if (depth >= limit.Depth) return (null, all.Length);
        if (limit.Left < all.Length) { limit.Cut = true; return (null, all.Length); }
        limit.Left -= all.Length;
        return (all.Select(c => Unreadable.Try(path + "/" + (Model.NameOf(c) ?? ElementPaths.Kind(c)),
                () => Node(c, depth + 1, path + "/" + ElementPaths.Segment(c, o), limit), null)).OfType<object>().ToArray(), null);
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

    // A grouped aggregate with a group filter holds a source whose Source getter throws (it casts its GroupBy parent
    // to a source). Tree only reads references for display, so such a getter shows no ref instead of ending the command.
    static object? Ref(object o, string property)
    {
        try { return Reflect.Property(o, property)?.GetValue(o); }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is InvalidCastException || Reflect.ProxyLacks(o, e)) { return null; }
        catch (System.Reflection.TargetInvocationException e) { Unreadable.Add(() => Unreadable.Where(o) + " property " + property, e); return null; }
    }

    static object Node(IObjectSignature o, int depth, string path, TreeLimit limit)
    {
        var (children, more) = TreeChildren(o, depth, path, limit);
        return new
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
                ? roles.Cast<object>().Select(Model.NameOf).Order(StringComparer.Ordinal).ToArray() : null,
            text = o is IExpression e ? e.Text : Reflect.Get(o, "Text") as string,
            targetWidget = Ref(o, "TargetWidget") is { } labelled ? Model.NameOf(labelled) : null, // a Label's input
            // What an execute node, event handler, lifecycle event, refresh or argument points at.
            @ref = new[] { "Action", "Handler", "Destination", "Exception", "DataSource", "Source", "Parameter", "SourceBlock", "Email", "OnExceptionHandler", "GlobalExceptionHandler", "Image" }
                .Select(p => Ref(o, p))
                .Where(v => v is not null).Select(v => Model.NameOf(v!)).FirstOrDefault(),
            source = ConnectorName(o, "SourceNode"),
            target = ConnectorName(o, "TargetNode"),
            left = Reflect.Get(o, "LeftSource") is { } left ? Model.NameOf(left) : null,
            right = Reflect.Get(o, "RightSource") is { } right ? Model.NameOf(right) : null,
            joinType = Reflect.Get(o, "JoinType")?.ToString(),
            function = Reflect.Get(o, "AggregationType")?.ToString(),
            // Built child by child (TreeChildren), so that a child that cannot be read is left out (and listed) instead of ending the command.
            children,
            more,
        };
    }

    // Who may open a screen: Anonymous, Registered and/or custom roles.
    static string?[] Roles(object screen) => Reflect.Get(screen, "Roles") is System.Collections.IEnumerable roles
        ? roles.Cast<object>().Select(Model.NameOf).Order(StringComparer.Ordinal).ToArray() : Array.Empty<string?>(); // the model gives them in no fixed order

    // Object counts by concrete type: tells you whether a job is 20 objects or 1,000 before you start it.
    public static object Census(IESpace m)
    {
        var counts = new Dictionary<string, int>();
        using var unreadable = new Unreadable();
        var stack = new Stack<IObjectSignature>(((IObjectSignature)m).Children);
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            var t = o.GetType().Name;
            counts[t] = counts.GetValueOrDefault(t) + 1;
            foreach (var c in Unreadable.Try(() => "children of " + Unreadable.Where(o), () => o.Children.ToArray(), [])) stack.Push(c);
        }
        var census = JsonSerializer.SerializeToNode(new { total = counts.Values.Sum(), byType = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary() })!.AsObject();
        if (unreadable.Items() is { } items) census["unreadable"] = JsonSerializer.SerializeToNode(items); // absent, not null, also with --pretty
        return census;
    }
}
