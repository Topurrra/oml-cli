using System.Text.Json.Nodes;

namespace OmlCli;

// One entry per command the launcher knows about, so a new command is added in one place: how it runs, when the
// launcher takes it, whether --timings is refused and whether the launcher prints its --help. Anything not here (and
// any entry whose condition says no) goes to Core. `oml commands` prints this table; `--check` guards it against CliUsage.Text.
// Run takes args (what Core would get: --pretty and --json kept) and filtered (without them); null means Core runs it.
internal sealed record Cmd(string Name, Func<string[], string[], int>? Run, Func<string[], bool>? When = null, bool NoTimings = false, bool Help = false, bool Hidden = false);

internal static class CommandTable
{
    static string[] Rest(string[] filtered, int skip = 1) => filtered.Skip(skip).ToArray();

    public static readonly Cmd[] All =
    [
        new("hooks", (_, f) => AgentHooks.Run(Rest(f)), NoTimings: true, Help: true),
        new("lifetime", (_, f) => LifeTime.Run(Rest(f), Program.RunCore), NoTimings: true, Help: true),
        new("font", (_, f) => FontAssets.Run(Rest(f)), NoTimings: true, Help: true),
        new("doctor", (_, f) => Doctor.Run(Rest(f)), NoTimings: true, Help: true),
        new("spec", (_, f) => SpecCommand.Run(Rest(f)), NoTimings: true),
        new("doc", (_, f) => DocCommand.Run(Rest(f)), NoTimings: true),
        new("explain-warning", (_, f) => DocCommand.Warn(Rest(f)), NoTimings: true),
        new("memory", (_, f) => MemoryCommand.Run(Rest(f)), NoTimings: true),
        new("learn", (_, f) => EvolveCommand.Run(Rest(f)), NoTimings: true),
        // oml map: build loads modules (Core); the queries read the stored map here, without Service Studio.
        new("map", (_, f) => MapCommand.Run(Rest(f)), f => !(f.Length > 1 && f[1] == "build")),
        new("mock", (_, f) => MockCommand.Run(Rest(f))),
        new("create", (_, f) => CreateCommand.Run(Rest(f))),
        new("skill", (_, f) => CreateCommand.Skill(Rest(f))),
        new("migrate", (_, f) => Migrate.Run(Rest(f)), NoTimings: true, Help: true),
        new("config", (_, f) => ProjectConfig.Run(Rest(f)), NoTimings: true),
        // oml test data: the module spec needs the producer module (Core); the rest calls the test-data API here.
        new("test", (_, f) => TestDataCommand.Run(Rest(f, 2)), f => f.Length > 1 && f[1] == "data" && !(f.Length > 2 && f[2] == "module")),
        new("outsystems-docs", (_, f) => Library.Run(Rest(f)), NoTimings: true, Help: true),
        new("team-rules", (_, f) => CompanySkills.Run(Rest(f)), NoTimings: true),
        new("dashboard", (_, f) => Dashboard.Run(Rest(f)), NoTimings: true, Help: true),
        new("qa", (_, f) => Qa.QaCommand.Run(Rest(f)), NoTimings: true, Help: true),
        new("show-change", (_, f) => Dashboard.Dif(Rest(f)), NoTimings: true, Help: true),
        new("explorer-menu", (_, f) => Setup.Run(Rest(f)), _ => OperatingSystem.IsWindows(), NoTimings: true, Help: true),
        new("architecture", TeamRun, f => f.Contains("--team")),
        new("check", TeamRun, f => f.Contains("--team")),
        // The launcher only prints image's help and refuses --timings; Core converts the image.
        new("image", null, NoTimings: true, Help: true),
        // Taken in Main, before the usage log wraps the call (a summary of the log is not itself logged); --help goes to Core.
        new("log", (_, f) => UsageLog.Summary(Rest(f)), f => !f.Contains("--help") && !f.Contains("-h"), NoTimings: true),
        // Taken in Main (a person's terminal; the usage log does not copy it); --help goes to the launcher's help.
        new("menu", (_, f) => MenuApp.Run(Rest(f)), f => !f.Contains("--help") && !f.Contains("-h"), NoTimings: true, Help: true),
        // Hidden maintainer commands: not in the usage text, not in help.
        new("terminal-test", (_, _) => { var result = TerminalRender.SelfTest(); Console.WriteLine(result); return result == "ok" ? 0 : 1; }, Hidden: true),
        new("commands", (_, f) => Commands(f), Hidden: true),
        new("menu-test", (_, _) => { var result = MenuTest.Run(); Console.WriteLine(result); return result == "ok" ? 0 : 1; }, Hidden: true),
    ];

    // The entry the launcher runs for these arguments, or null when Core does.
    public static Cmd? Find(string[] filtered) =>
        filtered.FirstOrDefault() is { } name ? All.FirstOrDefault(c => c.Name == name && c.Run is not null && (c.When?.Invoke(filtered) ?? true)) : null;

    public static bool PrintsHelp(string name) => All.Any(c => c.Name == name && c.Help);

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
                ["usage"] = new JsonArray(CliUsage.For(name).Split('\n').Select(l => (JsonNode?)l.TrimEnd('\r')).ToArray()),
            });
        }
        if (filtered.Contains("--check"))
        {
            var problems = All.Where(c => !c.Hidden && !usage.Contains(c.Name)).Select(c => $"{c.Name} is in the command table but has no usage line in CliUsage.Text.")
                .Concat(usage.Where(u => !list.Any(n => n!["name"]!.GetValue<string>() == u)).Select(u => $"{u} has a usage line but is missing from the output."))
                .Concat(All.GroupBy(c => c.Name).Where(g => g.Count() > 1).Select(g => $"{g.Key} is in the command table twice."))
                .ToList();
            TerminalOutput.PrintObject(new { ok = problems.Count == 0, commands = list.Count, problems });
            return problems.Count == 0 ? 0 : 1;
        }
        TerminalOutput.PrintNode(new JsonObject { ["commands"] = list });
        return 0;
    }
}
