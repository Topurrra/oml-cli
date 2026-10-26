using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Logic.Aggregates;
using OutSystems.Model.Logic.Nodes;
using OutSystems.Model.Types;
using OutSystems.Model.UI;
using ServiceStudio.Plugin.RESTService;
using ServiceStudio.Plugin.RESTService.Enumerations;

namespace OmlCli;

// The walk over one module that finds the `oml check` patterns (the catalog is in CheckCatalog.cs).
// Where Service Studio has its own warning for a pattern, the rule reads that warning, so it matches what developers see.
static class CheckRules
{
    // A finding before the rule settings are applied: the rule, where, a sentence with the facts, and an AI Mentor
    // name when it depends on the module (null keeps the catalog's name).
    internal sealed record Hit(string Rule, string Module, string Element, string? Note = null, string? AiMentorName = null);

    // A flow fingerprint for duplicate detection across all input modules.
    internal sealed record Fingerprint(string Hash, string Module, string Element, int Nodes);

    // Failures: rules that threw on this module, with the reason. Their findings for the module are missing.
    internal sealed record ModuleResult(List<Hit> Hits, List<Fingerprint> Flows, List<(string Rule, string Reason)> Failures);

    static readonly Regex UserIdOnly = new(@"^\s*GetUserId\s*\(\s*\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex RowLimit = new(@"\b(TOP|LIMIT|ROWNUM|FETCH\s+(FIRST|NEXT))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // A SELECT of aggregate functions only, without GROUP BY, returns one row.
    const string AggregateColumn = @"(?:COUNT|COUNT_BIG|SUM|MIN|MAX|AVG)\s*\([^()]*\)(?:\s+AS)?(?:\s+\[?\w+\]?)?";
    static readonly Regex SingleRow = new(@"^\s*SELECT\s+" + AggregateColumn + @"(?:\s*,\s*" + AggregateColumn + @")*\s+FROM\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex GroupBy = new(@"\bGROUP\s+BY\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex SecretName = new(@"password|passwd|secret|token|api_?key|private_?key|access_?key|client_?key|connection_?string|credential",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex SignInScreen = new(@"log_?in|log_?on|log_?out|sign_?in|sign_?up|sign_?out|register|password", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex StringLiteral = new("\"(?:[^\"]|\"\")*\"", RegexOptions.CultureInvariant);
    static readonly Regex Sanitizer = new(@"\b(?:EncodeSql|BuildSafe_\w+)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Identifier = new(@"(?<![\w.""])[A-Za-z_]\w*", RegexOptions.CultureInvariant);
    // Names that need no description (AI Mentor exceptions for common names).
    static readonly HashSet<string> CommonNames = new(StringComparer.OrdinalIgnoreCase) { "Id", "Name", "Label", "Description", "CreatedBy", "UpdatedBy", "CreatedOn", "UpdatedOn" };
    static readonly string[] SqlInjectionWarnings = ["SQLInjection", "SQLInjectionInvalidEncodeSQL", "SQLInjectionInvalidReplace"];
    // Elements a node points at, for duplicate detection: two flows that read different entities are not copies.
    static readonly string[] ReferenceKeys = ["Action", "Source", "DataSource", "Destination", "Entity", "Exception", "SourceBlock", "RightSource"];

    static string Name(object o) => Model.NameOf(o) ?? "";
    // A Refresh Data node has no name: label it by its source, as describe does.
    static string NodeLabel(object n) => n is IRefreshDataNode && Reflect.Get(n, "DataSource") is { } source ? "Refresh " + Name(source) : Name(n);
    static string Text(object? expression) => expression is IExpression e ? e.Text ?? "" : "";
    static bool Implements(object o, string token) => o.GetType().GetInterfaces().Any(i => i.Name.Contains(token, StringComparison.Ordinal));
    static IEnumerable<object> List(object o, string key) => Reflect.Get(o, key) is IEnumerable xs ? xs.Cast<object>() : [];
    static bool IsScreenOrBlock(object o) => o is IScreenSignature || Inspect.IsBlock(o);
    static bool IsComment(object o) => o.GetType().GetInterfaces().Any(i => i.Name == "IComment");

    // Names from the module down, Kind[index] only for unnamed parts (the form `oml tree` prints). Names keep the
    // path stable when other elements are added, which baselines and ignore entries need; Kind[index] can shift.
    internal static string PathOf(IObjectSignature o)
    {
        var parts = new List<string>();
        for (; o.Parent is { } parent && o is not IESpace; o = parent)
            parts.Insert(0, Model.NameOf(o) is { Length: > 0 } name ? name : ElementPaths.Segment(o, parent));
        return string.Join("/", parts);
    }

    // A flow is any object with a Start node among its children: actions, screen actions, data actions, preparations.
    sealed class Flow
    {
        public required IObjectSignature Owner;
        public required List<IObjectSignature> Nodes;
        public required Dictionary<IObjectSignature, List<(IObjectSignature target, bool cycle)>> Next;
        public IObjectSignature? Screen;            // the screen or block that owns this flow
        public bool Client;                          // runs in the browser or device
        public bool Comment;
        public IEnumerable<IObjectSignature> Successors(IObjectSignature n) => Next.TryGetValue(n, out var t) ? t.Select(x => x.target) : [];
    }

    static Flow? ReadFlow(IObjectSignature owner, bool reactive)
    {
        var children = ElementPaths.Children(owner).ToList();
        if (!children.Any(c => c is IStartNode)) return null;
        var nodes = children.Where(c => c is IFlowNodeSignature).ToList();
        var set = nodes.ToHashSet();
        var next = new Dictionary<IObjectSignature, List<(IObjectSignature, bool)>>();
        foreach (var node in nodes)
        {
            var targets = new List<(IObjectSignature, bool)>();
            foreach (var c in ElementPaths.Children(node))
            {
                // Sequence, True, False, Cycle and Otherwise connectors point at TargetNode; switch conditions through Link.
                var connector = Reflect.Get(c, "Link") as IObjectSignature ?? c;
                if (Reflect.Get(connector, "TargetNode") is IObjectSignature target && set.Contains(target))
                    targets.Add((target, ElementPaths.Kind(c) == "Cycle"));
            }
            next[node] = targets;
        }
        IObjectSignature? screen = null;
        for (var p = owner.Parent; p is not null && p is not IESpace; p = p.Parent)
            if (IsScreenOrBlock(p)) { screen = p; break; }
        var client = owner is OutSystems.Model.Logic.IClientAction
            || reactive && owner is OutSystems.Model.UI.Mobile.IScreenAction;
        return new Flow { Owner = owner, Nodes = nodes, Next = next, Screen = screen, Client = client, Comment = children.Any(IsComment) };
    }

    // Every object of the module itself; referenced modules' signatures are not walked.
    static List<IObjectSignature> Own(IESpace m)
    {
        var result = new List<IObjectSignature>();
        var seen = new HashSet<IObjectSignature>();
        var pending = new Stack<IObjectSignature>(((IObjectSignature)m).Children);
        while (pending.TryPop(out var o))
            if (o is not IReference && seen.Add(o)) { result.Add(o); foreach (var c in o.Children) pending.Push(c); }
        return result;
    }

    // The loop body: nodes after the Cycle connector that only the loop leads to and that lead back to the loop.
    // A path that leaves the loop (an exit to an End or to other logic) is not part of it.
    static HashSet<IObjectSignature> LoopBody(Flow f, IObjectSignature loop)
    {
        HashSet<IObjectSignature> Reach(IEnumerable<IObjectSignature> from, Func<IObjectSignature, IEnumerable<IObjectSignature>> step)
        {
            var seen = new HashSet<IObjectSignature>();
            var pending = new Stack<IObjectSignature>(from);
            while (pending.TryPop(out var n))
                if (!ReferenceEquals(n, loop) && seen.Add(n)) foreach (var s in step(n)) pending.Push(s);
            return seen;
        }
        var predecessors = f.Nodes.ToDictionary(n => n, _ => new List<IObjectSignature>());
        foreach (var (node, targets) in f.Next) foreach (var t in targets) predecessors[t.target].Add(node);
        var entries = f.Nodes.Where(n => n is IStartNode || n.GetType().GetInterfaces().Any(i => i.Name.Contains("ExceptionHandler")));
        var outside = Reach(entries, f.Successors);                        // reachable without passing the loop
        var after = Reach(f.Next[loop].Where(t => t.cycle).Select(t => t.target), f.Successors);
        var back = Reach(predecessors[loop], n => predecessors[n]);        // can reach the loop again
        return after.Where(n => !outside.Contains(n) && back.Contains(n)).ToHashSet();
    }

    // Each node in a loop body once, with the innermost loop that contains it.
    static IEnumerable<(IObjectSignature node, IObjectSignature loop)> InLoops(Flow f)
    {
        var bodies = f.Nodes.OfType<IForEachNode>().Cast<IObjectSignature>().Select(l => (loop: l, body: LoopBody(f, l))).ToList();
        foreach (var node in bodies.SelectMany(b => b.body).Distinct())
            yield return (node, bodies.Where(b => b.body.Contains(node)).MinBy(b => b.body.Count).loop);
    }

    static bool ServerCall(IObjectSignature node)
    {
        // Refreshing a local storage aggregate stays on the device.
        if (node is IRefreshDataNode) return Reflect.Get(node, "DataSource") is not IAggregate { IsClientSide: true };
        if (Reflect.Get(node, "Action") is not { } action) return false;
        if (Implements(action, "ServerAction") || Implements(action, "ServiceAction")) return true;
        // Entity actions run on the server, except those of local (client) entities, which run on the device.
        // The concrete entity class implements both entity interfaces, so EntityKind (Server, Client, Static) decides.
        return (Implements(action, "EntityAction") || action.GetType().Name.Contains("Entity", StringComparison.Ordinal))
            && action is IObjectSignature { Parent: { } entity } && Reflect.Get(entity, "EntityKind")?.ToString() != "Client";
    }

    // Argument values only: the Parameter of an argument to a broken reference throws on some builds, and only SQL needs it.
    static IEnumerable<string> Values(object node) => List(node, "Arguments").Select(a => Text(Reflect.Get(a, "Value")));

    public static ModuleResult Run(IESpace m, Func<string, bool> enabled, Func<string, string, int> option)
    {
        var module = Name(m);
        var hits = new List<Hit>();
        var prints = new List<Fingerprint>();
        var failures = new List<(string, string)>();
        void Add(string rule, IObjectSignature where, string? note = null, string? aiMentor = null) => hits.Add(new(rule, module, PathOf(where), note, aiMentor));
        // A rule that throws is reported as a failure of that rule; the other rules still run.
        void Try(string rule, Action body)
        {
            if (!enabled(rule)) return;
            try { body(); }
            catch (Exception e) when (e is not OutOfMemoryException)
            { if (!failures.Any(f => f.Item1 == rule)) failures.Add((rule, (e.InnerException ?? e).Message)); }
        }
        var kind = m.Kind.ToString();
        var reactive = kind is "CrossDevice" or "Mobile" || kind.Contains("Library", StringComparison.OrdinalIgnoreCase);
        var traditional = kind is "WebResponsive" or "Web";
        var all = Own(m);
        var flows = all.Select(o => ReadFlow(o, reactive)).OfType<Flow>().ToList();
        var anonymous = Reflect.Get(m, "AnonymousRole");
        var registered = Reflect.Get(m, "RegisteredRole");
        var studio = new[] { "unused-action", "unused-query", "server-call-in-client-loop", "multiple-server-calls", "sql-expand-inline", "sql-expand-inline-input", "render-assign", "self-refreshing-aggregate", "escape-content-off" }.Any(enabled)
            ? Model.Warnings(m) : [];
        IEnumerable<OutSystems.Model.IValidationMessage> Warned(params string[] ids) => studio.Where(w => ids.Contains(w.Id) && w.Owner is not null);

        // Performance: queries and server calls.
        Try("query-in-loop", () =>
        {
            foreach (var f in flows)
                foreach (var (n, loop) in InLoops(f))
                    if (n is IAggregateNode || n is ISQLNode) Add("query-in-loop", n, $"{Name(n)} runs inside the loop {Name(loop)}.");
        });
        Try("server-call-in-client-loop", () =>
        {
            foreach (var w in Warned("IteratedServerInteraction").DistinctBy(w => w.Owner))
                Add("server-call-in-client-loop", w.Owner!, w.Detail);
        });
        Try("multiple-server-calls", () =>
        {
            foreach (var (flow, w) in Warned("ConsecutiveServerInteractions").Select(w => (flow: w.Owner is IFlowNodeSignature && w.Owner.Parent is { } p ? p : w.Owner!, w)).DistinctBy(x => x.flow))
                Add("multiple-server-calls", flow, w.Detail);
        });
        Try("server-call-in-client-event", () =>
        {
            var events = new HashSet<object>();
            foreach (var o in all.Where(o => IsScreenOrBlock(o) || o is IAggregate || o is OutSystems.Model.UI.Mobile.IDataAction))
                foreach (var key in new[] { "OnInitialize", "OnReady", "OnRender", "OnAfterFetch" })
                    if (Reflect.Property(o, key) is not null && Reflect.Get(o, key) is { } e && Reflect.Get(e, "Destination") is { } handler) events.Add(handler);
            foreach (var f in flows.Where(f => f.Client && events.Contains(f.Owner)))
                if (f.Nodes.Where(ServerCall).ToList() is { Count: > 0 } calls)
                    Add("server-call-in-client-event", f.Owner, $"{Name(f.Owner)} handles a lifecycle event and calls {string.Join(", ", calls.Select(NodeLabel).Order(StringComparer.Ordinal))}.");
        });
        Try("render-assign", () =>
        {
            // The warning's owner is the OnRender event; report the handler it runs.
            foreach (var w in Warned("ClientActionWithModelAssignmentOnLifecycleEvent"))
                Add("render-assign", Reflect.Get(w.Owner!, "Destination") as IObjectSignature ?? w.Owner!, w.Detail);
        });
        Try("self-refreshing-aggregate", () =>
        {
            // Report the aggregate that owns the On After Fetch event.
            foreach (var w in Warned("SelfRefreshingAggregate"))
                Add("self-refreshing-aggregate", Ancestor<IAggregate>(w.Owner!) as IObjectSignature ?? w.Owner!, w.Detail);
        });
        Try("site-property-update", () =>
        {
            foreach (var assign in flows.SelectMany(f => f.Nodes.OfType<IAssignNode>()))
                if (List(assign, "Assignments").Any(a => Text(Reflect.Get(a, "Variable")).TrimStart().StartsWith("Site.", StringComparison.OrdinalIgnoreCase)))
                    Add("site-property-update", assign);
        });

        // Security: GetUserId() itself sent from the client. Comparisons such as GetUserId() <> NullIdentifier() are Booleans, not identity.
        Try("getuserid-client", () =>
        {
            foreach (var f in flows.Where(f => f.Client))
            {
                var holders = f.Nodes.OfType<IAssignNode>().SelectMany(a => List(a, "Assignments"))
                    .Where(a => UserIdOnly.IsMatch(Text(Reflect.Get(a, "Value")))).Select(a => Text(Reflect.Get(a, "Variable")).Trim())
                    .Where(v => v.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var call in f.Nodes.Where(n => n is not IRefreshDataNode && ServerCall(n)))
                    if (Values(call).Any(v => UserIdOnly.IsMatch(v) || holders.Contains(v.Trim())))
                        Add("getuserid-client", call, $"{Name(call)} gets the user identifier from the client.");
            }
        });
        Try("getuserid-block-argument", () =>
        {
            if (!reactive) return;
            foreach (var instance in all.Where(o => ElementPaths.Kind(o).Contains("BlockInstance", StringComparison.Ordinal)))
                if (Values(instance).Any(v => UserIdOnly.IsMatch(v))) Add("getuserid-block-argument", instance, $"An input of {Name(Reflect.Get(instance, "SourceBlock") ?? instance)} gets GetUserId().");
        });

        // Security: Service Studio's HTML injection warning on an Expression with Escape Content = No (Traditional Web).
        // Service Studio does not warn for fixed text or a value inside an Encode function.
        Try("escape-content-off", () =>
        {
            foreach (var w in Warned("HTMLInjection").DistinctBy(w => w.Owner))
                if (Reflect.Get(w.Owner!, "EscapeContent") is false) Add("escape-content-off", w.Owner!, $"{Name(w.Owner!)} shows {Text(Reflect.Get(w.Owner!, "Value"))} with Escape Content = No.");
        });

        // Security: Service Studio's SQL injection warnings; the input check only decides which of the two rules.
        if (enabled("sql-expand-inline") || enabled("sql-expand-inline-input"))
            Try(enabled("sql-expand-inline") ? "sql-expand-inline" : "sql-expand-inline-input", () =>
            {
                foreach (var sql in Warned(SqlInjectionWarnings).Select(w => Ancestor<ISQLNode>(w.Owner!)).OfType<ISQLNode>().Distinct())
                {
                    var flow = flows.FirstOrDefault(f => f.Nodes.Contains((IObjectSignature)sql));
                    var inputs = flow is null ? new HashSet<string>() : ExternalInputs(flow);
                    var expanded = sql.Arguments.Where(a => a.Parameter is OutSystems.Model.Logic.ISQLInputParameter { ExpandInline: true }).ToList();
                    var fed = expanded.Select(a => (parameter: Name(a.Parameter), input: Identifier.Matches(StringLiteral.Replace(Unsanitized(Text(a.Value)), ""))
                        .Select(x => x.Value).FirstOrDefault(inputs.Contains))).FirstOrDefault(x => x.input is not null);
                    var names = string.Join(", ", expanded.Select(a => Name(a.Parameter)));
                    // AI Mentor names the pattern "SQL injection" in Traditional Web and "Vulnerable SQL injection" (input-fed) in Reactive and Mobile.
                    var aiMentor = traditional ? "SQL injection" : null;
                    if (fed.input is not null) { if (enabled("sql-expand-inline-input")) Add("sql-expand-inline-input", (IObjectSignature)sql, $"Parameter {fed.parameter} of {Name(sql)} is expanded inline and gets its value from {fed.input}.", aiMentor); }
                    else if (enabled("sql-expand-inline")) Add("sql-expand-inline", (IObjectSignature)sql, $"{Name(sql)} expands {names} inline.", aiMentor);
                }
            });

        // Maintainability: size, nesting and duplicates.
        Try("long-flow", () =>
        {
            foreach (var f in flows.Where(f => !f.Comment))
            {
                var size = Size(f);
                var limit = f.Screen is null ? option("long-flow", "maxNodes") : option("long-flow", "maxScreenNodes");
                if (size > limit) Add("long-flow", f.Owner, $"{Name(f.Owner)} has {size} nodes (limit {limit}).");
            }
        });
        Try("deep-nesting", () =>
        {
            foreach (var f in flows)
                if (Nesting(f) is var depth && depth > option("deep-nesting", "maxDepth"))
                    Add("deep-nesting", f.Owner, $"{Name(f.Owner)} nests {depth} levels deep (limit {option("deep-nesting", "maxDepth")}).");
        });
        Try("duplicated-flow", () =>
        {
            foreach (var f in flows)
            {
                // A flow the model cannot read in full is left out of the comparison; it must not fail the rule for the module.
                try { if (Size(f) is var size && size >= option("duplicated-flow", "minNodes")) prints.Add(new(Hash(f), module, PathOf(f.Owner), size)); }
                catch (Exception e) when (e is System.Reflection.TargetInvocationException or NullReferenceException or InvalidCastException or NotSupportedException or InvalidOperationException or KeyNotFoundException) { }
            }
        });

        // Queries that run for a screen or block.
        Try("aggregate-unlimited", () =>
        {
            foreach (var screen in all.Where(IsScreenOrBlock))
                foreach (var o in ModuleReferences.Descendants(screen).OfType<IAggregate>())
                    if (Text(o.MaxRecords).Trim().Length == 0) Add("aggregate-unlimited", (IObjectSignature)o, $"{Name(o)} runs for {Name(screen)}.");
        });
        Try("sql-unlimited", () =>
        {
            foreach (var screen in all.Where(IsScreenOrBlock))
                foreach (var sql in ModuleReferences.Descendants(screen).OfType<ISQLNode>())
                {
                    var statement = sql.Statement ?? "";
                    if (Text(sql.MaxRecords).Trim().Length > 0 || RowLimit.IsMatch(statement) || !statement.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                        || SingleRow.IsMatch(statement) && !GroupBy.IsMatch(statement)) continue;
                    Add("sql-unlimited", (IObjectSignature)sql, $"{Name(sql)} runs for {Name(screen)}.");
                }
        });

        var screens = all.OfType<IScreenSignature>().Where(s => !Inspect.IsBlock(s)).ToList();
        bool HasRole(IScreenSignature s, object? role) => role is not null && List(s, "Roles").Any(r => ReferenceEquals(r, role));
        Try("registered-screen", () =>
        {
            foreach (var screen in screens.Where(s => !HasRole(s, anonymous) && HasRole(s, registered))) Add("registered-screen", screen);
        });
        Try("anonymous-screen-server", () =>
        {
            if (!reactive) return;
            foreach (var screen in screens.Where(s => HasRole(s, anonymous)))
            {
                var parts = ModuleReferences.Descendants(screen).ToList();
                var data = parts.OfType<IAggregate>().Where(a => !a.IsClientSide).Select(Name)
                    .Concat(parts.OfType<OutSystems.Model.UI.Mobile.IDataAction>().Select(Name)).ToList();
                var calls = parts.Where(p => p is IFlowNodeSignature && p is not IRefreshDataNode && ServerCall(p)).Select(p => Name(Reflect.Get(p, "Action")!)).ToList();
                // A sign-in screen must be public and must call the server to sign in: not a finding unless it also fetches data.
                if (data.Count == 0 && (calls.Count == 0 || SignInScreen.IsMatch(Name(screen)))) continue;
                Add("anonymous-screen-server", screen, $"{Name(screen)} allows Anonymous and calls the server: {string.Join(", ", data.Concat(calls).Distinct())}.");
            }
        });

        Try("large-session-variable", () =>
        {
            foreach (var variable in List(m, "SessionVariables").OfType<ISessionVariable>())
                if (variable.DataType is IListType or IRecordTypeSignature) Add("large-session-variable", variable, $"{variable.Name} is {Model.Describe(m, variable.DataType)}.");
        });
        Try("site-property-secret", () =>
        {
            // Only Text can hold a secret: an Integer or Boolean named PasswordMinLength or UseApiKey is a setting.
            foreach (var property in m.SiteProperties.OfType<ISiteProperty>())
                if (!property.IsSecret && ReferenceEquals(property.DataType, m.TextType) && SecretName.IsMatch(property.Name)) Add("site-property-secret", property);
        });
        Try("client-variable-secret", () =>
        {
            // Same names and Text-only test as site-property-secret.
            foreach (var variable in all.OfType<IClientVariable>())
                if (ReferenceEquals(variable.DataType, m.TextType) && SecretName.IsMatch(variable.Name)) Add("client-variable-secret", variable);
        });
        Try("timer-long-timeout", () =>
        {
            foreach (var timer in m.Timers)
            {
                if (Reflect.Get(timer, "TimeoutInMinutes") is not int minutes || minutes <= option("timer-long-timeout", "maxMinutes")) continue;
                // The wake-timer pattern: the timer's action calls Wake<Timer> to continue in a new run.
                var action = flows.FirstOrDefault(f => ReferenceEquals(f.Owner, Reflect.Get(timer, "Action")));
                if (action is not null && action.Nodes.Any(n => Reflect.Get(n, "Action") is IObjectSignature called
                        && (ReferenceEquals(called.Parent, timer) || Name(called) == "Wake" + Name(timer)))) continue;
                Add("timer-long-timeout", (IObjectSignature)timer, $"{Name(timer)} has a timeout of {minutes} minutes (limit {option("timer-long-timeout", "maxMinutes")}).");
            }
        });
        Try("rest-no-auth", () =>
        {
            foreach (var api in m.Integrations.OfType<IRestService>().Where(a => ReferenceEquals(a.GetESpace(), m)))
                if (api.Authentication == Authentication.None) Add("rest-no-auth", (IObjectSignature)api);
        });
        Try("public-entity-writable", () =>
        {
            foreach (var entity in m.Entities.OfType<IEntity>())
                if (entity.Public && !entity.ExposeReadOnly && Reflect.Get(entity, "EntityKind")?.ToString() is not ("Static" or "Client")) Add("public-entity-writable", entity);
        });

        // Descriptions: the module, its public contracts and their parameters, and the elements that have no public flag.
        // Well-known names (CommonNames) are skipped unless allNames = 1; attributes are read only with attributes = 1.
        Try("missing-description", () =>
        {
            var allNames = option("missing-description", "allNames") > 0;
            var attributes = option("missing-description", "attributes") > 0;
            bool Empty(object o) => string.IsNullOrWhiteSpace(Reflect.Get(o, "Description") as string);
            bool Skipped(object p) => !allNames && CommonNames.Contains(Name(p));
            IEnumerable<IObjectSignature> Inputs(object o) => List(o, "InputParameters").OfType<IObjectSignature>();
            IEnumerable<IObjectSignature> Outputs(object o) => List(o, "OutputParameters").OfType<IObjectSignature>();
            void Parameters(IObjectSignature owner, IEnumerable<IObjectSignature> parameters)
            {
                foreach (var p in parameters)
                    if (!Skipped(p) && Empty(p)) Add("missing-description", p, $"Parameter {Name(p)} of {Name(owner)} has no description.");
            }
            if (string.IsNullOrWhiteSpace(Reflect.Get(m, "Description") as string)) hits.Add(new("missing-description", module, "", $"Module {module} has no description."));
            foreach (var e in ModuleReferences.Elements(m).Where(e => Reflect.Get(e, "Public") is true).Distinct())
            {
                var elementKind = Architecture.ElementKind(e);
                if (elementKind is not ("Entity" or "StaticEntity" or "Structure" or "ServerAction" or "ServiceAction" or "ClientAction" or "Block" or "Screen" or "Image" or "Role" or "Theme")) continue;
                if (Empty(e)) Add("missing-description", e, $"{elementKind} {Name(e)} has no description.");
                if (elementKind.EndsWith("Action", StringComparison.Ordinal)) Parameters(e, Inputs(e).Concat(Outputs(e)));
                else if (elementKind is "Block" or "Screen") Parameters(e, Inputs(e));
                else if (attributes && elementKind is "Entity" or "StaticEntity" or "Structure")
                    foreach (var a in List(e, "Attributes").OfType<IObjectSignature>())
                        if (!Skipped(a) && Empty(a)) Add("missing-description", a, $"Attribute {Name(a)} of {Name(e)} has no description.");
            }
            foreach (var process in m.Processes.OfType<IObjectSignature>().Where(x => Reflect.Get(x, "Public") is true))
            {
                if (Empty(process)) Add("missing-description", process, $"Process {Name(process)} has no description.");
                Parameters(process, Inputs(process).Concat(Outputs(process)));
            }
            // Timers, site properties and session variables have no Public flag in the model: all of them are checked.
            foreach (var timer in m.Timers.OfType<IObjectSignature>().Where(Empty))
                Add("missing-description", timer, $"Timer {Name(timer)} has no description (timers have no public flag).");
            foreach (var property in m.SiteProperties.OfType<IObjectSignature>().Where(Empty))
                Add("missing-description", property, $"SiteProperty {Name(property)} has no description (site properties have no public flag).");
            foreach (var variable in List(m, "SessionVariables").OfType<IObjectSignature>().Where(Empty))
                Add("missing-description", variable, $"SessionVariable {Name(variable)} has no description (session variables have no public flag).");
        });

        // Unused elements: Service Studio's own warnings.
        Try("unused-action", () => { foreach (var w in Warned("UnusedUserAction", "UnusedAction")) Add("unused-action", w.Owner!, w.Detail); });
        Try("unused-query", () => { foreach (var w in Warned("UnusedScreenData")) Add("unused-query", w.Owner!, w.Detail); });
        return new(hits, prints, failures);
    }

    static T? Ancestor<T>(IObjectSignature o) where T : class
    {
        for (IObjectSignature? p = o; p is not null; p = p.Parent) if (p is T found) return found;
        return null;
    }

    static int Size(Flow f) => f.Nodes.Count(n => n is not IStartNode && n is not IEndNode && !IsComment(n));

    // The value without the arguments of EncodeSql(...) and BuildSafe_...(...): those parts are sanitized.
    static string Unsanitized(string value)
    {
        for (var match = Sanitizer.Match(value); match.Success; match = Sanitizer.Match(value))
        {
            var depth = 1;
            var end = match.Index + match.Length;
            while (end < value.Length && depth > 0) depth += value[end++] switch { '(' => 1, ')' => -1, _ => 0 };
            value = value[..match.Index] + "\"\"" + value[end..];
        }
        return value;
    }

    // Names whose values a caller controls: inputs and variables of the owning screen (for data actions and
    // preparations), and inputs of public actions, service actions and exposed REST methods.
    static HashSet<string> ExternalInputs(Flow f)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (f.Screen is not null)
            foreach (var p in List(f.Screen, "InputParameters").Concat(List(f.Screen, "LocalVariables"))) names.Add(Name(p));
        if (Reflect.Get(f.Owner, "Public") is true || Implements(f.Owner, "ServiceAction") || Reflect.Property(f.Owner, "HTTPMethod") is not null)
            foreach (var p in List(f.Owner, "InputParameters")) names.Add(Name(p));
        return names;
    }

    // Nesting depth: a For Each covers its loop body; an If or Switch covers the nodes before its branches join
    // again (its nearest post-dominator). Branches that meet only at an End (guards, early exits) do not nest.
    internal static int Nesting(IReadOnlyList<object> nodes, Func<object, IEnumerable<(object target, bool cycle)>> next)
    {
        var index = nodes.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        var count = nodes.Count;
        var exit = count; // virtual exit after every End, Raise or Destination
        var succ = Enumerable.Range(0, count).Select(i => next(nodes[i]).Where(t => index.ContainsKey(t.target)).Select(t => index[t.target]).ToList()).ToList();
        for (var i = 0; i < count; i++) if (succ[i].Count == 0) succ[i].Add(exit);
        // Post-dominator sets by iteration; small flows, so plain bit arrays are enough.
        var pdom = Enumerable.Range(0, count + 1).Select(i => { var b = new BitArray(count + 1, i != exit); if (i == exit) b[exit] = true; return b; }).ToList();
        for (var changed = true; changed;)
        {
            changed = false;
            for (var i = 0; i < count; i++)
            {
                var b = new BitArray(count + 1, true);
                foreach (var s in succ[i]) b.And(pdom[s]);
                b[i] = true;
                if (!Same(b, pdom[i])) { pdom[i] = b; changed = true; }
            }
        }
        int? Join(int i)
        {
            // The nearest strict post-dominator: the one that all other strict post-dominators also post-dominate.
            var strict = Enumerable.Range(0, count).Where(j => j != i && pdom[i][j]).ToList();
            foreach (var j in strict) if (strict.All(k => k == j || pdom[j][k])) return j;
            return null;
        }
        var depth = new int[count];
        for (var i = 0; i < count; i++)
        {
            var scope = new HashSet<int>();
            var cycle = next(nodes[i]).Where(t => t.cycle && index.ContainsKey(t.target)).Select(t => index[t.target]).ToList();
            int? stop = i;
            if (cycle.Count == 0)
            {
                // A join that only ends the flow is a shared End: treat the branch as a guard, not as nesting.
                if (succ[i].Count(s => s != exit) < 2 || Join(i) is not { } join || succ[join].All(s => s == exit)) continue;
                stop = join;
                cycle = succ[i];
            }
            var pending = new Stack<int>(cycle);
            while (pending.TryPop(out var n))
                if (n != exit && n != stop && n != i && scope.Add(n)) foreach (var s in succ[n]) pending.Push(s);
            foreach (var n in scope) depth[n]++;
        }
        return count == 0 ? 0 : depth.Max();
    }

    static bool Same(BitArray a, BitArray b) { for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }

    static int Nesting(Flow f) => Nesting(f.Nodes.Cast<object>().ToList(), n => f.Next[(IObjectSignature)n].Select(t => ((object)t.target, t.cycle)));

    static List<(IObjectSignature target, bool cycle)> Next(Flow f, IObjectSignature n) => f.Next.TryGetValue(n, out var t) ? t : [];

    // A model read that may throw inside the model (a missing part of an odd flow): null then, like a missing reference.
    static object? Read(object o, string key)
    {
        try { return Reflect.Get(o, key); }
        catch (Exception e) when (e is System.Reflection.TargetInvocationException or NullReferenceException or InvalidCastException or NotSupportedException or InvalidOperationException) { return null; }
    }

    // Structure hash: node kinds, the elements each node points at (called actions, aggregate sources, refreshed data,
    // destinations), expressions and connections, in a fixed walk order from Start.
    static string Hash(Flow f)
    {
        var order = new Dictionary<IObjectSignature, int>();
        var text = new StringBuilder();
        var pending = new Stack<IObjectSignature>();
        foreach (var root in f.Nodes.Where(n => n is IStartNode || n.GetType().GetInterfaces().Any(i => i.Name.Contains("ExceptionHandler"))).Reverse()) pending.Push(root);
        while (pending.TryPop(out var n))
        {
            if (order.ContainsKey(n)) continue;
            order[n] = order.Count;
            foreach (var t in Next(f, n).AsEnumerable().Reverse()) pending.Push(t.target);
        }
        foreach (var (n, i) in order.OrderBy(p => p.Value))
        {
            text.Append(i).Append(':').Append(n.GetType().Name);
            var parts = new List<IObjectSignature> { n };
            try { parts.AddRange(ModuleReferences.Descendants(n)); }
            catch (Exception e) when (e is System.Reflection.TargetInvocationException or NullReferenceException or InvalidCastException or NotSupportedException) { }
            var references = parts.Where(p => p is not IExpression).SelectMany(p => ReferenceKeys.Select(k => Read(p, k)))
                .OfType<IObjectSignature>().Where(r => !parts.Contains(r)).Select(r => r.GetType().Name + ":" + PathOf(r)).Order(StringComparer.Ordinal);
            text.Append(" {").Append(string.Join("|", references)).Append('}');
            var expressions = parts.OfType<IExpression>().Select(e => Read(e, "Text") as string ?? "").Order(StringComparer.Ordinal);
            text.Append(" [").Append(string.Join("|", expressions)).Append(']');
            if (n is ISQLNode) text.Append(" sql ").Append(Read(n, "Statement") as string);
            foreach (var t in Next(f, n)) text.Append(t.cycle ? " c" : " >").Append(order.GetValueOrDefault(t.target, -1));
            text.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    // Groups of equal fingerprints across all modules: each copy after the first (by module, then path) is one finding.
    internal static IEnumerable<Hit> Duplicates(IEnumerable<Fingerprint> prints) =>
        prints.GroupBy(p => p.Hash).Where(g => g.Count() > 1).SelectMany(g =>
        {
            var copies = g.OrderBy(p => p.Module, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Element, StringComparer.Ordinal).ToList();
            var first = copies[0];
            return copies.Skip(1).Select(c => new Hit("duplicated-flow", c.Module, c.Element,
                $"{c.Element} has the same {c.Nodes} nodes as {(c.Module == first.Module ? "" : first.Module + ".")}{first.Element}."));
        });
}
