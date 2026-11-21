using System.Text;

namespace OmlCli;

// The menu as plain data and pure functions: groups, tasks, how a task turns answers into an argument array, the
// filter, the "does a bare oml open the menu" decision and the settings rows. No console here, so `oml menu-test`
// can check all of it without a terminal. Every command line is built from options that appear in CliUsage.Text.
internal enum AskKind { Text, Module, Folder, Font, Image }
internal enum BareChoice { Overview, Menu }

internal sealed record Ask(string Key, string Prompt, AskKind Kind, bool Optional = false);

// Build turns the answers (by Ask.Key) into the arguments after "oml"; Before runs first (hooks install shows its status).
// Needs is the usage prefix this build must have for the task to be offered ("migrate plan").
internal sealed record MenuTask(string Title, string About, Ask[] Asks, Func<IReadOnlyDictionary<string, string>, string[]> Build, string[]? Before = null, string? Needs = null);

// Special: a group that is a screen of its own ("settings", "commands") instead of a list of tasks.
internal sealed record MenuGroup(string Title, string About, MenuTask[] Tasks, string? Special = null);

internal static class MenuModel
{
    static Ask Module(string key = "file", string prompt = "Which module?") => new(key, prompt, AskKind.Module);
    static Ask Folder(string prompt, bool optional = false, string key = "folder") => new(key, prompt, AskKind.Folder, optional);
    static Ask Text(string key, string prompt, bool optional = false) => new(key, prompt, AskKind.Text, optional);
    static readonly Ask[] None = [];

    public static readonly MenuGroup[] Groups =
    [
        new("Start", "Set up, create a project, open the dashboard", [
            new("Check my setup", "Finds what is missing for oml to work and says how to fix it.", None, _ => ["doctor", "--text"]),
            new("Create a project", "A folder ready to work in: memory, project profile, skill, hooks and the OutSystems docs.",
                [Text("name", "Project name"), Folder("Folder of modules to bring in", optional: true, key: "in")],
                a => a["in"].Length > 0 ? ["create", a["name"], "--in", a["in"]] : ["create", a["name"]]),
            new("Open the dashboard", "The local web dashboard in your browser (Ctrl+C stops it).", None, _ => ["dashboard"]),
            new("Install agent hooks in this folder", "Guards for AI agents working here; shows what is installed first.", None, _ => ["hooks", "install"], Before: ["hooks", "status"]),
        ]),
        new("Project settings", "What agents do, skip and ask in this project (.oml/oml-config.json)", [], "settings"),
        new("Module", "Read, check and explain one .oml file", [
            new("What is in it", "Entities, actions, screens and more that the module owns.", [Module()], a => ["inspect", a["file"], "--own"]),
            new("Check it", "Rule findings for the module, with their severity.", [Module()], a => ["check", a["file"]]),
            new("Describe an element", "One action, screen or entity explained in a few readable lines.", [Module(), Text("element", "Name of the element")], a => ["describe", a["file"], a["element"]]),
            new("Export OpenAPI", "OpenAPI files for the REST APIs the module exposes, written to this folder.", [Module()], a => ["openapi", a["file"]]),
            new("Migrate to Reactive", "A plan for moving a module to Reactive.", [Module()], a => ["migrate", "plan", a["file"]], Needs: "migrate plan"),
            new("Review a change", "What changed between two versions of a module: risks and new findings.",
                [Module("before", "The file before the change"), Module("after", "The file after the change")], a => ["review", a["before"], a["after"]]),
        ]),
        new("Application", "Many modules at once: architecture, checks, the map, LifeTime", [
            new("Architecture report", "A page of the modules of a folder: who uses whom and the publish order.", [Folder("Folder with the .oml files")], a => ["architecture", "--in", a["folder"]]),
            new("Check a folder", "Rule findings for every module of a folder.", [Folder("Folder with the .oml files")], a => ["check", "--in", a["folder"]]),
            new("Build the application map", "Who calls what across the modules; later answers need no Service Studio.",
                [Text("project", "Project name"), Folder("Folder with the .oml files")], a => ["map", "build", a["project"], "--in", a["folder"]]),
            new("LifeTime: environments", "The environments of your LifeTime (needs OML_LIFETIME_HOST and a token).", None, _ => ["lifetime", "environments"]),
            new("LifeTime: applications", "The applications LifeTime knows.", None, _ => ["lifetime", "apps"]),
            new("LifeTime: download an application", "Save an application of an environment to a folder.",
                [Text("app", "Application name"), Text("env", "Environment name"), Folder("Folder to save it in", key: "out")], a => ["lifetime", "download", a["app"], "--env", a["env"], "--out", a["out"]]),
        ]),
        new("Agents", "What AI agents did and learned in this project", [
            new("What agents ran", "Per command: count, failures and median time, from the local usage log.", None, _ => ["log", "summary"]),
            new("What agents learned", "Where agents fail and retry, from the local log and memory.", None, _ => ["learn", "report"]),
            new("Project memory", "The facts, decisions and traps saved for the next agent.", None, _ => ["memory", "list"]),
            new("Refresh the skill in this project", "Update the project's skill folder and AGENTS.md to this oml.", None, _ => ["skill", "refresh"]),
        ]),
        new("Docs", "OutSystems rules and warnings", [
            new("Search the docs", "Ranked search of the rules, spec keys, recipes and commands.", [Text("words", "Search for")], a => ["doc", "--search", a["words"]]),
            new("Explain a warning", "Cause and fix of a validation message, by id or title words.", [Text("warning", "Warning id or words")], a => ["explain-warning", a["warning"]]),
        ]),
        new("Assets", "Fonts and images", [
            new("Convert a font to WOFF2", "A web font from a TTF or OTF file.", [new("file", "Which font?", AskKind.Font)], a => ["font", "woff2", a["file"]]),
            new("Optimize an image", "A smaller PNG, JPEG, WebP or SVG.", [new("file", "Which image?", AskKind.Image)], a => ["image", a["file"]]),
            new("Make an ICO", "A multi-size Windows icon or favicon from an image.", [new("file", "Which image?", AskKind.Image)], a => ["image", a["file"], "--to", "ico"]),
        ]),
        new("Every command", "All commands with their usage lines, for the raw thing", [], "commands"),
    ];

    // ---------------------------------------------------------------- tasks and search

    public static bool Offered(MenuTask t) => t.Needs is null || UsageLines().Any(l => l.StartsWith("oml " + t.Needs));
    public static IEnumerable<MenuTask> Tasks(MenuGroup g) => g.Tasks.Where(Offered);
    public static IEnumerable<(MenuGroup Group, MenuTask Task)> AllTasks() => Groups.SelectMany(g => Tasks(g).Select(t => (g, t)));

    // The words of a query are all found (any case) in the haystack.
    public static bool Match(string haystack, string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));

    // Tasks of every group whose title, description or group name has all the words: what typing in the main list finds.
    public static List<(MenuGroup Group, MenuTask Task)> Search(string query) =>
        AllTasks().Where(x => Match(x.Task.Title + " " + x.Task.About + " " + x.Group.Title, query)).ToList();

    // Answers for tests: every key gets a value of its kind.
    public static IReadOnlyDictionary<string, string> Sample(MenuTask t) => t.Asks.ToDictionary(a => a.Key, a => a.Kind == AskKind.Text ? "two words" : "some dir/file.x");

    // ---------------------------------------------------------------- command lines

    // "oml inspect Orders.oml --own": what a person could type. Quoted only where an argument needs it.
    public static string Line(IEnumerable<string> args) => "oml " + string.Join(' ', args.Select(Quote));
    static string Quote(string a) => a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c == '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    // What a person typed in "Every command" as arguments: words, with "double quoted" parts kept together; a leading oml is dropped.
    public static string[] Words(string line)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; any = true; }
            else if (char.IsWhiteSpace(c) && !quoted) { if (any || sb.Length > 0) list.Add(sb.ToString()); sb.Clear(); any = false; }
            else sb.Append(c);
        }
        if (any || sb.Length > 0) list.Add(sb.ToString());
        if (list.Count > 0 && list[0] == "oml") list.RemoveAt(0);
        return list.ToArray();
    }

    // Commands the menu does not run from a typed line: they change a module, an environment or data, and a person
    // should type them in a shell after reading what they do. Null when the line may run.
    public static string? Refusal(string[] a)
    {
        if (a.Length == 0) return "Nothing to run.";
        var refused = a[0] is "install" or "publish" or "references" or "apply" or "batch" or "menu" or "menu-test"
            || a[0] == "lifetime" && a.Length > 1 && a[1] is "tag" or "deploy" or "abort" or "continue"
            || a[0] == "test" && a.Length > 2 && a[1] == "data" && a[2] is "create" or "update" or "cleanup" or "allow";
        return refused ? $"The menu does not run oml {a[0]} like this: it changes a module, an environment or data. Type it in a shell." : null;
    }

    // ---------------------------------------------------------------- the usage text

    public static IEnumerable<string> UsageLines() => CliUsage.Text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("oml "));

    // Every command word of the usage text with its usage lines, in the order of the text.
    public static List<(string Name, string[] Lines)> Commands() =>
        UsageLines().Select(l => l[4..].Split(' ')[0]).Where(w => w.Length > 0 && char.IsLetter(w[0])).Distinct()
            .Select(n => (n, UsageLines().Where(l => l == "oml " + n || l.StartsWith("oml " + n + " ")).ToArray())).ToList();

    // ---------------------------------------------------------------- bare oml

    // Session variables of agents (the list the usage log reads) and of CI runners: a bare oml there prints the overview.
    static readonly string[] AgentVariables = [.. UsageLog.SessionVariables, "CLAUDECODE", "CI", "GITHUB_ACTIONS"];

    // The menu only for a person at a real terminal: stdin and stdout are not redirected and nothing says agent, CI or "no menu".
    public static BareChoice Bare(bool inputRedirected, bool outputRedirected, Func<string, string?> env)
    {
        if (inputRedirected || outputRedirected) return BareChoice.Overview;
        if (string.Equals(env("OML_OUTPUT")?.Trim(), "json", StringComparison.OrdinalIgnoreCase)) return BareChoice.Overview;
        if (env("OML_MENU")?.Trim().ToLowerInvariant() is "0" or "off" or "false" or "no") return BareChoice.Overview;
        return AgentVariables.Any(v => env(v) is { Length: > 0 }) ? BareChoice.Overview : BareChoice.Menu;
    }

    // ---------------------------------------------------------------- candidates for the file picker

    // Files with these extensions in the folder and in modules/ and staged/, newest first; paths relative to the folder.
    public static List<(string Path, DateTime When)> Files(string cwd, string[] exts) =>
        Bases(cwd).SelectMany(d => Safe(() => Directory.EnumerateFiles(d)))
            .Where(f => exts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => (Path.GetRelativePath(cwd, f), Safe(() => File.GetLastWriteTime(f), DateTime.MinValue)))
            .OrderByDescending(x => x.Item2).ThenBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();

    // Folders of the folder and of modules/ and staged/, newest first, without hidden ones and build output.
    public static List<(string Path, DateTime When)> Folders(string cwd) =>
        Bases(cwd).SelectMany(d => Safe(() => Directory.EnumerateDirectories(d)))
            .Where(f => Path.GetFileName(f) is { } n && !n.StartsWith('.') && n is not ("bin" or "obj" or "node_modules"))
            .Select(f => (Path.GetRelativePath(cwd, f), Safe(() => Directory.GetLastWriteTime(f), DateTime.MinValue)))
            .OrderByDescending(x => x.Item2).ThenBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();

    static IEnumerable<string> Bases(string cwd) => new[] { cwd }.Concat(new[] { "modules", "staged" }.Select(d => Path.Combine(cwd, d)).Where(Directory.Exists));

    // A folder that cannot be read lists nothing: never an exception on a person's screen.
    static IEnumerable<string> Safe(Func<IEnumerable<string>> list)
    {
        try { return list().ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static T Safe<T>(Func<T> get, T fallback)
    {
        try { return get(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return fallback; }
    }

    // ---------------------------------------------------------------- settings rows

    public static string Show(ProjectConfig.Setting s, string value) => s.Kind == "bool" ? (value == "true" ? "on" : "off") : value.Length == 0 ? "(empty)" : value;

    // The value after one toggle: a bool flips, a choice goes to the next choice; null when the setting needs input (list, text, address).
    public static string? Next(ProjectConfig.Setting s, string current) => s.Kind switch
    {
        "bool" => current == "true" ? "false" : "true",
        "choice" => s.Choices![(Math.Max(0, System.Array.IndexOf(s.Choices, current)) + 1) % s.Choices.Length],
        _ => null,
    };
}
