using System.Text.Json;
using System.Text.RegularExpressions;

namespace OmlCli;

// The `oml check` rule catalog and its input checks (arguments, outputs, rules file, baseline). No model types:
// the launcher compiles this file too, so `oml check --team` can refuse bad input before it downloads anything.
// Severities are oml's own defaults: the OutSystems docs publish no severity per pattern.
// aiMentorName is the AI Mentor / Architecture Dashboard pattern name only where the meaning is the same; never a guess.
static class CheckCatalog
{
    internal sealed record Rule(string Id, string Category, string Severity, string Title, string Detail, string Fix,
        string AiMentorName, bool Enabled = true, Dictionary<string, int>? Options = null, bool SeveralModules = false);

    const string Perf = "Performance", Sec = "Security", Maint = "Maintainability", Arch = "Architecture";

    internal static readonly Rule[] All =
    [
        new("aggregate-unlimited", Perf, "high", "Screen aggregate without Max. Records",
            "An aggregate that runs for a screen or block has no Max. Records. It can fetch every row of the tables and slow the page as the data grows.",
            "Set Max. Records to the number of rows the screen shows (or one page), and use paging for more.", "Unlimited records in Aggregate"),
        new("sql-unlimited", Perf, "medium", "Screen SQL query without a row limit",
            "A SQL query that runs for a screen or block has no Max. Records and no row limit (TOP, LIMIT, FETCH FIRST or ROWNUM) in its statement. It can return every row as the data grows.",
            "Set Max. Records to the rows the screen shows. Better, also limit the rows in the statement itself (for example SELECT TOP (50)), so the database does not prepare rows that the screen never uses.", "Unlimited records in SQL query"),
        new("query-in-loop", Perf, "high", "Aggregate or SQL query inside a loop",
            "A query runs once for every iteration of a For Each. The number of database calls grows with the list.",
            "Fetch the data once before the loop, with a join or a filter on all keys, and match the rows in memory.", "Aggregate or SQL query inside a cycle"),
        new("server-call-in-client-loop", Perf, "high", "Server request inside a client loop",
            "Service Studio reports a server request inside a For Each of a client flow (its warning IteratedServerInteraction). Each iteration is a separate request to the server.",
            "Send the whole list to one server action, or fetch all data in one aggregate, and loop on the server.", ""),
        new("multiple-server-calls", Perf, "medium", "Several server requests in one client action",
            "Service Studio reports that a client flow calls the server more than once on one path (its warning ConsecutiveServerInteractions). Each request adds a network round trip.",
            "Move the calls into one server action that does all the work and returns what the screen needs.", "Multiple server requests inside Client Actions"),
        new("server-call-in-client-event", Perf, "high", "Server request in a screen lifecycle event",
            "An OnInitialize, OnReady, OnRender or OnAfterFetch handler calls the server. The screen waits for the request, or repeats it on each render.",
            "Fetch the data with screen aggregates or data actions, which run in parallel, and keep event handlers on the client.", "Server requests in client events"),
        new("large-session-variable", Perf, "medium", "Session variable with a record or list type",
            "A session variable holds a record or a list. The session is stored in the database and read on every request, so large values slow every page.",
            "Keep only identifiers or small basic values in session variables and fetch the rest when needed.", "Large Session Variable"),
        new("render-assign", Perf, "medium", "OnRender changes screen data",
            "Service Studio reports that the OnRender handler of a screen or block changes its data (its warning ClientActionWithModelAssignmentOnLifecycleEvent). Each change renders the screen again, which runs the handler again, so it can loop without end. A one-time flag guarded by an If is also reported.",
            "Change the data in OnInitialize, in OnAfterFetch or in the action of a user event. Keep OnRender for work that changes no screen or block data.", ""),
        new("self-refreshing-aggregate", Perf, "medium", "On After Fetch refreshes its own source",
            "Service Studio reports that the On After Fetch handler of an aggregate refreshes that same aggregate (its warning SelfRefreshingAggregate). Each refresh runs the handler again, so it can loop without end.",
            "Remove the refresh from the handler. To load more data after a fetch, refresh a local storage aggregate, or merge the server fetches into one data action.", ""),
        new("site-property-update", Perf, "medium", "Site property changed in logic",
            "An Assign writes a site property. Each change clears the module cache and makes the next requests slower.",
            "Keep values that change at runtime in an entity. Use site properties for settings that rarely change.", "Site property update"),
        new("timer-long-timeout", Perf, "medium", "Timer with a long timeout",
            "A timer may run longer than the limit, and its action does not call Wake<Timer> to continue in a new run. Long timers hold one of the few timer slots of a front-end server and are restarted from the start after a failure.",
            "Process the data in batches and stop before the timeout, then wake the timer again to continue.", "Avoid long-running Timers",
            Options: new() { ["maxMinutes"] = 30 }),
        new("anonymous-screen-server", Sec, "high", "Anonymous screen calls the server",
            "A screen that anonymous users can open has server aggregates, data actions or server action calls. Each one is a public endpoint that anyone can call with any input.",
            "Remove Anonymous access unless the screen must be public. On public screens, call only server logic that checks its inputs and returns no private data.",
            "Automatically generated publicly exposed REST endpoint"),
        new("registered-screen", Sec, "low", "Screen open to every registered user",
            "A screen allows the Registered role. Any user signed in to any application of the same environment can open it, not only the users of this application.",
            "Use custom roles for screens with private data, and turn off Registered.", "Avoid setting Registered Roles to access Screens", Enabled: false),
        new("escape-content-off", Sec, "high", "Expression shows unescaped input",
            "Service Studio reports HTML injection on an Expression whose Escape Content is No (its warning HTMLInjection): the value is not fixed text and not inside one of the Encode functions, and it is written into the page as HTML. Traditional Web only.",
            "Set Escape Content to Yes. If the value must be HTML, pass it through SanitizeHtml (Sanitization extension) or EncodeHtml.", "JavaScript or HTML injection"),
        new("sql-expand-inline", Sec, "medium", "SQL parameter with Expand Inline",
            "Service Studio warns that a SQL parameter with Expand Inline set to Yes gets a value that is not a fixed text, so the value becomes part of the SQL text (its SQLInjection warnings). A value that contains user input allows SQL injection.",
            "Use a normal parameter. If you must build SQL text, use only fixed values chosen in logic, EncodeSql, or the BuildSafe_InClause functions.", ""),
        new("sql-expand-inline-input", Sec, "high", "Expand Inline SQL parameter fed by input",
            "The same Service Studio warning, and the value names a screen input or variable, or an input of a public action, service action or exposed REST method, outside EncodeSql and BuildSafe_ functions. A caller can change that value and inject SQL.",
            "Use a normal parameter, or map the input to a fixed list of allowed SQL texts in logic before the query.", "Vulnerable SQL injection"),
        new("getuserid-client", Sec, "high", "GetUserId() from the client sent to the server",
            "A client flow passes GetUserId() itself (or a variable assigned GetUserId()) as an argument of a server call. The client can change that value, so the server may act for another user.",
            "Call GetUserId() inside the server action or aggregate filter instead of passing it from the client.", "Insecure usage of GetUserId function on client context"),
        new("getuserid-block-argument", Sec, "high", "GetUserId() passed to a block",
            "A block instance gets GetUserId() itself as an input. Block inputs are client values that a user can change.",
            "Read the user inside the server logic that needs it, not from a block input.", "Insecure usage of GetUserId in client block parameters"),
        new("site-property-secret", Sec, "high", "Secret-looking site property not marked Secret",
            "A Text site property has a name like password, token, secret, key or connection string, but Is Secret is No. Its value is shown in clear text in Service Center.",
            "Set Is Secret to Yes (needs Platform Server 11.25.0 or later), or keep the value in a secure store.", "Exposed secrets in site properties"),
        new("client-variable-secret", Sec, "medium", "Secret-looking client variable",
            "A Text client variable has a name like password, token, secret, key or connection string. Client variables are kept in the browser: the user can read and change them, and apps with the same user provider share them.",
            "Keep secrets on the server, in a secret site property or an entity, and use them only in server logic.", ""),
        new("rest-no-auth", Sec, "low", "Exposed REST API without authentication",
            "An exposed REST API has Authentication set to None. This is often correct when an API gateway (for example Kong) does the authentication, so the rule is off by default.",
            "Turn this rule on only when your APIs must authenticate in OutSystems. Then use Basic or Custom authentication.", "Exposed REST services are not secured", Enabled: false),
        new("public-entity-writable", Arch, "medium", "Public entity is not read-only",
            "A public entity has Expose Read Only set to No. Consumer modules can create, change and delete its records, so the owner module cannot protect its rules.",
            "Set Expose Read Only to Yes and offer public server or service actions for changes.", "Public Entities aren't read-only"),
        new("long-flow", Maint, "low", "Long flow without comments",
            "A flow has many nodes and no comment. Long flows are hard to read, test and change.",
            "Split the flow into smaller actions with clear names, or add comments that explain each part.", "Long undocumented flow",
            Options: new() { ["maxNodes"] = 40, ["maxScreenNodes"] = 20 }),
        new("deep-nesting", Maint, "low", "Deeply nested flow",
            "A flow has branches and loops nested deeper than the limit. Deep nesting is hard to follow and to test.",
            "Move inner branches into separate actions, or check exit conditions early.", "", Options: new() { ["maxDepth"] = 4 }),
        new("duplicated-flow", Maint, "low", "Duplicated flow",
            "Two or more flows have the same nodes, connections and expressions. A fix in one copy is easy to miss in the others.",
            "Keep one action and call it from the other places.", "Duplicated Code", Options: new() { ["minNodes"] = 5 }),
        new("missing-description", Maint, "low", "Public element without description",
            "The module, a public element or a parameter of a public action has no description. Consumers do not know what it does or what to pass.",
            "Write a short description: what it does, and what each parameter means.", "Missing descriptions on public element or parameter",
            Options: new() { ["allNames"] = 0, ["attributes"] = 0 }),
        new("unused-action", Maint, "low", "Unused action",
            "Service Studio reports an action that is never used in this module.",
            "Delete it, or make it public (ask the user: rule 7).", "Unused Actions in Module"),
        new("unused-query", Maint, "low", "Unused aggregate or SQL query",
            "Service Studio reports an aggregate or SQL query whose result is never used. It still runs and reads the database.",
            "Delete the query, or use its result.", "Unused Aggregate or SQL Query"),
        // Reused from `oml architecture`; they need two or more input modules.
        new("upward-reference", Arch, "high", "A module references a higher layer",
            "A lower layer module uses a module of a higher layer. Lower layers then change with the upper ones and stop being reusable.",
            "Move the shared contract and implementation to a lower layer, or remove this dependency.", "", SeveralModules: true),
        new("side-reference-enduser", Arch, "high", "End-user modules reference each other",
            "An end-user module uses elements other than screens from another end-user module. This couples the user applications.",
            "Move the shared elements into a Core or Foundation module.", "", SeveralModules: true),
        new("cycle", Arch, "high", "Modules form a dependency cycle",
            "Modules depend on each other in a cycle. They cannot change or deploy independently.",
            "Move shared contracts to a lower module and remove one direction of the cycle.", "", SeveralModules: true),
        new("core-ui", Arch, "medium", "A Core module contains screens",
            "Screens in a Core module tie reusable business logic to one user interface.",
            "Move user screens into an end-user module.", "", SeveralModules: true),
        new("foundation-business", Arch, "medium", "A Foundation module owns business entities",
            "Non-static entities in a Foundation module tie technical utilities to business data.",
            "Move business entities into a Core module.", "", SeveralModules: true),
        new("large-module", Arch, "low", "A module has more than 150 actions, screens and entities",
            "A very large module is hard to understand and to change safely.",
            "Split independent responsibilities into smaller modules.", "", SeveralModules: true),
        new("unused-public", Arch, "low", "Public elements have no consumer in the input",
            "No other input module uses these public elements. Modules outside the input may still use them.",
            "Check other applications before you make unused elements private.", "", SeveralModules: true),
    ];
}

sealed class CheckInputError(string message) : Exception(message);

static class CheckInput
{
    internal static readonly string[] Severities = ["high", "medium", "low"];

    internal sealed record RuleSetting(bool Enabled, string Severity, Dictionary<string, int> Options);
    internal sealed record Ignore(string Rule, string Pattern, Regex Match, string Reason);
    internal sealed record Settings(Dictionary<string, RuleSetting> Rules, List<Ignore> Ignores);
    internal sealed record Request(List<string> Sources, List<string> Apps, string? TeamLabel, string? Layers, string? Html, string? Json,
        string? RulesPath, string? BaselinePath, string? FailOn, string? InitRules, Settings Settings, Dictionary<string, int>? Baseline);

    // args[0] is "check". teamPreflight: the launcher's check before a --team download (no input module yet).
    public static Request Parse(IReadOnlyList<string> args, bool teamPreflight = false)
    {
        var sources = new List<string>();
        var apps = new List<string>();
        var singles = new Dictionary<string, string>();
        var init = false;
        for (var i = 1; i < args.Count; i++)
        {
            var option = args[i];
            if (option == "--init-rules") { init = true; continue; }
            if (!option.StartsWith("--") && Path.GetExtension(option).Equals(".oml", StringComparison.OrdinalIgnoreCase)) { sources.Add(option); continue; }
            if (option is not ("--in" or "--app" or "--rules" or "--baseline" or "--layers" or "--out" or "--json" or "--fail-on" or "--team-label")
                || i + 1 >= args.Count || args[i + 1].StartsWith("--"))
                throw new CheckInputError("check accepts <module.oml>, repeated --in <folder|file.oml|file.oap> and --app <Name>, --team <Name>, --rules <oml-rules.json>, --baseline <old-check.json>, --layers <layers.json>, --out <report.html>, --json <report.json>, --fail-on high|medium|low, or --init-rules [--out <oml-rules.json>].");
            var value = args[++i];
            if (option == "--in") sources.Add(value);
            else if (option == "--app") apps.Add(value);
            else if (!singles.TryAdd(option, value)) throw new CheckInputError($"Duplicate option {option}.");
        }
        string? Full(string key) => singles.TryGetValue(key, out var v) ? Path.GetFullPath(v) : null;
        if (init)
        {
            if (sources.Count > 0 || apps.Count > 0 || singles.Keys.Any(k => k != "--out")) throw new CheckInputError("--init-rules accepts only --out <oml-rules.json>.");
            var target = Full("--out") ?? Path.GetFullPath("oml-rules.json");
            NewOutput(target);
            return new(sources, apps, null, null, null, null, null, null, null, target, ReadSettings(null), null);
        }
        if (sources.Count == 0 && !teamPreflight) throw new CheckInputError("check requires a module .oml or --in <folder|file.oml|file.oap>.");
        var failOn = singles.GetValueOrDefault("--fail-on");
        if (failOn is not null && !Severities.Contains(failOn)) throw new CheckInputError("--fail-on must be high, medium or low.");
        var html = Full("--out");
        var json = Full("--json");
        if (html is not null && string.Equals(html, json, StringComparison.OrdinalIgnoreCase)) throw new CheckInputError("HTML and JSON outputs must have different paths.");
        foreach (var path in new[] { html, json }.OfType<string>()) NewOutput(path);
        var layers = Full("--layers");
        if (layers is not null && !File.Exists(layers)) throw new CheckInputError($"Layers file not found: {layers}");
        var rules = Full("--rules");
        var baseline = Full("--baseline");
        return new(sources, apps, singles.GetValueOrDefault("--team-label"), layers, html, json, rules, baseline, failOn, null, ReadSettings(rules), ReadBaseline(baseline));
    }

    // Outputs are new files in an existing folder; both are checked before anything is read or written.
    internal static void NewOutput(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new CheckInputError($"Output already exists: {path}. Use a new output path, or delete that file first.");
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) throw new CheckInputError($"Output folder does not exist: {folder}");
    }

    // The team rules file: every rule is optional, so a file from an older oml still works; anything unknown is an error.
    internal static Settings ReadSettings(string? path)
    {
        var rules = CheckCatalog.All.ToDictionary(x => x.Id, x => new RuleSetting(x.Enabled, x.Severity, new Dictionary<string, int>(x.Options ?? new())));
        var ignores = new List<Ignore>();
        if (path is null) return new(rules, ignores);
        CheckInputError Bad(string message) => new($"Rules file {path}: {message}");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { throw Bad(e.Message); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Bad("expected a JSON object with \"rules\" and \"ignore\".");
            foreach (var p in root.EnumerateObject())
                if (p.Name is not ("schema" or "rules" or "ignore")) throw Bad($"unknown key \"{p.Name}\"; use schema, rules and ignore.");
            if (root.TryGetProperty("schema", out var schema) && !(schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) && version == 1))
                throw Bad("schema must be 1.");
            var seen = new HashSet<string>();
            foreach (var entry in Items(root, "rules", Bad))
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String)
                    throw Bad("each rule needs an \"id\".");
                var id = idValue.GetString()!;
                if (!rules.TryGetValue(id, out var current)) throw Bad($"unknown rule \"{id}\". Run oml check --init-rules to see the rule ids.");
                if (!seen.Add(id)) throw Bad($"rule \"{id}\" is listed twice.");
                var enabled = current.Enabled;
                var severity = current.Severity;
                foreach (var p in entry.EnumerateObject())
                    switch (p.Name)
                    {
                        case "id" or "title" or "category" or "aiMentorName": break; // written by --init-rules for people to read
                        case "enabled":
                            if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Bad($"rule \"{id}\": enabled must be true or false.");
                            enabled = p.Value.GetBoolean(); break;
                        case "severity":
                            if (p.Value.ValueKind != JsonValueKind.String || !Severities.Contains(p.Value.GetString())) throw Bad($"rule \"{id}\": severity must be high, medium or low.");
                            severity = p.Value.GetString()!; break;
                        case "options":
                            if (p.Value.ValueKind != JsonValueKind.Object) throw Bad($"rule \"{id}\": options must be an object.");
                            foreach (var o in p.Value.EnumerateObject())
                            {
                                if (!current.Options.ContainsKey(o.Name))
                                    throw Bad($"rule \"{id}\" has no option \"{o.Name}\"" + (current.Options.Count == 0 ? "." : $"; use {string.Join(", ", current.Options.Keys)}."));
                                var switchOption = o.Name is "allNames" or "attributes"; // 0 = off, 1 = on
                                if (o.Value.ValueKind != JsonValueKind.Number || !o.Value.TryGetInt32(out var number) || number < (switchOption ? 0 : 1) || switchOption && number > 1)
                                    throw Bad(switchOption ? $"rule \"{id}\": option {o.Name} must be 0 or 1." : $"rule \"{id}\": option {o.Name} must be a whole number of at least 1.");
                                current.Options[o.Name] = number;
                            }
                            break;
                        default: throw Bad($"rule \"{id}\": unknown key \"{p.Name}\"; use enabled, severity and options.");
                    }
                rules[id] = current with { Enabled = enabled, Severity = severity };
            }
            foreach (var entry in Items(root, "ignore", Bad))
            {
                string Field(string name)
                {
                    if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
                        throw Bad("each ignore entry needs \"rule\", \"element\" and \"reason\" texts.");
                    return v.GetString()!;
                }
                var (rule, element, reason) = (Field("rule"), Field("element"), Field("reason"));
                foreach (var p in entry.EnumerateObject())
                    if (p.Name is not ("rule" or "element" or "reason")) throw Bad($"ignore entry: unknown key \"{p.Name}\"; use rule, element and reason.");
                if (rule != "*" && !rules.ContainsKey(rule)) throw Bad($"ignore entry: unknown rule \"{rule}\".");
                // Glob: * is any text, ? one character; case-insensitive, matched against the element path or Module/path.
                var regex = new Regex("^" + Regex.Escape(element).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                ignores.Add(new(rule, element, regex, reason));
            }
        }
        return new(rules, ignores);
    }

    static IEnumerable<JsonElement> Items(JsonElement root, string key, Func<string, CheckInputError> bad)
    {
        if (!root.TryGetProperty(key, out var value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw bad($"\"{key}\" must be an array.");
        return value.EnumerateArray().ToList();
    }

    internal static string Key(string rule, string module, string element) => rule + "\n" + module.ToUpperInvariant() + "\n" + element;

    // Counts of earlier findings by rule, module and element path.
    static Dictionary<string, int>? ReadBaseline(string? path)
    {
        if (path is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array)
                throw new CheckInputError($"Baseline {path} is not an oml check report: it has no \"findings\" array.");
            var counts = new Dictionary<string, int>();
            foreach (var f in findings.EnumerateArray())
            {
                string Field(string name) => f.ValueKind == JsonValueKind.Object && f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                if (Field("rule").Length == 0 || Field("module").Length == 0) throw new CheckInputError($"Baseline {path}: each finding needs \"rule\" and \"module\".");
                var key = Key(Field("rule"), Field("module"), Field("element"));
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
            return counts;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { throw new CheckInputError($"Baseline {path}: {e.Message}"); }
    }
}
