using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml config`: the project configuration, `.oml/oml-config.json` in the project root. A person turns parts of the work
// on or off there (phone and tablet, dark theme, browser checks, questions, which steps need a yes, which commands may run, apply's
// layout, migrate's choices, the hooks), and agents get the result as a few plain lines they must follow instead of
// deciding or asking each time:
//   oml config                    the profile for an agent: only the lines that differ from the default work
//   oml config show               every setting: value, default, choices and what it does
//   oml config set <key> <value>  change one setting (the file is made when it is missing)
//   oml config init               write the file with every default, to edit by hand
// The file holds only settings: no secrets, no paths outside the project. A missing file or key means the default.
// autoApprove.* true means agents run that step without asking; commands.* off makes oml refuse the command. The .oml
// write guard is locked on. oml hooks read the same file: the session-start hook gives the profile to the agent, the
// pre-tool hook follows the hooks.* and browser settings, the end-of-turn hook follows memory.reminder.
// Up to 1.22 the file was .oml/config.json: that name is still read, and the next change writes the new one.
static class ProjectConfig
{
    const string Usage = "Usage: oml config [show | init | example | set <key> <value>] [--dir <project folder>]";
    internal const string FileName = "oml-config.json";
    internal const string LegacyFileName = "config.json";

    // Max: the longest value a text setting takes.
    internal sealed record Setting(string Key, string Kind, string Default, string[]? Choices, string About, bool Locked = false, int Max = 300);

    // Kind: bool, choice (one of Choices), list (comma-separated, each one of Choices) or text.
    internal static readonly Setting[] Settings =
    [
        new("ui.targets", "list", "desktop,tablet,phone", ["desktop", "tablet", "phone"], "Screen sizes the project supports. Agents design and test only these."),
        new("ui.themes", "list", "light,dark", ["light", "dark"], "Themes the project supports. Agents write and check styles only for these."),
        new("ui.rtl", "bool", "false", null, "Right-to-left layouts are needed."),
        new("ui.accessibility", "bool", "true", null, "Check contrast, labels and keyboard use for new UI."),
        new("ui.translations", "bool", "false", null, "New texts need translations (the module is multilingual)."),
        new("checks.browser", "bool", "true", null, "Run oml browser page after a publish to see the result in the app."),
        new("checks.uiPreview", "bool", "true", null, "Before building UI, show a preview injected into the running app (browser dev tools or oml browser try) and build after the user's yes (golden rule 1)."),
        new("checks.review", "bool", "true", null, "Write the oml review file (.md) for a staged change."),
        new("checks.uiChecklist", "bool", "true", null, "Follow oml doc ui-checklist for UI work."),
        new("checks.bestPractices", "bool", "true", null, "Read oml doc best-practices before adding entities, screens or flows no recipe covers."),
        new("checks.testScenarios", "bool", "true", null, "Offer oml test scenarios after a change."),
        new("checks.testData", "bool", "true", null, "Use oml test data to prepare and clean up test records."),
        new("checks.checkDiff", "bool", "true", null, "Run oml check --diff on a staged change before the report."),
        new("checks.validate", "bool", "true", null, "Run oml validate --warnings on the staged file right before oml install."),
        new("ask.testing", "bool", "true", null, "Ask whether to test or look at the app in the browser. Off: decide from checks.*, never ask, and list what was not run."),
        new("ask.testScenarios", "bool", "true", null, "Ask whether the user wants test scenarios. Off: follow checks.testScenarios without asking."),
        new("ask.devUrl", "bool", "true", null, "Ask for the DEV URL of a screen. Off: use app.devUrl, or skip the live checks when it is empty."),
        new("ask.producerChoice", "bool", "true", null, "Ask which producer element to use even for one match. Off: one match is used without a question."),
        new("autoApprove.apply", "bool", "true", null, "Agents run oml apply (it writes a staged copy; the source is not changed) without asking. Off: show the spec and ask before each apply."),
        new("autoApprove.install", "bool", "false", null, "Agents run oml install without asking, after oml validate shows zero errors. Off: ask first."),
        new("autoApprove.publish", "bool", "false", null, "Agents run oml publish without asking. Off: ask first."),
        new("autoApprove.references", "bool", "false", null, "Agents refresh references (oml references) without asking. Off: ask first."),
        new("autoApprove.deploy", "bool", "false", null, "Agents run LifeTime tag, deploy, abort and continue without asking. Off: ask first."),
        new("autoApprove.deletes", "bool", "false", null, "Agents delete existing elements (actions, screens, blocks, structures, static records and the like) without asking. Entities and attributes are never deleted. Off: ask first."),
        new("autoApprove.dataDeletion", "bool", "false", null, "Agents run what changes or deletes production data without asking: SQL that changes data, a shorter length, fewer decimals or a new mandatory setting on an existing attribute, test data cleanup. Off: ask first."),
        new("commands.install", "bool", "true", null, "oml install may run. Off: it is refused, and agents leave the install to the user."),
        new("commands.publish", "bool", "true", null, "oml publish may run. Off: it is refused, and agents name the publish step for the user."),
        new("commands.references", "bool", "true", null, "oml references may run. Off: it is refused; references are refreshed in Service Studio."),
        new("commands.pull", "bool", "true", null, "oml pull may run. Off: it is refused; the user saves the .oml from Service Studio."),
        new("commands.lifetime", "bool", "true", null, "oml lifetime may run. Off: it is refused; deployments are done in LifeTime."),
        new("commands.studio", "bool", "true", null, "oml studio may drive Service Studio (open, close, tabs, focus). Off: it is refused."),
        new("commands.migrate", "bool", "true", null, "oml migrate may run. Off: it is refused."),
        new("apply.autoLayout", "bool", "true", null, "oml apply lays out every flow it changes. Off: flows that already existed keep their layout (new nodes are not arranged; tidyFlows still does it on request), new flows are still laid out."),
        new("migrate.serverStore", "bool", "true", null, "oml migrate keeps session variables that server logic writes (or sensitive, or records) in a server store (entity SessionValue). Off: they stay manual."),
        new("migrate.mergeServerCalls", "bool", "true", null, "oml migrate merges consecutive server calls of a screen action into one server action (one request). Off: each call stays."),
        new("migrate.copyExposed", "bool", "false", null, "oml migrate copies exposed REST and SOAP services into the new module (as --copy-exposed). Off: they stay in the old module."),
        new("output.color", "choice", "auto", ["auto", "always", "never"], "Colours in the readable view for people: auto (when the terminal shows them), always, never. NO_COLOR in the environment turns them off. Agents read JSON, which has no colours."),
        new("lane.fast", "bool", "true", null, "Small changes (only CSS values or texts of existing elements) skip the browser check, the review file and questions."),
        new("browser", "choice", "oml", ["oml", "agent"], "Who opens the app to look at it: oml (oml browser, its own Edge or Chrome) or agent (the agent's built-in browser)."),
        new("app.devUrl", "text", "", null, "Base URL of the app in DEV, such as https://dev.example.com/Orders."),
        new("author", "text", "", null, "The name written as creator and last modifier of what oml creates or changes in this project; empty means not set. The variable OML_AUTHOR wins over it.", Max: 100),
        new("outsystems.mcpServer", "url-local", "", null, "Address of Service Studio's MCP server (O11), such as http://127.0.0.1:41820/mcp. oml publish, pull, references and studio use it; empty means the default port. Only this computer (127.0.0.1 or localhost)."),
        new("outsystems.serverHost", "text", "", null, "The platform server of the DEV environment, such as dev.example.com (https:// is dropped). oml server and the dashboard use it to list and download modules with Service Studio closed; OML_SERVER_HOST wins over it. The login stays in OML_SERVER_USER and OML_SERVER_PASSWORD, never in this file.", Max: 255),
        new("outsystems.odcMcpServer", "url", "", null, "Address of the OutSystems MCP server of your ODC tenant, such as https://mytenant.outsystems.dev/mcp. Agents use it for ODC apps; oml itself does not call it."),
        new("code.use", "bool", "true", null, "Agents run a step of three or more oml calls with logic between them as one oml code program. Off: they chain commands and use oml batch."),
        new("memory.recall", "bool", "true", null, "Agents recall project memory at the start of a task."),
        new("memory.write", "bool", "true", null, "Agents save what they learned at the end of a task."),
        new("memory.reminder", "bool", "true", null, "The end-of-turn hook reminds an agent that changed something and saved nothing (costs one turn)."),
        new("docs.library", "bool", "true", null, "Agents may search the downloaded OutSystems docs (oml doc --search ... --library)."),
        new("map.use", "bool", "true", null, "Agents use the application map (oml map ...) for questions across modules."),
        new("hooks.bigDocs", "bool", "true", null, "The hook refuses reading the large skill references whole."),
        new("hooks.testDataApi", "bool", "true", null, "The hook refuses calls to the test-data API that do not go through oml test data."),
        new("hooks.externalBrowser", "bool", "true", null, "With browser = agent, the hook refuses shell commands that start Edge, Chrome or Firefox."),
        new("hooks.sessionProfile", "bool", "true", null, "The session-start hook gives this profile to the agent, so it needs no command to read it."),
        new("hooks.omlWrites", "bool", "true", null, "The hook refuses editing or writing .oml files with a tool or a shell.", Locked: true),
    ];

    // Keys that were renamed: the old one still reads and sets the new one.
    static readonly Dictionary<string, string> OldKeys = new(StringComparer.OrdinalIgnoreCase) { ["checks.qa"] = "checks.browser" };

    internal static Setting? Find(string key) =>
        Settings.FirstOrDefault(s => s.Key.Equals(OldKeys.TryGetValue(key, out var renamed) ? renamed : key, StringComparison.OrdinalIgnoreCase));

    // The project root: the nearest folder, from `start` upwards, that has .oml/oml-config.json (or the older
    // .oml/config.json); null when none has.
    internal static string? RootOf(string? start)
    {
        try
        {
            for (var dir = new DirectoryInfo(Path.GetFullPath(string.IsNullOrWhiteSpace(start) ? Environment.CurrentDirectory : start)); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, ".oml", FileName)) || File.Exists(Path.Combine(dir.FullName, ".oml", LegacyFileName))) return dir.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { }
        return null;
    }

    // Where the file is written.
    internal static string PathIn(string root) => Path.Combine(root, ".oml", FileName);
    static string LegacyPathIn(string root) => Path.Combine(root, ".oml", LegacyFileName);
    // Where it is read: the new name, else the older one while it is the only one there.
    internal static string ReadPathIn(string root) => !File.Exists(PathIn(root)) && File.Exists(LegacyPathIn(root)) ? LegacyPathIn(root) : PathIn(root);

    // Settings that were renamed or replaced: the message a file that still has them gets.
    static readonly Dictionary<string, string> Replaced = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ask.install"] = "replaced by autoApprove.install (true: agents install without asking).",
        ["ask.publish"] = "replaced by autoApprove.publish and autoApprove.references (true: agents do it without asking).",
        ["ask.deploy"] = "replaced by autoApprove.deploy (true: agents run LifeTime steps without asking).",
        ["ask.dataDeletion"] = "replaced by autoApprove.dataDeletion (true: agents delete data without asking).",
    };

    // The values of a project: the file's values over the defaults. A file that cannot be read gives the defaults and
    // an error text (the hooks ignore it: a broken file must never block an agent).
    internal sealed class Values
    {
        readonly Dictionary<string, string> set = new(StringComparer.OrdinalIgnoreCase);
        public string? File { get; init; }
        public string? Error { get; init; }
        public List<string> Problems { get; } = [];
        public bool IsSet(string key) => set.ContainsKey(key);
        public string Get(string key) => set.TryGetValue(key, out var v) ? v : Find(key)?.Default ?? "";
        public bool On(string key) => Get(key) == "true";
        public string[] List(string key) => Get(key).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        internal void Put(string key, string value) => set[key] = value;
    }

    internal static Values Load(string? start)
    {
        if (RootOf(start) is not { } root) return new Values();
        var file = ReadPathIn(root);
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(System.IO.File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return new Values { File = file, Error = $"{file} cannot be read ({e.Message}); the defaults apply." }; }
        if (json is null) return new Values { File = file, Error = $"{file} is not a JSON object; the defaults apply." };
        var values = new Values { File = file };
        Read(json, "", values);
        return values;
    }

    // The file is nested ({"ui":{"targets":[...]}}); a dotted key at the top ("ui.targets") is read too.
    static void Read(JsonObject o, string prefix, Values values)
    {
        foreach (var (name, node) in o)
        {
            var key = prefix + name;
            if (key == "version") continue;
            if (node is JsonObject child && Find(key) is null) { Read(child, key + ".", values); continue; }
            if (Replaced.TryGetValue(key, out var replaced)) { values.Problems.Add($"{key}: {replaced} (ignored)"); continue; }
            if (Find(key) is not { } setting) { values.Problems.Add($"{key}: unknown setting (ignored)."); continue; }
            var text = node switch
            {
                JsonArray a => string.Join(",", a.Select(x => x?.ToString() ?? "")),
                JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
                null => "",
                _ => node.ToString(),
            };
            if (Normalize(setting, text, out var value, fromFile: true) is { } problem) { values.Problems.Add($"{key}: {problem} The default applies."); continue; }
            if (setting.Locked && value != setting.Default) { values.Problems.Add($"{key}: cannot be turned off. It stays {setting.Default}."); continue; }
            values.Put(setting.Key, value);
        }
    }

    // The value in its stored form, or what is wrong with it.
    internal static string? Normalize(Setting s, string text, out string value, bool fromFile = false)
    {
        value = text.Trim();
        // A pasted address of the platform server keeps only its host.
        if (s.Key == "outsystems.serverHost" && value.Length > 0) { if (value.Contains("://")) value = value[(value.IndexOf("://") + 3)..]; value = value.Split('/')[0]; }
        switch (s.Kind)
        {
            case "bool":
                var lower = value.ToLowerInvariant();
                if (lower is "true" or "on" or "yes" or "1") { value = "true"; return null; }
                if (lower is "false" or "off" or "no" or "0") { value = "false"; return null; }
                return $"'{text}' is not on or off.";
            case "choice":
                value = value.ToLowerInvariant();
                return s.Choices!.Contains(value) ? null : $"'{text}' is not one of {string.Join(", ", s.Choices!)}.";
            case "list":
                var items = value.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
                if (items.Length == 0) return $"give at least one of {string.Join(", ", s.Choices!)}.";
                if (items.FirstOrDefault(i => !s.Choices!.Contains(i)) is { } bad) return $"'{bad}' is not one of {string.Join(", ", s.Choices!)}.";
                value = string.Join(",", s.Choices!.Where(items.Contains)); // always in the order of Choices
                return null;
            case "url" or "url-local":
                if (value.Length == 0) return null;
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || value.Length > 300)
                    return $"'{text}' is not an http or https address (and it holds no user name or password).";
                // The file is shared with the repository: an address in it must not send a module to another machine.
                if (s.Kind == "url-local" && !uri.IsLoopback) return "Service Studio's MCP server runs on this computer: use 127.0.0.1 or localhost.";
                if (s.Kind == "url" && uri.Scheme != "https" && !uri.IsLoopback) return "use an https address.";
                value = uri.ToString();
                return null;
            default:
                if (value.Length > s.Max || value.Any(char.IsControl)) return $"a text of at most {s.Max} characters on one line.";
                // Only when a person sets it: the hooks load this file on every tool call, and the secret patterns are slow to build.
                if (!fromFile && value.Length > 0 && MemorySecrets.Find(value) is not null) return "this looks like a secret; the config file holds no secrets.";
                return null;
        }
    }

    // The lines an agent follows. Only what differs from the default work is said, so a default project costs
    // almost nothing to read; the locked line is always there.
    internal static List<string> Profile(Values v)
    {
        var lines = new List<string>();
        string Join(IEnumerable<string> items) => string.Join(", ", items);
        var targets = v.List("ui.targets");
        var allTargets = Find("ui.targets")!.Choices!;
        if (targets.Length < allTargets.Length)
            lines.Add($"UI sizes: {Join(targets)} only. Do not design, screenshot or test for {Join(allTargets.Except(targets))}, and do not add styles for them.");
        var themes = v.List("ui.themes");
        if (themes.Length == 1)
            lines.Add($"Theme: {themes[0]} only. Do not write or check {(themes[0] == "light" ? "dark" : "light")} theme styles.");
        if (v.On("ui.rtl")) lines.Add("Right-to-left layouts are needed: check new UI in RTL.");
        if (!v.On("ui.accessibility")) lines.Add("Do not run accessibility checks unless the user asks.");
        if (v.On("ui.translations")) lines.Add("The module is multilingual: new texts need translations.");

        var skipped = new List<string>();
        if (!v.On("checks.browser")) skipped.Add("oml browser");
        if (!v.On("checks.uiPreview")) skipped.Add("the UI preview before building (golden rule 1)");
        if (!v.On("checks.review")) skipped.Add("the oml review file");
        if (!v.On("checks.uiChecklist")) skipped.Add("oml doc ui-checklist");
        if (!v.On("checks.bestPractices")) skipped.Add("oml doc best-practices");
        if (!v.On("checks.testScenarios")) skipped.Add("oml test scenarios");
        if (!v.On("checks.testData")) skipped.Add("oml test data");
        var noRun = new List<string>();
        if (!v.On("checks.checkDiff")) noRun.Add("oml check --diff before the report");
        if (!v.On("checks.validate")) noRun.Add("oml validate before install");
        if (skipped.Count > 0) lines.Add($"Turned off for this project, do not run or offer unless the user asks in this task: {Join(skipped)}. The commands refuse; --no-profile runs one once when the user asks.");
        if (noRun.Count > 0) lines.Add($"Skip these steps of the workflow unless the user asks: {Join(noRun)}.");

        if (!v.On("ask.testing")) lines.Add("Do not ask whether to test or look at the app in the browser. Run what this profile leaves on, then end with one line: Not run: <what you skipped>.");
        if (!v.On("ask.testScenarios")) lines.Add("Do not ask about test scenarios.");
        var url = v.Get("app.devUrl");
        if (url.Length > 0) lines.Add($"DEV URL of the app: {url}");
        if (!v.On("ask.devUrl")) lines.Add(url.Length > 0 ? "Do not ask for the DEV URL; build screen URLs from the one above." : "Do not ask for a DEV URL; skip the checks that need a running page and say so.");
        lines.Add(Author(v) is { Length: > 0 } author ? $"Author for what you create: {author}."
            : "Before the first change, ask the user which author name to write on what you create, then run oml config set author \"<name>\".");
        if (!v.On("ask.producerChoice")) lines.Add("When exactly one producer element matches, use it without asking; say which one you used.");

        if (v.On("lane.fast"))
            lines.Add("Fast lane: when the task changes only CSS values or texts of existing elements, do it in 2 tool calls (read the sheet or widget; spec check and apply). Report the staged file and the changed lines. No QA, review file, memory step or questions.");
        if (v.Get("browser") == "agent")
            lines.Add("Browser: look at the app with your own built-in browser tool. Never start Edge, Chrome or Firefox from a shell. Use oml browser only for saved test runs the user asks for.");
        if (v.Get("outsystems.mcpServer") is { Length: > 0 } studioMcp)
            lines.Add($"Service Studio MCP server (O11): {studioMcp}. oml pull, publish, references and studio already use it; call it yourself only for a step oml has no command for.");
        if (Qa.TestAccounts.Roles(v.File is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(v.File))) is { Count: > 0 } roles)
            lines.Add($"Test accounts of the app, by role: {Join(roles)}. In a browser script log in with {{\"login\": \"<role>\"}}, or fill \"$account:<role>.username\" and \"$account:<role>.password\". Never read .oml/test-accounts.local.json and never ask for a password.");
        if (ServerHost(v) is { Length: > 0 } serverHost)
            lines.Add($"Platform server: {serverHost}. oml server lists, downloads and checks modules there with Service Studio closed.");
        if (v.Get("outsystems.odcMcpServer") is { Length: > 0 } odcMcp)
            lines.Add($"OutSystems MCP server (ODC): {odcMcp}. Use it for ODC apps as skill/reference/odc.md says; ask before publish and deploy there too.");
        if (!v.On("code.use")) lines.Add("Do not use oml code; chain commands and use oml batch.");
        if (!v.On("memory.recall")) lines.Add("Do not run oml memory recall.");
        if (!v.On("memory.write")) lines.Add("Do not write project memory.");
        if (!v.On("docs.library")) lines.Add("Do not search the OutSystems docs library.");
        if (!v.On("map.use")) lines.Add("Do not use oml map.");

        var off = Commands.Where(c => !v.On(c.Key)).Select(c => c.Command).ToList();
        if (off.Count > 0)
            lines.Add($"Commands turned off for this project: {Join(off)}. They are refused. Do not run or offer them; name the step for the user to do in Service Studio or LifeTime.");
        if (!v.On("apply.autoLayout")) lines.Add("oml apply keeps the layout of flows that already exist. Do not add tidyFlows unless the user asks.");
        var migrate = new List<string>();
        if (!v.On("migrate.serverStore")) migrate.Add("no server store for session variables (they stay manual)");
        if (!v.On("migrate.mergeServerCalls")) migrate.Add("consecutive server calls are not merged");
        if (v.On("migrate.copyExposed")) migrate.Add("exposed REST and SOAP services are copied without asking");
        if (migrate.Count > 0) lines.Add($"oml migrate in this project: {Join(migrate)}.");
        lines.AddRange(Approvals(v));
        return lines;
    }

    // The commands a project can turn off: setting, the command as an agent types it.
    static readonly (string Key, string Command)[] Commands =
    [
        ("commands.install", "oml install"), ("commands.publish", "oml publish"), ("commands.references", "oml references"), ("commands.pull", "oml pull"),
        ("commands.lifetime", "oml lifetime"), ("commands.studio", "oml studio"), ("commands.migrate", "oml migrate"),
    ];

    // The approval lines: what agents run without asking (autoApprove.* on) and what still needs a yes. A step whose command
    // is turned off is in neither.
    static IEnumerable<string> Approvals(Values v)
    {
        (string Key, string Step, string? Command)[] steps =
        [
            ("autoApprove.install", "install", "commands.install"), ("autoApprove.publish", "publish", "commands.publish"),
            ("autoApprove.references", "reference refresh", "commands.references"), ("autoApprove.deploy", "LifeTime steps (tag, deploy, abort, continue)", "commands.lifetime"),
            ("autoApprove.deletes", "deleting an existing element", null),
            ("autoApprove.dataDeletion", "anything that changes or deletes production data", null),
        ];
        var live = steps.Where(s => s.Command is null || v.On(s.Command)).ToList();
        var free = live.Where(s => v.On(s.Key)).Select(s => s.Step).ToList();
        var asked = live.Where(s => !v.On(s.Key)).Select(s => s.Step).ToList();
        if (!v.On("autoApprove.apply")) yield return "Ask before each oml apply: show the spec and the staged path first.";
        if (free.Count > 0)
            yield return $"Do not ask before {string.Join(", ", free)} (autoApprove, set by the user): run it when the task needs it, then report the exact command and its result."
                + (free.Contains("install") && v.On("checks.validate") ? " Install only after oml validate --warnings shows zero errors." : "");
        if (asked.Count > 0) yield return $"Always ask before {string.Join(", ", asked)}: restate the exact command and wait for a yes.";
    }

    // The text the session-start hook gives to an agent; null when the project has no config file or the profile is off.
    internal static string? SessionText(string? cwd)
    {
        var v = Load(cwd);
        if (v.File is null || v.Error is not null || !v.On("hooks.sessionProfile")) return null;
        return "oml project profile (.oml/oml-config.json, set by the user; follow it: it overrides the defaults of the skill and AGENTS.md):\n- " + string.Join("\n- ", Profile(v));
    }

    // The platform server host: OML_SERVER_HOST, else outsystems.serverHost of the project; scheme and path dropped.
    internal static string ServerHost(Values v)
    {
        var text = Environment.GetEnvironmentVariable("OML_SERVER_HOST") is { Length: > 0 } env ? env : v.Get("outsystems.serverHost");
        text = text.Trim();
        if (text.Contains("://")) text = text[(text.IndexOf("://") + 3)..];
        return text.Split('/')[0];
    }

    // The author name for what oml creates or changes: OML_AUTHOR when it is a valid name, else the profile's `author`;
    // "" when neither is set.
    internal static string Author(Values v) =>
        Environment.GetEnvironmentVariable("OML_AUTHOR") is { } variable && Normalize(Find("author")!, variable, out var name, fromFile: true) is null && name.Length > 0
            ? name : v.Get("author");

    // The Service Studio MCP address of the project, for the commands that call it (Locations.StudioUrl reads
    // OML_STUDIO_URL), and the author Core writes on what it creates or changes (Authorship reads OML_AUTHOR).
    // A variable the person set wins; a broken config file changes nothing.
    internal static void ApplyEnvironment()
    {
        var v = Load(null);
        if (Environment.GetEnvironmentVariable("OML_AUTHOR") is not { Length: > 0 } && v.Get("author") is { Length: > 0 } author)
            Environment.SetEnvironmentVariable("OML_AUTHOR", author);
        // Core reads it in FlowLayout.Apply: "keep" leaves flows that existed before the apply as they are.
        if (!v.On("apply.autoLayout") && Environment.GetEnvironmentVariable("OML_FLOW_LAYOUT") is not { Length: > 0 })
            Environment.SetEnvironmentVariable("OML_FLOW_LAYOUT", "keep");
        if (Environment.GetEnvironmentVariable("OML_STUDIO_URL") is { Length: > 0 } || Environment.GetEnvironmentVariable("OML_STUDIO_PORT") is { Length: > 0 }) return;
        if (v.Get("outsystems.mcpServer") is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        Environment.SetEnvironmentVariable("OML_STUDIO_URL", url);
        Environment.SetEnvironmentVariable("OML_STUDIO_PORT", uri.Port.ToString()); // the token file is named after the port
    }

    static string SizeOf(int width) => width < 600 ? "phone" : width < 1024 ? "tablet" : "desktop";

    // browser and test scenarios follow the profile: a command whose checks.* setting is off is refused, and so is
    // a width or theme that ui.targets and ui.themes do not support, each with the reason (--no-profile skips this
    // once). test scenarios runs in Core, so the launcher is the only place that can refuse it. Returns the arguments to run, or the refusal.
    internal static (string[] Args, string? Refusal) Adjust(string[] args)
    {
        if (args.Length == 0) return (args, null);
        var command = Commands.FirstOrDefault(c => c.Command == "oml " + args[0]);
        if (command.Key is null && args[0] is not ("browser" or "qa") && args is not ["test", "scenarios", ..]) return (args, null);
        if (args.Contains("--no-profile")) return (args.Where(a => a != "--no-profile").ToArray(), null);
        if (args.Contains("--help") || args.Contains("-h")) return (args, null);
        var v = Load(null);
        if (v.File is null || v.Error is not null) return (args, null);
        if (command.Key is not null)
            return (args, v.On(command.Key) ? null
                : $"{command.Key} is off for this project ({v.File}), so {command.Command} is not run. Do this step in Service Studio or LifeTime, or ask the user; oml config set {command.Key} on turns it on, and --no-profile runs it once when the user asked for it in this task.");
        // Only the commands that do the check: browser start, stop and the bare browser only manage the browser session.
        var check = args[0] == "test" ? "checks.testScenarios" : args.Length > 1 && args[1] is "page" or "run" or "record" or "look" or "sweep" or "diff" or "try" ? "checks.browser" : null;
        if (check is not null && !v.On(check))
            return (args, $"{check} is off for this project ({v.File}), so oml {(args[0] == "test" ? "test scenarios" : args[0])} is not run. Turn it on with oml config set {check} on, or add --no-profile to run it once when the user asked for it in this task.");
        string[] targets = v.List("ui.targets"), themes = v.List("ui.themes");
        string? Value(string option) => System.Array.IndexOf(args, option) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        if (Value("--width") is { } w && int.TryParse(w, out var width) && !targets.Contains(SizeOf(width)))
            return (args, $"--width {width} is a {SizeOf(width)} size, and this project supports only {string.Join(", ", targets)} (ui.targets in {v.File}). Leave it out, or add --no-profile when the user asked for this size in this task.");
        if (Value("--theme") is { } theme && theme is "light" or "dark" && !themes.Contains(theme))
            return (args, $"--theme {theme}: this project supports only the {string.Join(", ", themes)} theme (ui.themes in {v.File}). Leave it out, or add --no-profile when the user asked for this theme in this task.");
        return (args, null);
    }

    public static int Run(string[] args)
    {
        if (args is ["--help" or "-h"]) { Console.WriteLine(CliUsage.For("config")); return 0; }
        string? dir = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dir" && i + 1 < args.Length) dir = args[++i];
            else rest.Add(args[i]);
        }
        if (dir is not null && !Directory.Exists(dir)) return Fail($"Folder not found: {dir}");
        var start = Path.GetFullPath(dir ?? Environment.CurrentDirectory);
        switch (rest)
        {
            case []:
                var v = Load(start);
                TerminalOutput.PrintObject(new
                {
                    profile = Profile(v), file = v.File, error = v.Error, problems = v.Problems.Count == 0 ? null : v.Problems,
                    note = v.File is null ? "No .oml/oml-config.json here or above: these are the defaults. oml config init writes the file."
                        : Path.GetFileName(v.File) == LegacyFileName ? $"{v.File} is the older name: oml config init renames it to {FileName}, and oml config set writes the new name." : null,
                });
                return v.Error is null ? 0 : 1;
            case ["show"]:
                var all = Load(start);
                TerminalOutput.PrintObject(new
                {
                    file = all.File ?? PathIn(start) + " (not there yet)", error = all.Error, problems = all.Problems.Count == 0 ? null : all.Problems,
                    settings = Settings.Select(s => new
                    {
                        key = s.Key, value = all.Get(s.Key), @default = all.Get(s.Key) == s.Default ? null : s.Default,
                        choices = s.Kind switch { "bool" => "on, off", "text" => null, "url" or "url-local" => "an address, or \"\" to clear it", _ => string.Join(", ", s.Choices!) + (s.Kind == "list" ? " (one or more, comma-separated)" : "") },
                        locked = s.Locked ? true : (bool?)null, about = s.About,
                    }),
                    next = "oml config set <key> <value> changes one; agents read the result with oml config.",
                });
                return all.Error is null ? 0 : 1;
            case ["example"]:
                Console.Write(Example());
                return 0;
            case ["init"]:
                var root = RootOf(start) ?? start;
                if (File.Exists(PathIn(root))) return Fail($"{PathIn(root)} exists.", "Change it with oml config set <key> <value>, or edit the file.");
                var old = Load(root);   // an older .oml/config.json: its values move to the new file
                if (old.Error is not null) return Fail(old.Error, "Fix or remove the file first. Nothing was changed.");
                Save(root, old);
                TerminalOutput.PrintObject(new { file = PathIn(root), next = "Edit the file or use oml config set <key> <value>. oml config show explains every setting." });
                return 0;
            case ["set", var key, .. var words] when words.Count > 0:
                if (Find(key) is not { } setting) return Fail($"Unknown setting '{key}'.", "oml config show lists the settings.");
                var target = RootOf(start) ?? start;
                var (bad, hint, saved, current) = Change(target, setting, string.Join(setting.Kind == "list" ? "," : " ", words));
                if (bad is not null) return Fail(bad, hint);
                TerminalOutput.PrintObject(new { file = PathIn(target), key = setting.Key, value = saved, profile = Profile(current!), note = "Agents see it in a new session (the session-start hook), or at once with oml config." });
                return 0;
            default:
                return Fail($"Unknown or incomplete config command. {Usage}");
        }
    }

    // One change, validated and saved: the path of `oml config set` and of the menu's settings editor, so both follow the
    // same rules. Problem is null when the file was written; Saved is then the values as written.
    internal static (string? Problem, string? Hint, string Value, Values? Saved) Change(string root, Setting setting, string text)
    {
        if (Normalize(setting, text, out var value) is { } problem) return ($"{setting.Key}: {problem}", null, value, null);
        if (setting.Locked && value != setting.Default) return ($"{setting.Key} cannot be turned off.", setting.About, value, null);
        var current = Load(root);
        if (current.Error is not null) return (current.Error, "Fix or remove the file first. Nothing was changed.", value, null);
        current.Put(setting.Key, value);
        Save(root, current);
        return (null, null, value, current);
    }

    static int Fail(string message, string? hint = null)
    {
        TerminalOutput.PrintObject(new { error = message, hint });
        return 2;
    }

    // Every setting with its default and a comment, as .oml/oml-config.json takes it (comments are allowed there): a
    // file to copy and change. docs/oml-config.example.jsonc is this text.
    internal static string Example()
    {
        var b = new System.Text.StringBuilder();
        b.Append("// .oml/oml-config.json: every setting with its default. Copy it, change what you need, delete the rest.\n");
        b.Append("// Comments are allowed. oml config show explains each setting; oml config prints what agents follow.\n");
        b.Append("{\n  \"version\": 1");
        string? group = null;
        string Value(Setting s) => s.Kind switch
        {
            "bool" => s.Default,
            "list" => "[" + string.Join(", ", s.Default.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => "\"" + x + "\"")) + "]",
            _ => System.Text.Json.JsonSerializer.Serialize(s.Default),
        };
        foreach (var s in Settings.Where(s => !s.Locked))
        {
            var dot = s.Key.IndexOf('.');
            var g = dot < 0 ? null : s.Key[..dot];
            var name = dot < 0 ? s.Key : s.Key[(dot + 1)..];
            if (g != group)
            {
                if (group is not null) b.Append("\n  }");
                if (g is not null) b.Append($",\n\n  \"{g}\": {{");
                group = g;
            }
            else b.Append(',');
            var indent = g is null ? "  " : "    ";
            if (g is null) b.Append(",\n");
            var choices = s.Kind switch { "choice" or "list" => " One" + (s.Kind == "list" ? " or more" : "") + " of: " + string.Join(", ", s.Choices!) + ".", _ => "" };
            b.Append($"\n{indent}// {s.About}{choices}\n{indent}\"{name}\": {Value(s)}");
        }
        if (group is not null) b.Append("\n  }");
        b.Append("\n}\n");
        return b.ToString();
    }

    // Writes every setting, nested by its first key part, so the file documents itself. Locked settings are left out.
    internal static void Save(string root, Values values)
    {
        var json = new JsonObject { ["version"] = 1 };
        foreach (var s in Settings.Where(s => !s.Locked))
        {
            var value = values.Get(s.Key);
            JsonNode node = s.Kind switch
            {
                "bool" => JsonValue.Create(value == "true"),
                "list" => new JsonArray(value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                _ => JsonValue.Create(value),
            };
            var dot = s.Key.IndexOf('.');
            if (dot < 0) { json[s.Key] = node; continue; }
            if (json[s.Key[..dot]] is not JsonObject group) json[s.Key[..dot]] = group = [];
            group[s.Key[(dot + 1)..]] = node;
        }
        Directory.CreateDirectory(Path.Combine(root, ".oml"));
        File.WriteAllText(PathIn(root), json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n") + "\n");
        // The older name is oml's own file: its values are in the new one now.
        if (File.Exists(LegacyPathIn(root))) File.Delete(LegacyPathIn(root));
    }
}
