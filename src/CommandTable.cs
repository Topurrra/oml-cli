using System.Text.Json.Nodes;

namespace OmlCli;

// One entry per command the launcher knows about, so a new command is added in one place: how it runs, when the
// launcher takes it, whether --timings is refused and whether the launcher prints its --help. Anything not here (and
// any entry whose condition says no) goes to Core. `oml commands` prints this table; `--check` guards it against CliUsage.Text.
// Run takes args (what Core would get: --pretty and --json kept) and filtered (without them); null means Core runs it.
// Mcp: the tool of `oml mcp` that runs the command ("read", "write", "gated", or "read write" for both); null: no tool
// runs it. McpTools adds the rules per subcommand and option. Hidden: no usage line in CliUsage.Text: the maintainer commands
// and the old names of merged commands (CliUsage.Merged: the new form, such as `doc warning`, runs the old entry).
internal sealed record Cmd(string Name, Func<string[], string[], int>? Run, Func<string[], bool>? When = null, bool NoTimings = false, bool Help = false, bool Hidden = false, string? Mcp = null, bool Warm = false);

internal static class CommandTable
{
    static string[] Rest(string[] filtered, int skip = 1) => filtered.Skip(skip).ToArray();

    public static readonly Cmd[] All =
    [
        new("hooks", (_, f) => AgentHooks.Run(Rest(f)), NoTimings: true, Help: true),
        new("lifetime", (_, f) => LifeTime.Run(Rest(f), Program.RunCore), NoTimings: true, Help: true, Mcp: "gated"),
        new("server", (_, f) => ServerApi.Run(Rest(f)), NoTimings: true, Help: true, Mcp: "gated"),
        new("font", (_, f) => FontAssets.Run(Rest(f)), NoTimings: true, Help: true),
        new("doctor", (_, f) => Doctor.Run(Rest(f)), NoTimings: true, Help: true, Mcp: "read"),
        new("spec", (_, f) => SpecCommand.Run(Rest(f)), NoTimings: true, Mcp: "read write", Warm: true),
        new("doc", (_, f) => DocCommand.Run(Rest(f)), NoTimings: true, Mcp: "read"),
        new("explain-warning", (_, f) => DocCommand.Warn(Rest(f)), NoTimings: true, Hidden: true, Mcp: "read"),   // doc warning <id>
        new("memory", (_, f) => MemoryCommand.Run(Rest(f)), NoTimings: true, Mcp: "read"),
        new("learn", (_, f) => EvolveCommand.Run(Rest(f)), NoTimings: true),
        // oml map: build loads modules (Core); the queries read the stored map here, without Service Studio.
        new("map", (_, f) => MapCommand.Run(Rest(f)), f => !(f.Length > 1 && f[1] == "build"), Mcp: "read"),
        new("mock", (_, f) => MockCommand.Run(Rest(f)), Hidden: true),   // map mock
        new("create", (_, f) => CreateCommand.Run(Rest(f))),
        new("skill", (_, f) => CreateCommand.Skill(Rest(f))),
        new("migrate", (_, f) => Migrate.Run(Rest(f)), NoTimings: true, Help: true, Mcp: "write"),
        new("config", (_, f) => ProjectConfig.Run(Rest(f)), NoTimings: true),
        // oml code: the code-mode runtime in codemode/ (Python); the arguments go to it as typed, --json included.
        new("code", (a, _) => CodeMode.Run(CodeMode.AfterCommand(a)), NoTimings: true),
        // oml test data: the module spec needs the producer module (Core); the rest calls the test-data API here.
        new("test", (_, f) => TestDataCommand.Run(Rest(f, 2)), f => f.Length > 1 && f[1] == "data" && !(f.Length > 2 && f[2] == "module"), Mcp: "read", Warm: true),
        new("outsystems-docs", (_, f) => Library.Run(Rest(f)), NoTimings: true, Help: true, Hidden: true),   // doc library
        new("team-rules", (_, f) => CompanySkills.Run(Rest(f)), NoTimings: true, Mcp: "read"),
        new("dashboard", (_, f) => Dashboard.Run(Rest(f)), NoTimings: true, Help: true),
        new("browser", (_, f) => Qa.QaCommand.Run(Rest(f)), NoTimings: true, Help: true, Mcp: "write"),
        new("show-change", (_, f) => Dashboard.Dif(Rest(f)), NoTimings: true, Help: true, Hidden: true),   // dashboard --change
        new("explorer-menu", (_, f) => Setup.Run(Rest(f)), _ => OperatingSystem.IsWindows(), NoTimings: true, Help: true, Hidden: true),   // menu explorer
        new("architecture", TeamRun, f => f.Contains("--team"), Mcp: "read", Warm: true),
        new("check", TeamRun, f => f.Contains("--team"), Mcp: "read", Warm: true),
        // oml review --server: pulls two versions from the platform server (a login), then Core reviews them.
        new("review", (_, f) => ServerApi.Review(Rest(f)), f => f.Contains("--server"), Mcp: "read", Warm: true),
        // The launcher only prints image's help and refuses --timings; Core converts the image.
        new("image", null, NoTimings: true, Help: true),
        // Taken in Main, before the usage log wraps the call (a summary of the log is not itself logged); --help goes to Core.
        new("log", (_, f) => UsageLog.Summary(Rest(f)), f => !f.Contains("--help") && !f.Contains("-h"), NoTimings: true, Hidden: true),   // learn usage
        // Taken in Main (a person's terminal; the usage log does not copy it); --help goes to the launcher's help.
        new("menu", (_, f) => MenuApp.Run(Rest(f)), f => !f.Contains("--help") && !f.Contains("-h"), NoTimings: true, Help: true),
        // `oml mcp`: a server on stdin and stdout. Taken in Main, before the usage log wraps it (each call is logged on its own).
        new("mcp", (_, f) => McpServer.Run(Rest(f)), f => !f.Contains("--help") && !f.Contains("-h"), NoTimings: true, Help: true),
        // Commands Core runs, listed here only for the tool of `oml mcp` that runs them.
        // Warm: the command may run in a warm worker (WarmPool); Warms has the exceptions per subcommand and option.
        new("version", null, Mcp: "read"), new("inspect", null, Mcp: "read", Warm: true), new("describe", null, Mcp: "read", Warm: true), new("find", null, Mcp: "read", Warm: true),
        new("tree", null, Mcp: "read", Warm: true), new("flow", null, Mcp: "read", Warm: true), new("stats", null, Hidden: true, Mcp: "read", Warm: true), new("header", null, Hidden: true, Mcp: "read", Warm: true),
        new("expression", null, Hidden: true, Mcp: "read"), new("openapi", null, Mcp: "read", Warm: true), new("export", null, Mcp: "read", Warm: true), new("studio", null, Mcp: "read"),
        new("app", null, Mcp: "read"), new("query", null, Mcp: "read", Warm: true), new("validate", null, Mcp: "read", Warm: true), new("batch", null, Mcp: "read write", Warm: true),
        new("apply", null, Mcp: "write", Warm: true), new("new", null, Mcp: "write", Warm: true),
        new("install", null, Mcp: "gated"), new("publish", null, Mcp: "gated"), new("references", null, Mcp: "gated"), new("pull", null, Mcp: "gated"),
        // Hidden maintainer commands: not in the usage text, not in help.
        new("terminal-test", (_, _) => { var result = TerminalRender.SelfTest(); Console.WriteLine(result); return result == "ok" ? 0 : 1; }, Hidden: true),
        new("commands", (_, f) => Commands(f), Hidden: true, Mcp: "read"),
        new("menu-test", (_, _) => { var result = MenuTest.Run(); Console.WriteLine(result); return result == "ok" ? 0 : 1; }, Hidden: true),
        // oml warm status | stop: the warm workers (WarmPool). Hidden from the overview; commands.md documents it.
        new("warm", (_, f) => WarmPool.Command(Rest(f)), NoTimings: true, Hidden: true),
    ];

    // Whether a warm worker may run this command line (normalized, command first): the table's Warm field, except the
    // forms that are interactive, reach a server or Service Studio, read stdin, or only print help or usage. Only
    // commands that load modules, read files and write new files go warm; everything else always runs in its own process.
    public static bool Warms(string[] f)
    {
        if (f.Length < 2 || f.Contains("--help") || f.Contains("-h") || !All.Any(c => c.Name == f[0] && c.Warm)) return false;
        return f[0] switch
        {
            "spec" => f[1] == "check",                        // the other forms need no Service Studio
            "test" => f[1] is "bdd" or "scenarios",           // test data calls a server
            "query" => !f.Contains("--live"),                 // --live asks Service Studio
            "check" or "architecture" => !f.Contains("--team"),   // --team downloads from LifeTime
            "review" => !f.Contains("--server"),              // --server signs in to the platform server
            "batch" => !f.Skip(1).Contains("-"),              // steps from stdin
            _ => true,
        };
    }

    // The entry the launcher runs for these arguments, or null when Core does.
    public static Cmd? Find(string[] filtered) =>
        filtered.FirstOrDefault() is { } name ? All.FirstOrDefault(c => c.Name == name && c.Run is not null && (c.When?.Invoke(filtered) ?? true)) : null;

    public static bool PrintsHelp(string name) => All.Any(c => c.Name == name && c.Help);

    // The tools of `oml mcp` that may run this command line (normalized, command first); empty when no tool does. The
    // table's Mcp field, except where a subcommand or option writes, or reaches a server or LifeTime. `batch` is read
    // or write by its steps; McpServer checks them with this same function.
    public static string[] McpTools(string[] a)
    {
        var (cmd, sub) = (a.FirstOrDefault() ?? "", a.Length > 1 ? a[1] : "");
        if (cmd == "review" && a.Contains("--server")) return ["gated"];   // signs in to the platform server
        if (cmd is "check" or "architecture" && a.Contains("--team")) return ["gated"];   // downloads from LifeTime
        if (cmd == "memory" && sub is "add" or "retract" or "dream" or "init") return ["write"];
        if (cmd == "map" && sub == "build") return ["write"];   // writes the stored map
        if (cmd == "studio" && sub == "open") return ["gated"];   // opens a module in Service Studio
        if (cmd == "apply" && a.Contains("--user-approved")) return ["gated"];   // saves deletes and data-risk changes: a person says yes
        if (cmd == "team-rules" && sub is "add" or "remove") return ["write"];   // installs or removes a company skill
        if (cmd == "test" && sub == "data" && a.Length > 2 && a[2] is "allow" or "create" or "update" or "cleanup") return ["gated"];   // changes data on a server
        return All.FirstOrDefault(c => c.Name == cmd)?.Mcp?.Split(' ') ?? [];
    }

    // Every command a tool of `oml mcp` runs, for the tool's description.
    // A hidden command (an old name) still runs, but is not named.
    public static string[] McpCommands(string tool) => All.Where(c => !c.Hidden && c.Mcp?.Split(' ').Contains(tool) == true).Select(c => c.Name).ToArray();

    // `check --team` and `architecture --team`: refuse bad input before the team's applications are downloaded.
    static int TeamRun(string[] args, string[] filtered)
    {
        if (filtered[0] == "check" && Program.CheckPreflight(filtered) is { } error)
        {
            TerminalOutput.PrintObject(new { error });
            return 2;
        }
        if (filtered[0] == "architecture" && filtered.Contains("--publish-order"))
        {
            // Core refuses this too, but only after the team's applications are downloaded; a package has no .oml file to publish anyway.
            TerminalOutput.PrintObject(new { error = "--publish-order does not take --team: export the .oml files into a folder and use --in <folder>." });
            return 2;
        }
        return LifeTime.Architecture(args, Program.RunCore);
    }

    // Every command of the usage text once, in its order: the first word of each "oml <command> ..." line.
    static string[] UsageCommands() => CliUsage.Text.Split('\n').Select(l => l.Trim())
        .Where(l => l.StartsWith("oml ")).Select(l => l[4..].Split(' ')[0]).Where(w => w.Length > 0 && char.IsLetter(w[0])).Distinct().ToArray();

    // The overview names the areas of the usage text in its order, each with exactly the command words of its lines; a
    // command word is in one area only; each merged command's new form has a usage line.
    static IEnumerable<string> OverviewProblems()
    {
        // An area line: "Read a module: inspect, describe, ... (what they do)".
        var lines = CliUsage.Overview.Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^[A-Z][A-Za-z ]*: [a-z][a-z, -]*( \(|$)")).ToList();
        var named = lines.Select(l => l[..l.IndexOf(": ")]).ToList();
        if (!named.SequenceEqual(CliUsage.Areas.Select(a => a.Area)))
            yield return $"The overview's areas ({string.Join(", ", named)}) are not the areas of the usage text in order ({string.Join(", ", CliUsage.Areas.Select(a => a.Area))}).";
        foreach (var (area, commands) in CliUsage.Areas)
        {
            var line = lines.FirstOrDefault(l => l.StartsWith(area + ": "));
            if (line is null) continue;
            var words = line[(area.Length + 2)..].Split(" (")[0].Split(", ");
            if (!words.Order().SequenceEqual(commands.Order())) yield return $"The overview lists {string.Join(", ", words)} under {area}; the usage text has {string.Join(", ", commands)}.";
        }
        foreach (var dup in CliUsage.Areas.SelectMany(a => a.Commands).GroupBy(c => c).Where(g => g.Count() > 1)) yield return $"{dup.Key} is in two areas of the usage text.";
        foreach (var (form, _) in CliUsage.Merged)
            if (!CliUsage.Text.Split('\n').Any(l => CliUsage.FormOf(l.TrimEnd('\r')) == form)) yield return $"The new form {form} has no usage line.";
    }

    // oml commands [--check]: the table as JSON (the source for a later MCP tool list); --check compares it with the usage text.
    static int Commands(string[] filtered)
    {
        var usage = UsageCommands();
        var list = new JsonArray();
        foreach (var name in usage)
        {
            var cmd = All.FirstOrDefault(c => c.Name == name);
            var runsIn = cmd is null || cmd.Run is null ? "core" : cmd.When is null ? "launcher" : "both";
            list.Add(new JsonObject
            {
                ["name"] = name,
                ["runsIn"] = runsIn,
                ["timings"] = cmd is null || !cmd.NoTimings,
                ["mcp"] = cmd?.Mcp,
                ["usage"] = new JsonArray(CliUsage.For(name).Split('\n').Select(l => (JsonNode?)l.TrimEnd('\r')).ToArray()),
            });
        }
        if (filtered.Contains("--check"))
        {
            var problems = All.Where(c => !c.Hidden && !usage.Contains(c.Name)).Select(c => $"{c.Name} is in the command table but has no usage line in CliUsage.Text.")
                .Concat(usage.Where(u => !list.Any(n => n!["name"]!.GetValue<string>() == u)).Select(u => $"{u} has a usage line but is missing from the output."))
                .Concat(All.GroupBy(c => c.Name).Where(g => g.Count() > 1).Select(g => $"{g.Key} is in the command table twice."))
                .Concat(OverviewProblems())
                .ToList();
            TerminalOutput.PrintObject(new { ok = problems.Count == 0, commands = list.Count, problems });
            return problems.Count == 0 ? 0 : 1;
        }
        TerminalOutput.PrintNode(new JsonObject { ["commands"] = list });
        return 0;
    }
}
