using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// `oml create <project | path> [--dir <parent>] [--in <folder|file.oap|file.oml>]... [--author "<name>"] [--server <host>]
// [--hooks claude|copilot|codex|gemini|all] [--no-skill] [--no-library] [--no-doctor]`; `oml create` alone, run by a person in
// a terminal, asks for each of these (Ask). A path (C:\work\orders) is the parent folder and the name; missing
// folders are created. a project that is ready to work in, from one command. The output's `ready` list has one row per
// step (done, skipped or failed, with the command that repeats it): folders, memory (as oml memory init), the project
// profile (.oml/oml-config.json, see ProjectConfig), the skill, the OutSystems docs for oml doc --search
// --library (downloaded once per machine; skipped when they are there), the modules and the map, and a setup check
// (oml doctor). The starting point of a project. A new folder with a place for every kind of work (modules, staged copies, specs, browser scripts, mocks,
// scenarios), the project memory, the agent's instructions (the skill, AGENTS.md, CLAUDE.md), a
// .gitignore and a PROJECT.md with the next steps. With --in it copies the project's modules in and builds the
// application map under the project's name, so `oml map ...` works there at once. Nothing outside the new folder changes,
// except the map store (%LOCALAPPDATA%\oml-cli\maps\<project>).
static class CreateCommand
{
    static readonly Regex ProjectName = new("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);
    static readonly string[] Folders = ["modules", "staged", "specs", "browser", "mocks", "scenarios"];

    static int Fail(string message, string? hint = null, int code = 2)
    {
        TerminalOutput.PrintObject(new { error = message, hint });
        return code;
    }

    public static int Run(string[] args)
    {
        // No name: a person is asked for one and for the rest; options typed after oml create still apply.
        if ((args.Length == 0 || args[0].StartsWith("--") && args[0] is not ("--help" or "-h")) && Person())
        {
            if (Ask() is not { } asked) return Fail("Nothing was created.");
            args = [.. asked, .. args];
        }
        if (args.Length == 0 || args[0] is "--help" or "-h") { Console.WriteLine(CliUsage.For("create")); return args.Length == 0 ? 2 : 0; }
        var name = args[0];
        string? parent = null, author = null, server = null, hooks = null;
        // A path: its last part is the name, the rest is the parent folder.
        if (name.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            var full = Path.GetFullPath(name).TrimEnd('/', '\\');
            parent = Path.GetDirectoryName(full);
            name = Path.GetFileName(full);
        }
        if (!ProjectName.IsMatch(name))
            return Fail($"Project name '{name}': use 1-40 lower-case letters, digits and hyphens, such as falcon.",
                ProjectName.IsMatch(name.ToLowerInvariant()) ? $"Use {name.ToLowerInvariant()}." : null);
        var inputs = new List<string>();
        bool skill = true, library = true, doctor = true;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dir" when i + 1 < args.Length: parent = args[++i]; break;
                case "--in" when i + 1 < args.Length: inputs.Add(Path.GetFullPath(args[++i])); break;
                case "--author" when i + 1 < args.Length: author = args[++i]; break;
                case "--server" when i + 1 < args.Length: server = args[++i]; break;
                case "--hooks" when i + 1 < args.Length: hooks = args[++i].ToLowerInvariant(); break;
                case "--no-skill": skill = false; break;
                case "--no-hooks": break; // hooks are opt-in now: oml hooks install
                case "--no-library": library = false; break;
                case "--no-doctor": doctor = false; break;
                default: return Fail($"Unknown or incomplete option {args[i]}.", "Usage: " + CliUsage.For("create").Trim());
            }
        }
        // The name oml writes as creator and last modifier in this project (`author` in .oml/oml-config.json).
        if (author is not null && ProjectConfig.Normalize(ProjectConfig.Find("author")!, author, out author) is { } badAuthor)
            return Fail($"--author: {badAuthor}");
        if (hooks is not null && hooks is not ("claude" or "copilot" or "codex" or "gemini" or "all" or "none"))
            return Fail($"--hooks {hooks}: use claude, copilot, codex, gemini, all or none.");
        if (server is not null && ProjectConfig.Normalize(ProjectConfig.Find("outsystems.serverHost")!, server, out server) is { } badServer)
            return Fail($"--server: {badServer}");
        foreach (var input in inputs)
            if (!Directory.Exists(input) && !(File.Exists(input) && Path.GetExtension(input).ToLowerInvariant() is ".oap" or ".oml"))
                return Fail($"--in {input}: not a folder, .oap or .oml file.");
        var root = Path.GetFullPath(Path.Combine(parent ?? Environment.CurrentDirectory, name));
        if (File.Exists(root) || Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            return Fail($"{root} exists and is not empty.", "Choose another name or --dir; oml create never writes into a folder that has files.");

        var created = new List<string>();
        var notes = new List<string>();
        var ready = new List<object>();
        void Step(string step, string status, string? detail = null, string? fix = null) => ready.Add(new { step, status, detail, fix });
        Directory.CreateDirectory(root);
        foreach (var f in Folders) { Directory.CreateDirectory(Path.Combine(root, f)); created.Add(f + "/"); }

        // Memory: the store of this project (the folder holds no module yet, so the wrong-folder check of init does not apply).
        MemStore.Create(root);
        created.Add(".oml/memory/memory.log");
        Directory.CreateDirectory(Path.Combine(root, ".oml"));
        File.WriteAllText(Path.Combine(root, ".oml", "map.json"), new JsonObject { ["project"] = name }.ToJsonString() + "\n");
        created.Add(".oml/map.json");
        Step("folders", "done");
        Step("memory", "done", "oml memory recall and add work here");
        // The project profile with every default, for the person to edit (oml config set, or the file).
        var profile = new ProjectConfig.Values();
        if (author is { Length: > 0 }) profile.Put("author", author);
        if (server is { Length: > 0 }) profile.Put("outsystems.serverHost", server);
        ProjectConfig.Save(root, profile);
        created.Add(".oml/oml-config.json");
        // The app's test accounts per role, for the person to fill in; agents never read it, and it stays out of git.
        File.WriteAllText(Path.Combine(root, ".oml", Qa.TestAccounts.FileName), Qa.TestAccounts.Template());
        created.Add(".oml/" + Qa.TestAccounts.FileName);
        Step("config", "done", (author is { Length: > 0 } ? $"author {author}, the rest " : "") + "defaults; oml config show explains every setting");

        // The agent's instructions: a copy of the skill that came with this oml, AGENTS.md for any agent, CLAUDE.md for Claude Code.
        if (skill)
        {
            if (SkillFolder() is { } source)
            {
                Copy(source, Path.Combine(root, "skill"));
                File.WriteAllText(Path.Combine(root, "skill", VersionFile), OmlVersion + "\n");
                File.Copy(Path.Combine(source, "AGENTS.md"), Path.Combine(root, "AGENTS.md"));
                File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "@AGENTS.md\n");
                created.AddRange(["skill/", "AGENTS.md", "CLAUDE.md"]);
                Step("skill", "done");
            }
            else
            {
                notes.Add("The skill folder was not found next to oml, so no skill/, AGENTS.md or CLAUDE.md was written. Copy skill/ from the oml release.");
                Step("skill", "failed", "skill/ was not found next to oml", "copy skill/ from the oml release, then: oml skill refresh --dir " + root);
            }
        }
        else Step("skill", "skipped", "--no-skill");

        // The OutSystems docs for oml doc --search --library: one copy per machine, so a second project downloads nothing.
        if (!library) Step("library", "skipped", "--no-library", "oml outsystems-docs fetch");
        else if (Library.Folders().Count == Library.Sources.Length) Step("library", "done", "already on this machine: " + Library.Root);
        else
        {
            var (code, output) = Captured(() => Library.Run(["fetch"]));
            if (code == 0) Step("library", "done", "downloaded to " + Library.Root);
            else
            {
                Step("library", "failed", output?["error"]?.ToString() ?? "the download did not finish (no network, or a proxy)", "oml outsystems-docs fetch");
                notes.Add("The OutSystems docs were not downloaded; platform questions (oml doc --search ... --library) need them. Run: oml outsystems-docs fetch");
            }
        }

        File.WriteAllText(Path.Combine(root, ".gitignore"), string.Join("\n",
            "# oml: staged copies and outputs are rebuilt, never committed",
            "staged/", "*.staged.oml", "browser/out/", "*.blank.oml",
            "# oml: caches and local state (memory.log itself is kept)",
            ".oml/memory/memory.idx", ".oml/memory/.lock", ".oml/memory/memory.log.torn-*", ".oml/testdata/", ".oml/evolve/*/", ".oml/codemode/", ".oml/code/", ".oml/test-accounts.local.json",
            ".claude/settings.local.json", ".github/hooks/oml.json", ".codex/hooks.json", ".gemini/settings.json", "") );
        created.Add(".gitignore");

        // Modules and the application map.
        object? map = null;
        if (inputs.Count > 0)
        {
            var modules = Path.Combine(root, "modules");
            var copied = 0;
            foreach (var input in inputs)
                foreach (var file in Directory.Exists(input) ? Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".oap" or ".oml") : [input])
                {
                    var target = Path.Combine(modules, Path.GetFileName(file));
                    if (File.Exists(target)) { notes.Add($"Two inputs are named {Path.GetFileName(file)}; kept the first."); continue; }
                    File.Copy(file, target);
                    copied++;
                }
            created.Add($"modules/ ({copied} file(s))");
            var previous = Environment.CurrentDirectory;
            Environment.CurrentDirectory = root; // map build remembers the project in the current folder's .oml/map.json
            try
            {
                var (code, text) = Program.RunCoreCaptured(["map", "build", name, "--in", modules]);
                JsonNode? output = null;
                try { output = JsonNode.Parse(text); } catch (JsonException) { }
                map = output ?? (object)new { error = text.Trim().Length > 0 ? text.Trim() : "map build printed nothing" };
                if (code != 0) notes.Add($"The map was not built (exit {code}); run in the project folder: oml map build {name} --in modules");
                Step("map", code == 0 ? "done" : "failed", $"{copied} file(s) in modules/", code == 0 ? null : $"cd {root}; oml map build {name} --in modules");
            }
            finally { Environment.CurrentDirectory = previous; }
        }
        else Step("map", "skipped", "no --in: modules/ is empty", $"put .oap or .oml files in modules/, then: oml map build {name} --in modules");

        // Agent hooks, only for the agent the person named.
        if (hooks is not null and not "none")
        {
            var (code, output) = Captured(() => AgentHooks.Run(["install", "--dir", root, "--agent", hooks]));
            var files = (output?["agents"] as JsonArray)?.Select(a => Path.GetRelativePath(root, a?["file"]?.ToString() ?? "").Replace('\\', '/')).ToList() ?? [];
            created.AddRange(files);
            Step("hooks", code == 0 ? "done" : "failed", code == 0 ? string.Join(", ", files) : output?["error"]?.ToString(), code == 0 ? null : $"oml hooks install --dir {root} --agent {hooks}");
        }

        // The setup check: what on this machine still stops the work (Service Studio, its MCP server, LifeTime variables).
        if (doctor)
        {
            var (_, report) = Captured(() => Doctor.Run([]));
            var open = (report?["checks"] as JsonArray)?.Where(c => c?["status"]?.ToString() is "fail" or "warn")
                .Select(c => $"{c!["name"]}: {c["detail"]}" + (c["fix"] is { } fix ? " Fix: " + fix : "")).ToList();
            if (report is null) Step("doctor", "failed", "oml doctor printed no report", "oml doctor");
            else if (open is { Count: > 0 }) { Step("doctor", "attention", $"{open.Count} item(s), see notes", "oml doctor"); notes.AddRange(open); }
            else Step("doctor", "done", "the setup is complete");
        }
        else Step("doctor", "skipped", "--no-doctor", "oml doctor");

        File.WriteAllText(Path.Combine(root, "PROJECT.md"), ProjectReadme(name, inputs.Count > 0));
        created.Add("PROJECT.md");
        TerminalOutput.PrintObject(new
        {
            project = name, folder = root, ready, created, map, notes = notes.Count == 0 ? null : notes,
            optional = new[]
            {
                author is { Length: > 0 } ? null : "oml config set author \"<name>\"   (the name Service Studio shows as creator and last modifier of what oml creates)",
                "oml config set ui.targets desktop   (and the other settings: what agents do, skip and ask; oml config show)",
                "oml team-rules add <file.md>   (your team's own rules)",
                hooks is null or "none" ? "oml hooks install --agent claude|copilot|codex|gemini   (optional agent hooks: guards, a memory reminder, the profile at session start)" : null,
                server is { Length: > 0 } ? null : "oml config set outsystems.serverHost <host>   (the platform server: list and download modules with Service Studio closed)",
                "fill .oml/test-accounts.local.json   (a test account of your app per role: browser scripts log in with {\"login\": \"<role>\"})",
                "oml browser start --headed   (log in to the app once; later browser runs keep the login)",
                "oml explorer-menu   (right-click entries in Windows Explorer, once per machine)",
            }.OfType<string>().ToArray(),
            next = inputs.Count > 0
                ? new[] { $"cd {root}", "oml map overview", "Start your agent in this folder and give it a task." }
                : new[] { $"cd {root}", "Put the project's .oap or .oml files in modules/ (oml lifetime download can fetch them).", $"oml map build {name} --in modules", "Start your agent in this folder and give it a task." },
        });
        return 0;
    }

    // A person at a terminal, not an agent or a script: the same test as the bare `oml` menu.
    // OML_ASK=1 asks also with redirected input (the tests answer through a pipe).
    static bool Person() => Prompt.Person();

    // `oml create` alone, for a person: one question per setting, with the value Enter keeps in brackets. Returns the
    // command line it stands for (printed, so the person learns it), or null when the person stopped.
    static string[]? Ask()
    {
        string C(string text, string color) => TerminalColors.Paint(text, color);
        string? Line(string question, string keep)
        {
            Console.Write(C("? ", TerminalColors.Cyan) + TerminalColors.BoldText(question) + (keep.Length > 0 ? C($" [{keep}]", TerminalColors.Dim) : "") + " ");
            var line = Console.ReadLine();
            return line is null ? null : line.Trim().Length == 0 ? keep : line.Trim().Trim('"');
        }
        Console.WriteLine(C("oml create", TerminalColors.Bold + TerminalColors.Cyan) + ": a new project folder for your modules and your agent. Enter keeps the value in [brackets]; Ctrl+C stops.");
        string? name;
        while (true)
        {
            name = Line("Project name (lower-case letters, digits and hyphens, such as orders)", "");
            if (name is null) return null;
            if (ProjectName.IsMatch(name)) break;
            if (ProjectName.IsMatch(name.ToLowerInvariant())) { name = name.ToLowerInvariant(); break; }
            Console.WriteLine(C("  Use 1-40 lower-case letters, digits and hyphens.", TerminalColors.Yellow));
        }
        var parent = Line("Folder to create it in (made if missing)", Environment.CurrentDirectory);
        if (parent is null) return null;
        string? input;
        while (true)
        {
            input = Line("Modules to copy in: a folder, .oap or .oml (Enter: none yet)", "");
            if (input is null) return null;
            if (input.Length == 0 || Directory.Exists(input) || File.Exists(input) && Path.GetExtension(input).ToLowerInvariant() is ".oap" or ".oml") break;
            Console.WriteLine(C("  Not a folder, .oap or .oml file.", TerminalColors.Yellow));
        }
        var author = Line("Your name, shown in Service Studio as creator of what oml makes (Enter: later)", Environment.GetEnvironmentVariable("OML_AUTHOR") ?? "");
        if (author is null) return null;
        var server = Line("Platform server of DEV, such as dev.example.com (Enter: none)", Environment.GetEnvironmentVariable("OML_SERVER_HOST") ?? "");
        if (server is null) return null;
        Console.WriteLine("  Agents: 1 Claude Code   2 GitHub Copilot (VS Code, app, CLI)   3 Codex   4 Gemini CLI   5 Cursor   6 all of them   0 none");
        string? hooks;
        while (true)
        {
            var pick = Line("Which agent do you use? oml adds its safety hooks for it", "0");
            if (pick is null) return null;
            hooks = pick switch { "1" or "5" => "claude", "2" => "copilot", "3" => "codex", "4" => "gemini", "6" => "all", "0" => "none", _ => null };
            if (hooks is not null) break;
            Console.WriteLine(C("  Type a number from 0 to 6.", TerminalColors.Yellow));
        }
        var args = new List<string> { name!, "--dir", parent };
        if (input.Length > 0) args.AddRange(["--in", input]);
        if (author.Length > 0) args.AddRange(["--author", author]);
        if (server.Length > 0) args.AddRange(["--server", server]);
        if (hooks != "none") args.AddRange(["--hooks", hooks]);
        string Quote(string a) => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a;
        Console.WriteLine(C("  The same as: ", TerminalColors.Dim) + "oml create " + string.Join(" ", args.Select(Quote)));
        var go = Line($"Create {Path.Combine(Path.GetFullPath(parent), name!)}? (y/n)", "y");
        return go is "y" or "Y" or "yes" ? args.ToArray() : null;
    }

    // The oml version a project's skill/ copy came from; doctor compares it with the running oml.
    internal const string VersionFile = ".oml-version";
    internal static string OmlVersion => typeof(CreateCommand).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // A project's skill/ copy is older than this oml: (folder, its version or null when unknown). Null when the folder
    // has no skill/ copy, or it is current.
    internal static (string folder, string? version)? StaleSkill(string folder)
    {
        var skill = Path.Combine(folder, "skill");
        if (!File.Exists(Path.Combine(skill, "SKILL.md")) || SkillFolder() is not { } source
            || string.Equals(Path.GetFullPath(skill), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) return null;
        var stamp = Path.Combine(skill, VersionFile);
        var version = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : null;
        return version is not null && Version.TryParse(version, out var v) && Version.TryParse(OmlVersion, out var now) && v >= now ? null : (skill, version);
    }

    // `oml skill refresh [--dir <project>]`: the project's skill/ and AGENTS.md from this oml. Files of this oml's skill
    // replace their copies; files only the project has (its own notes) stay. CLAUDE.md and the memory are not touched.
    public static int Skill(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" || args[0] != "refresh") { Console.WriteLine(CliUsage.For("skill")); return args.Length > 0 && args[0] is "--help" or "-h" ? 0 : 2; }
        var dir = Path.GetFullPath(args.Length > 2 && args[1] == "--dir" ? args[2] : Environment.CurrentDirectory);
        var target = Path.Combine(dir, "skill");
        if (!File.Exists(Path.Combine(target, "SKILL.md")))
            return Fail($"{dir} has no skill/ copy (skill/SKILL.md).", "Run it in a project made by oml create, or pass --dir <project>.");
        if (SkillFolder() is not { } source) return Fail("The skill folder was not found next to oml.", "Use an oml release, which has skill/ beside bin/.");
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            return Fail("That skill/ is oml's own copy, not a project's.");
        var stamp = Path.Combine(target, VersionFile);
        var before = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : null;
        int updated = 0, added = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(source, file));
            var exists = File.Exists(copy);
            if (exists && File.ReadAllBytes(copy).AsSpan().SequenceEqual(File.ReadAllBytes(file))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, overwrite: true);
            if (exists) updated++; else added++;
        }
        var agents = Path.Combine(dir, "AGENTS.md");
        var agentsUpdated = File.Exists(Path.Combine(source, "AGENTS.md")) && (!File.Exists(agents) || File.ReadAllText(agents) != File.ReadAllText(Path.Combine(source, "AGENTS.md")));
        if (agentsUpdated) File.Copy(Path.Combine(source, "AGENTS.md"), agents, overwrite: true);
        File.WriteAllText(stamp, OmlVersion + "\n");
        TerminalOutput.PrintObject(new { skill = target, from = before ?? "unknown", to = OmlVersion, updated, added, agentsMd = agentsUpdated ? "updated" : "unchanged",
            note = "Files only this project has in skill/ were kept. Start a new agent session so it reads the new rules." });
        return 0;
    }

    // The skill next to this oml: <release>/skill beside bin/, or the repository's skill/ for a build from source.
    static string? SkillFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "skill");
            if (File.Exists(Path.Combine(candidate, "SKILL.md")) && File.Exists(Path.Combine(candidate, "AGENTS.md"))) return candidate;
        }
        return null;
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    static (int Code, JsonNode? Output) Captured(Func<int> run)
    {
        var (previous, previousRun) = (Console.Out, TerminalOutput.InRun);
        var sw = new StringWriter();
        Console.SetOut(sw);
        TerminalOutput.InRun = true;
        int code;
        try { code = run(); }
        finally { Console.SetOut(previous); TerminalOutput.InRun = previousRun; }
        JsonNode? json = null;
        try { json = JsonNode.Parse(sw.ToString()); } catch (JsonException) { }
        return (code, json);
    }

    static string ProjectReadme(string name, bool mapped) => $"""
        # {name}

        An OutSystems 11 project worked on with oml and AI agents. Start the agent in this folder; it reads AGENTS.md
        (CLAUDE.md for Claude Code), which points to skill/. See INSTRUCTION.md in the oml repository for the whole picture.

        | Folder | For |
        |---|---|
        | `modules/` | The project's `.oap` and `.oml` files, as downloaded (`oml lifetime download`) or saved (`oml pull`). Never edited. |
        | `staged/` | The agent's staged copies (`oml apply` writes here), installed only after your yes. With `autoApprove.install` on, agents change the module itself instead, after a backup next to it (`<file>.<time>.bak`). |
        | `specs/` | Change specs (JSON). |
        | `browser/` | Browser scripts and saved replays (`oml browser run <file> --save <replay>`). |
        | `mocks/` | Mock responses for your mock server (`oml mock --all --out mocks`). |
        | `scenarios/` | What to test after a change (`oml test scenarios <source> <staged> --out scenarios/<task>`). |
        | `.oml/` | Project memory (`oml memory`), the project configuration (`oml-config.json`), the map's project name, test-data runs. |

        ## Project profile

        `.oml/oml-config.json` says what agents do, skip and ask in this project: screen sizes, themes, browser checks,
        questions, which steps need your yes (`autoApprove.*`), which oml commands may run (`commands.*`), apply's
        flow layout, migrate's choices, the browser. `oml config show` explains every setting, and `oml config set <key> <value>`
        changes one, for example `oml config set ui.targets desktop` or `oml config set autoApprove.install on`.
        Agents read it when they run oml. With the optional hooks (`oml hooks install`) they also get it at the start of a session.

        ## Next steps

        {(mapped ? $"The application map is built: try `oml map overview`. After new downloads, run `oml map build {name}` again." : $"1. Put the project's modules in `modules/`.\n2. `oml map build {name} --in modules`")}
        - Ask the agent for a task in plain words. It asks before install, publish or deploy.
        - `oml memory list` shows what agents remembered; `oml learn report` shows where they lose time.
        """.Replace("\r\n", "\n") + "\n";
}
