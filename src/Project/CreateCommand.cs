using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// `oml create <project> [--dir <parent>] [--in <folder|file.oap|file.oml>]... [--no-skill] [--no-hooks]`: the starting
// point of a project. A new folder with a place for every kind of work (modules, staged copies, specs, QA files, mocks,
// scenarios), the project memory, the agent's instructions (the skill, AGENTS.md, CLAUDE.md), the agent hooks (Claude Code, Copilot, Cursor, Codex, Gemini CLI), a
// .gitignore and a PROJECT.md with the next steps. With --in it copies the project's modules in and builds the
// application map under the project's name, so `oml map ...` works there at once. Nothing outside the new folder changes,
// except the map store (%LOCALAPPDATA%\oml-cli\maps\<project>).
static class CreateCommand
{
    static readonly Regex ProjectName = new("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);
    static readonly string[] Folders = ["modules", "staged", "specs", "qa", "mocks", "scenarios"];

    static int Fail(string message, string? hint = null, int code = 2)
    {
        TerminalOutput.PrintObject(new { error = message, hint });
        return code;
    }

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h") { Console.WriteLine(CliUsage.For("create")); return args.Length == 0 ? 2 : 0; }
        var name = args[0];
        if (!ProjectName.IsMatch(name)) return Fail($"Project name '{name}': use 1-40 lower-case letters, digits and hyphens, such as falcon.");
        string? parent = null;
        var inputs = new List<string>();
        bool skill = true, hooks = true;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dir" when i + 1 < args.Length: parent = args[++i]; break;
                case "--in" when i + 1 < args.Length: inputs.Add(Path.GetFullPath(args[++i])); break;
                case "--no-skill": skill = false; break;
                case "--no-hooks": hooks = false; break;
                default: return Fail($"Unknown or incomplete option {args[i]}.", "Usage: " + CliUsage.For("create").Trim());
            }
        }
        foreach (var input in inputs)
            if (!Directory.Exists(input) && !(File.Exists(input) && Path.GetExtension(input).ToLowerInvariant() is ".oap" or ".oml"))
                return Fail($"--in {input}: not a folder, .oap or .oml file.");
        var root = Path.GetFullPath(Path.Combine(parent ?? Environment.CurrentDirectory, name));
        if (File.Exists(root) || Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            return Fail($"{root} exists and is not empty.", "Choose another name or --dir; oml create never writes into a folder that has files.");

        var created = new List<string>();
        var notes = new List<string>();
        Directory.CreateDirectory(root);
        foreach (var f in Folders) { Directory.CreateDirectory(Path.Combine(root, f)); created.Add(f + "/"); }

        // Memory: the store of this project (the folder holds no module yet, so the wrong-folder check of init does not apply).
        MemStore.Create(root);
        created.Add(".oml/memory/memory.log");
        Directory.CreateDirectory(Path.Combine(root, ".oml"));
        File.WriteAllText(Path.Combine(root, ".oml", "map.json"), new JsonObject { ["project"] = name }.ToJsonString() + "\n");
        created.Add(".oml/map.json");

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
            }
            else notes.Add("The skill folder was not found next to oml, so no skill/, AGENTS.md or CLAUDE.md was written. Copy skill/ from the oml release.");
        }
        if (hooks)
        {
            var (code, _) = Captured(() => AgentHooks.Run(["install", "--dir", root]));
            if (code == 0) created.AddRange([".claude/settings.local.json (Claude Code, Copilot, Cursor)", ".codex/hooks.json", ".gemini/settings.json"]);
            else notes.Add("Agent hooks were not installed; run: oml hooks install --dir " + root);
        }

        File.WriteAllText(Path.Combine(root, ".gitignore"), string.Join("\n",
            "# oml: staged copies and outputs are rebuilt, never committed",
            "staged/", "*.staged.oml", "qa/out/", "*.blank.oml",
            "# oml: caches and local state (memory.log itself is kept)",
            ".oml/memory/memory.idx", ".oml/memory/.lock", ".oml/memory/memory.log.torn-*", ".oml/testdata/", ".oml/evolve/*/",
            ".claude/settings.local.json", ".codex/hooks.json", ".gemini/settings.json", "") );
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
            }
            finally { Environment.CurrentDirectory = previous; }
        }

        File.WriteAllText(Path.Combine(root, "PROJECT.md"), ProjectReadme(name, inputs.Count > 0));
        created.Add("PROJECT.md");
        TerminalOutput.PrintObject(new
        {
            project = name, folder = root, created, map, notes = notes.Count == 0 ? null : notes,
            next = inputs.Count > 0
                ? new[] { $"cd {root}", "oml map overview", "Start your agent in this folder and give it a task." }
                : new[] { $"cd {root}", "Put the project's .oap or .oml files in modules/ (oml lifetime download can fetch them).", $"oml map build {name} --in modules", "Start your agent in this folder and give it a task." },
        });
        return 0;
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
        | `staged/` | The agent's staged copies (`oml apply` writes here). Installed only after your yes. |
        | `specs/` | Change specs (JSON). |
        | `qa/` | QA files and saved replays (`oml qa run <file> --save <replay>`). |
        | `mocks/` | Mock responses for your mock server (`oml mock --all --out mocks`). |
        | `scenarios/` | What to test after a change (`oml test scenarios <source> <staged> --out scenarios/<task>`). |
        | `.oml/` | Project memory (`oml memory`), the map's project name, test-data runs. |

        ## Next steps

        {(mapped ? $"The application map is built: try `oml map overview`. After new downloads, run `oml map build {name}` again." : $"1. Put the project's modules in `modules/`.\n2. `oml map build {name} --in modules`")}
        - Ask the agent for a task in plain words. It asks before install, publish or deploy.
        - `oml memory list` shows what agents remembered; `oml learn report` shows where they lose time.
        """.Replace("\r\n", "\n") + "\n";
}
