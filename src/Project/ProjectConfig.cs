using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml config`: the project profile, `.oml/config.json` in the project root. A person turns parts of the work on or
// off there (phone and tablet, dark theme, previews, QA, questions, the browser, the hooks), and agents get the result
// as a few plain lines instead of deciding or asking each time:
//   oml config                    the profile for an agent: only the lines that differ from the default work
//   oml config show               every setting: value, default, choices and what it does
//   oml config set <key> <value>  change one setting (the file is made when it is missing)
//   oml config init               write the file with every default, to edit by hand
// The file holds only settings: no secrets, no paths outside the project. A missing file or key means the default.
// Locked settings (asking before install, publish, deploy and data deletion; the .oml write guard) are shown but
// cannot be turned off. oml hooks read the same file: the session-start hook gives the profile to the agent, the
// pre-tool hook follows the hooks.* and browser settings, the end-of-turn hook follows memory.reminder.
static class ProjectConfig
{
    const string Usage = "Usage: oml config [show | init | set <key> <value>] [--dir <project folder>]";
    internal const string FileName = "config.json";

    internal sealed record Setting(string Key, string Kind, string Default, string[]? Choices, string About, bool Locked = false);

    // Kind: bool, choice (one of Choices), list (comma-separated, each one of Choices) or text.
    internal static readonly Setting[] Settings =
    [
        new("ui.targets", "list", "desktop,tablet,phone", ["desktop", "tablet", "phone"], "Screen sizes the project supports. Agents design, preview and test only these."),
        new("ui.themes", "list", "light,dark", ["light", "dark"], "Themes the project supports. Agents write and check styles only for these."),
        new("ui.rtl", "bool", "false", null, "Right-to-left layouts are needed."),
        new("ui.accessibility", "bool", "true", null, "Check contrast, labels and keyboard use for new UI."),
        new("ui.translations", "bool", "false", null, "New texts need translations (the module is multilingual)."),
        new("checks.preview", "bool", "true", null, "Run oml preview for new or changed UI."),
        new("checks.livePreview", "bool", "true", null, "Run oml preview --live for a style change on a running page."),
        new("checks.qa", "bool", "true", null, "Run oml qa page after a publish to see the result in the app."),
        new("checks.review", "bool", "true", null, "Write the oml review file (.md) for a staged change."),
        new("checks.uiChecklist", "bool", "true", null, "Follow oml doc ui-checklist for UI work."),
        new("checks.bestPractices", "bool", "true", null, "Read oml doc best-practices before adding entities, screens or flows no recipe covers."),
        new("checks.testScenarios", "bool", "true", null, "Offer oml test scenarios after a change."),
        new("checks.testData", "bool", "true", null, "Use oml test data to prepare and clean up test records."),
        new("ask.testing", "bool", "true", null, "Ask whether to test or run QA. Off: decide from checks.*, never ask, and list what was not run."),
        new("ask.testScenarios", "bool", "true", null, "Ask whether the user wants test scenarios. Off: follow checks.testScenarios without asking."),
        new("ask.devUrl", "bool", "true", null, "Ask for the DEV URL of a screen. Off: use app.devUrl, or skip the live checks when it is empty."),
        new("ask.producerChoice", "bool", "true", null, "Ask which producer element to use even for one match. Off: one match is used without a question."),
        new("ask.install", "bool", "true", null, "Ask before oml install.", Locked: true),
        new("ask.publish", "bool", "true", null, "Ask before oml publish and reference refresh.", Locked: true),
        new("ask.deploy", "bool", "true", null, "Ask before a LifeTime tag or deployment.", Locked: true),
        new("ask.dataDeletion", "bool", "true", null, "Ask before anything that deletes data.", Locked: true),
        new("lane.fast", "bool", "true", null, "Small changes (only CSS values or texts of existing elements) skip preview, QA, the review file and questions."),
        new("browser", "choice", "oml", ["oml", "agent"], "Who opens the app to look at it: oml (oml qa, its own Edge or Chrome) or agent (the agent's built-in browser)."),
        new("app.devUrl", "text", "", null, "Base URL of the app in DEV, such as https://dev.example.com/Orders."),
        new("outsystems.mcpServer", "url-local", "", null, "Address of Service Studio's MCP server (O11), such as http://127.0.0.1:41820/mcp. oml publish, pull, references and studio use it; empty means the default port. Only this computer (127.0.0.1 or localhost)."),
        new("outsystems.odcMcpServer", "url", "", null, "Address of the OutSystems MCP server of your ODC tenant, such as https://mytenant.outsystems.dev/mcp. Agents use it for ODC apps; oml itself does not call it."),
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

    static Setting? Find(string key) => Settings.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    // The project root: the nearest folder, from `start` upwards, that has .oml/config.json; null when none has.
    internal static string? RootOf(string? start)
    {
        try
        {
            for (var dir = new DirectoryInfo(Path.GetFullPath(string.IsNullOrWhiteSpace(start) ? Environment.CurrentDirectory : start)); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, ".oml", FileName))) return dir.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { }
        return null;
    }

    internal static string PathIn(string root) => Path.Combine(root, ".oml", FileName);

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
        var file = PathIn(root);
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
    static string? Normalize(Setting s, string text, out string value, bool fromFile = false)
    {
        value = text.Trim();
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
                if (value.Length > 300 || value.Any(char.IsControl)) return "a text of at most 300 characters on one line.";
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
            lines.Add($"UI sizes: {Join(targets)} only. Do not design, preview, screenshot or test for {Join(allTargets.Except(targets))}, and do not add styles for them.");
        var themes = v.List("ui.themes");
        if (themes.Length == 1)
            lines.Add($"Theme: {themes[0]} only. Do not write, preview or check {(themes[0] == "light" ? "dark" : "light")} theme styles.");
        if (v.On("ui.rtl")) lines.Add("Right-to-left layouts are needed: check new UI in RTL.");
        if (!v.On("ui.accessibility")) lines.Add("Do not run accessibility checks unless the user asks.");
        if (v.On("ui.translations")) lines.Add("The module is multilingual: new texts need translations.");

        var skipped = new List<string>();
        if (!v.On("checks.preview")) skipped.Add("oml preview");
        if (!v.On("checks.livePreview")) skipped.Add("oml preview --live");
        if (!v.On("checks.qa")) skipped.Add("oml qa");
        if (!v.On("checks.review")) skipped.Add("the oml review file");
        if (!v.On("checks.uiChecklist")) skipped.Add("oml doc ui-checklist");
        if (!v.On("checks.bestPractices")) skipped.Add("oml doc best-practices");
        if (!v.On("checks.testScenarios")) skipped.Add("oml test scenarios");
        if (!v.On("checks.testData")) skipped.Add("oml test data");
        if (skipped.Count > 0) lines.Add($"Turned off for this project, do not run or offer unless the user asks in this task: {Join(skipped)}.");

        if (!v.On("ask.testing")) lines.Add("Do not ask whether to test, preview or run QA. Run what this profile leaves on, then end with one line: Not run: <what you skipped>.");
        if (!v.On("ask.testScenarios")) lines.Add("Do not ask about test scenarios.");
        var url = v.Get("app.devUrl");
        if (url.Length > 0) lines.Add($"DEV URL of the app: {url}");
        if (!v.On("ask.devUrl")) lines.Add(url.Length > 0 ? "Do not ask for the DEV URL; build screen URLs from the one above." : "Do not ask for a DEV URL; skip the checks that need a running page and say so.");
        if (!v.On("ask.producerChoice")) lines.Add("When exactly one producer element matches, use it without asking; say which one you used.");

        if (v.On("lane.fast"))
            lines.Add("Fast lane: when the task changes only CSS values or texts of existing elements, do it in 2 tool calls (read the sheet or widget; spec check and apply). Report the staged file and the changed lines. No preview, QA, review file, memory step or questions.");
        if (v.Get("browser") == "agent")
            lines.Add("Browser: look at the app with your own built-in browser tool. Never start Edge, Chrome or Firefox from a shell. Use oml qa only for saved test runs the user asks for.");
        if (v.Get("outsystems.mcpServer") is { Length: > 0 } studioMcp)
            lines.Add($"Service Studio MCP server (O11): {studioMcp}. oml pull, publish, references and studio already use it; call it yourself only for a step oml has no command for.");
        if (v.Get("outsystems.odcMcpServer") is { Length: > 0 } odcMcp)
            lines.Add($"OutSystems MCP server (ODC): {odcMcp}. Use it for ODC apps as skill/reference/odc.md says; ask before publish and deploy there too.");
        if (!v.On("memory.recall")) lines.Add("Do not run oml memory recall.");
        if (!v.On("memory.write")) lines.Add("Do not write project memory.");
        if (!v.On("docs.library")) lines.Add("Do not search the OutSystems docs library.");
        if (!v.On("map.use")) lines.Add("Do not use oml map.");
        lines.Add("Always ask before install, publish, deploy and anything that deletes data.");
        return lines;
    }

    // The text the session-start hook gives to an agent; null when the project has no config file or the profile is off.
    internal static string? SessionText(string? cwd)
    {
        var v = Load(cwd);
        if (v.File is null || v.Error is not null || !v.On("hooks.sessionProfile")) return null;
        return "oml project profile (.oml/config.json, set by the user; it overrides the defaults of the skill and AGENTS.md):\n- " + string.Join("\n- ", Profile(v));
    }

    // The Service Studio MCP address of the project, for the commands that call it (Locations.StudioUrl reads
    // OML_STUDIO_URL). A variable the person set wins; a broken config file changes nothing.
    internal static void ApplyEnvironment()
    {
        if (Environment.GetEnvironmentVariable("OML_STUDIO_URL") is { Length: > 0 } || Environment.GetEnvironmentVariable("OML_STUDIO_PORT") is { Length: > 0 }) return;
        if (Load(null).Get("outsystems.mcpServer") is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        Environment.SetEnvironmentVariable("OML_STUDIO_URL", url);
        Environment.SetEnvironmentVariable("OML_STUDIO_PORT", uri.Port.ToString()); // the token file is named after the port
    }

    static string SizeOf(int width) => width < 600 ? "phone" : width < 1024 ? "tablet" : "desktop";

    // preview and qa follow ui.targets and ui.themes: a width or theme the project does not support is refused with
    // the reason (--no-profile skips this once), and a preview with no --width or --theme gets the project's own.
    // Returns the arguments to run, or the refusal.
    internal static (string[] Args, string? Refusal) Adjust(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("preview" or "qa")) return (args, null);
        if (args.Contains("--no-profile")) return (args.Where(a => a != "--no-profile").ToArray(), null);
        if (args.Contains("--help") || args.Contains("-h")) return (args, null);
        var v = Load(null);
        if (v.File is null || v.Error is not null) return (args, null);
        string[] targets = v.List("ui.targets"), themes = v.List("ui.themes");
        string? Value(string option) => System.Array.IndexOf(args, option) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        if (Value("--width") is { } w && int.TryParse(w, out var width) && !targets.Contains(SizeOf(width)))
            return (args, $"--width {width} is a {SizeOf(width)} size, and this project supports only {string.Join(", ", targets)} (ui.targets in {v.File}). Leave it out, or add --no-profile when the user asked for this size in this task.");
        if (Value("--theme") is { } theme && theme is "light" or "dark" && !themes.Contains(theme))
            return (args, $"--theme {theme}: this project supports only the {string.Join(", ", themes)} theme (ui.themes in {v.File}). Leave it out, or add --no-profile when the user asked for this theme in this task.");
        // Defaults only for the offline preview of one element; the live preview and qa show the page as it is.
        if (args[0] != "preview" || args.Contains("--live") || args.Contains("--serve")) return (args, null);
        var added = new List<string>(args);
        if (Value("--width") is null && !targets.Contains("desktop")) added.AddRange(["--width", targets.Contains("tablet") ? "820" : "390"]);
        if (Value("--theme") is null && themes is ["dark"]) added.AddRange(["--theme", "dark"]);
        return (added.ToArray(), null);
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
                    note = v.File is null ? "No .oml/config.json here or above: these are the defaults. oml config init writes the file." : null,
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
            case ["init"]:
                var root = RootOf(start) ?? start;
                if (File.Exists(PathIn(root))) return Fail($"{PathIn(root)} exists.", "Change it with oml config set <key> <value>, or edit the file.");
                Save(root, new Values());
                TerminalOutput.PrintObject(new { file = PathIn(root), next = "Edit the file or use oml config set <key> <value>. oml config show explains every setting." });
                return 0;
            case ["set", var key, .. var words] when words.Count > 0:
                if (Find(key) is not { } setting) return Fail($"Unknown setting '{key}'.", "oml config show lists the settings.");
                if (Normalize(setting, string.Join(setting.Kind == "list" ? "," : " ", words), out var value) is { } problem) return Fail($"{setting.Key}: {problem}");
                if (setting.Locked && value != setting.Default) return Fail($"{setting.Key} cannot be turned off.", setting.About);
                var target = RootOf(start) ?? start;
                var current = Load(target);
                if (current.Error is not null) return Fail(current.Error, "Fix or remove the file first. Nothing was changed.");
                current.Put(setting.Key, value);
                Save(target, current);
                TerminalOutput.PrintObject(new { file = PathIn(target), key = setting.Key, value, profile = Profile(current), note = "Agents see it in a new session (the session-start hook), or at once with oml config." });
                return 0;
            default:
                return Fail($"Unknown or incomplete config command. {Usage}");
        }
    }

    static int Fail(string message, string? hint = null)
    {
        TerminalOutput.PrintObject(new { error = message, hint });
        return 2;
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
    }
}
