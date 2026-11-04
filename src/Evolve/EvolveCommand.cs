using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml evolve`: oml learns from what happened, and a person decides. `report` reads local evidence and changes nothing;
// `propose` writes proposals (files under .oml/evolve and draft skills under .oml/skills), never a change; only
// `accept <id>` applies one (installs the skill, writes the memory records, or applies the patch), and `undo <id>`
// takes it back. `reject <id>` is remembered, so the same lines are not proposed again. No network, no model: the
// proposals are built from templates and the evidence they cite. Runs in the launcher, without Service Studio.
static class EvolveCommand
{
    public static readonly string[] ReadSubcommands = ["report", "show", "history"];

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h" or "help" || args.Contains("--help") || args.Contains("-h"))
            {
                (args.Length == 0 ? Console.Error : Console.Out).WriteLine(CliUsage.For("evolve"));
                return args.Length == 0 ? 2 : 0;
            }
            var o = Opts.Parse(args[1..]);
            return args[0] switch
            {
                "report" => Report(o),
                "propose" => Propose(o),
                "show" => Show(o),
                "accept" => Accept(o),
                "reject" => Reject(o),
                "history" => History(o),
                "undo" => Undo(o),
                _ => throw new EvolveException($"Unknown evolve command '{args[0]}'.", 2, "Run 'oml evolve --help'."),
            };
        }
        catch (EvolveException e)
        {
            TerminalOutput.PrintObject(new { error = e.Message, hint = e.Hint });
            return e.ExitCode;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TerminalOutput.PrintObject(new { error = "Cannot read or write the evolve files: " + e.Message });
            return 1;
        }
    }

    sealed class Opts
    {
        public List<string> Words = [];
        public List<string> Files = [], Checks = [];
        public Dictionary<string, string> Values = new(StringComparer.Ordinal);
        public bool Maintainer;
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public string Folder => Path.GetFullPath(Get("--dir") ?? ".");

        public static Opts Parse(string[] args)
        {
            var o = new Opts();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--maintainer") { o.Maintainer = true; continue; }
                if (a is "--file" or "--check" or "--review" or "--dir" or "--since" or "--md" or "--repo" or "--why")
                {
                    if (i + 1 >= args.Length) throw new EvolveException($"{a} needs a value.", 2, "Run 'oml evolve --help'.");
                    var v = args[++i];
                    if (a == "--file") o.Files.Add(Path.GetFullPath(v));
                    else if (a is "--check" or "--review") o.Checks.Add(Path.GetFullPath(v));
                    else o.Values[a] = v;
                    continue;
                }
                if (a.StartsWith("--", StringComparison.Ordinal)) throw new EvolveException($"Unknown option {a}.", 2, "Run 'oml evolve --help'.");
                o.Words.Add(a);
            }
            return o;
        }
    }

    // ---- state: .oml/evolve/state.json in the project folder ----

    static string EvolveDir(string folder) => Path.Combine(folder, ".oml", "evolve");
    static string StatePath(string folder) => Path.Combine(EvolveDir(folder), "state.json");

    static JsonObject LoadState(string folder)
    {
        var path = StatePath(folder);
        JsonObject state;
        try { state = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch (JsonException) { throw new EvolveException($"{path} is not valid JSON. It was changed outside oml; restore it, or move it away to start again.", 1); }
        state["proposals"] ??= new JsonObject();
        state["rejected"] ??= new JsonObject();
        state["accepted"] ??= new JsonArray();
        return state;
    }

    static void SaveState(string folder, JsonObject state)
    {
        Directory.CreateDirectory(EvolveDir(folder));
        var path = StatePath(folder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temp, path, overwrite: true);
    }

    static JsonObject Proposals(JsonObject state) => (JsonObject)state["proposals"]!;
    static JsonArray Accepted(JsonObject state) => (JsonArray)state["accepted"]!;

    static JsonObject Find(JsonObject state, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new EvolveException("Give one proposal id, such as e3fa2c1.", 2, "List them with: oml evolve show");
        return Proposals(state)[id] as JsonObject ?? throw new EvolveException($"No proposal {id}.", 1, "List them with: oml evolve show");
    }

    static string Now() => Evidence.Now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // ---- report ----

    static List<string> LogFiles(Opts o)
    {
        foreach (var f in o.Files) if (!File.Exists(f)) throw new EvolveException($"No usage log at {f}.", 2);
        if (o.Files.Count > 0) return o.Files;
        return Evidence.DefaultFile() is { } file ? [file]
            : throw new EvolveException("No usage log yet. oml adds one line per command (unless OML_LOG=0); use it for a while.", 1, "Or give a log file with --file <usage.jsonl>.");
    }

    static DateTime? Since(Opts o) => o.Get("--since") is not { } s ? null
        : DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d
        : throw new EvolveException("--since takes a date such as 2026-10-01.", 2);

    static (EvolveAnalysis Analysis, JsonObject Report) Analyze(Opts o, JsonObject state)
    {
        foreach (var c in o.Checks) if (!File.Exists(c)) throw new EvolveException($"Report not found: {c}", 2);
        var a = EvolveAnalysis.Build(LogFiles(o), Since(o), o.Folder, o.Checks);
        return (a, a.Report(a.Effects(Accepted(state))));
    }

    static int Report(Opts o)
    {
        if (o.Words.Count > 0) throw new EvolveException($"Unexpected '{o.Words[0]}'.", 2, "Usage: oml evolve report [--since <date>] [--file <usage.jsonl>]");
        var (_, report) = Analyze(o, LoadState(o.Folder));
        if (o.Get("--md") is { } md)
        {
            File.WriteAllText(Path.GetFullPath(md), EvolveAnalysis.Markdown(report));
            report["md"] = Path.GetFullPath(md);
        }
        TerminalOutput.PrintNode(report);
        return 0;
    }

    // ---- propose ----

    static HashSet<string> Closed(JsonObject state)
    {
        var closed = new HashSet<string>(((JsonObject)state["rejected"]!).Select(kv => kv.Key), StringComparer.Ordinal);
        foreach (var a in Accepted(state).OfType<JsonObject>().Where(a => a["undoneAt"] is null))
            foreach (var key in a["keys"] as JsonArray ?? []) closed.Add(key!.GetValue<string>());
        return closed;
    }

    static int Propose(Opts o)
    {
        if (o.Words.Count > 0) throw new EvolveException($"Unexpected '{o.Words[0]}'.", 2, "Usage: oml evolve propose [--maintainer] [--since <date>] [--file <usage.jsonl>]...");
        var folder = o.Folder;
        var state = LoadState(folder);
        var (analysis, report) = Analyze(o, state);
        var stamp = Evidence.Now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var runDir = Path.Combine(EvolveDir(folder), stamp);
        for (var n = 2; Directory.Exists(runDir); n++) runDir = Path.Combine(EvolveDir(folder), stamp + "-" + n);
        var repo = o.Maintainer ? Path.GetFullPath(o.Get("--repo") ?? ".") : null;
        var made = EvolveProposals.Make(analysis, folder, o.Maintainer, repo, Closed(state), runDir);

        Directory.CreateDirectory(runDir);
        foreach (var (path, content) in made.Files)
        {
            if (EvolveProposals.IsRules(path)) throw new InvalidOperationException("never write rules.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        // Proposals of earlier runs that this run did not make again are superseded: the evidence changed.
        var all = Proposals(state);
        var ids = made.Proposals.OfType<JsonObject>().Select(p => p["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, node) in all.ToList())
            if (node is JsonObject p && p["status"]?.GetValue<string>() == "open" && !ids.Contains(id) && p["level"]?.GetValue<string>() == (o.Maintainer ? "product" : "project"))
                p["status"] = "superseded";
        foreach (var p in made.Proposals.OfType<JsonObject>())
        {
            var id = p["id"]!.GetValue<string>();
            p["created"] = Now();
            p["run"] = runDir;
            all[id] = p.DeepClone();
        }
        SaveState(folder, state);
        File.WriteAllText(Path.Combine(runDir, "report.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.WriteAllText(Path.Combine(runDir, "proposals.json"), new JsonObject { ["proposals"] = made.Proposals.DeepClone(), ["flags"] = made.Flags.DeepClone() }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.WriteAllText(Path.Combine(runDir, "report.md"), EvolveAnalysis.Markdown(report, made.Proposals, made.Flags));
        TerminalOutput.PrintNode(new JsonObject
        {
            ["run"] = runDir,
            ["proposals"] = new JsonArray(made.Proposals.OfType<JsonObject>().Select(Summary).ToArray()),
            ["flags"] = made.Flags.DeepClone(),
            ["md"] = Path.Combine(runDir, "report.md"),
            ["hint"] = made.Proposals.Count == 0 ? "Nothing to propose from this evidence." : "Proposals only. A person reads each one (oml evolve show <id>) and runs oml evolve accept <id> or reject <id>. An agent never accepts on the user's behalf.",
        });
        return 0;
    }

    static JsonNode Summary(JsonObject p) => new JsonObject
    {
        ["id"] = p["id"]!.DeepClone(), ["kind"] = p["kind"]!.DeepClone(), ["title"] = p["title"]!.DeepClone(), ["status"] = p["status"]!.DeepClone(),
        ["lines"] = (p["lines"] as JsonArray)?.Count ?? 0, ["target"] = p["target"]!.DeepClone(), ["accept"] = p["accept"]!.DeepClone(),
    };

    // ---- show ----

    static int Show(Opts o)
    {
        var state = LoadState(o.Folder);
        if (o.Words.Count == 1)
        {
            TerminalOutput.PrintNode(Find(state, o.Words[0]).DeepClone());
            return 0;
        }
        if (o.Words.Count > 1) throw new EvolveException("Give at most one proposal id.", 2, "Usage: oml evolve show [<id>]");
        var open = Proposals(state).Select(kv => kv.Value as JsonObject).Where(p => p?["status"]?.GetValue<string>() == "open").ToList();
        TerminalOutput.PrintNode(new JsonObject
        {
            ["folder"] = EvolveDir(o.Folder),
            // Full proposals, so the dashboard can show the evidence and the diff without another call.
            ["proposals"] = new JsonArray(open.Select(p => (JsonNode)p!.DeepClone()).ToArray()),
            ["hint"] = open.Count == 0 ? "No open proposals. Run 'oml evolve propose'." : "A person accepts: oml evolve accept <id>, or rejects: oml evolve reject <id> --why \"<reason>\".",
        });
        return 0;
    }

    // ---- accept, reject, undo, history ----

    static int Reject(Opts o)
    {
        if (o.Words.Count != 1) throw new EvolveException("Give one proposal id.", 2, "Usage: oml evolve reject <id> [--why <text>]");
        var folder = o.Folder;
        var state = LoadState(folder);
        var p = Find(state, o.Words[0]);
        if (p["status"]?.GetValue<string>() is "accepted") throw new EvolveException($"Proposal {o.Words[0]} is accepted. Undo it first: oml evolve undo {o.Words[0]}", 1);
        var why = MemoryFormat.Sanitize(o.Get("--why") ?? "");
        if (MemorySecrets.Find(why) is { } secret) throw new EvolveException($"--why holds what looks like a {secret}. Nothing was written.", 2);
        var rejected = (JsonObject)state["rejected"]!;
        foreach (var key in (p["lines"] as JsonArray ?? []).Select(l => l!["key"]!.GetValue<string>()))
            rejected[key] = new JsonObject { ["id"] = o.Words[0], ["why"] = why.Length == 0 ? null : why, ["at"] = Now() };
        p["status"] = "rejected";
        p["rejectedAt"] = Now();
        if (why.Length > 0) p["why"] = why;
        SaveState(folder, state);
        TerminalOutput.PrintObject(new { rejected = o.Words[0], lines = (p["lines"] as JsonArray)?.Count ?? 0, hint = "These lines are not proposed again." });
        return 0;
    }

    static int Accept(Opts o)
    {
        if (o.Words.Count != 1) throw new EvolveException("Give one proposal id.", 2, "Usage: oml evolve accept <id>");
        var folder = o.Folder;
        var id = o.Words[0];
        var state = LoadState(folder);
        var p = Find(state, id);
        var status = p["status"]?.GetValue<string>();
        if (status != "open") throw new EvolveException($"Proposal {id} is {status}; only an open proposal can be accepted.", 1, "Run 'oml evolve propose' for current proposals.");
        var apply = (JsonObject)p["apply"]!;
        var undo = new JsonObject();
        object result;
        switch (p["kind"]!.GetValue<string>())
        {
            case "memory":
            {
                var ids = new JsonArray();
                foreach (var r in (JsonArray)apply["records"]!)
                {
                    var args = new List<string> { "add", r!["text"]!.GetValue<string>(), "--kind", r["kind"]!.GetValue<string>(), "--why", r["why"]!.GetValue<string>(), "--tags", "evolve", "--src", "evolve", "--dir", folder };
                    if (r["module"] is JsonValue m) args.AddRange(["--module", m.GetValue<string>()]);
                    var (code, output) = Captured(() => MemoryCommand.Run([.. args]));
                    if (code != 0 || output?["id"] is not JsonValue rid) throw new EvolveException("oml memory add refused the record: " + (output?["error"]?.GetValue<string>() ?? "no output"), 1, output?["hint"]?.GetValue<string>());
                    ids.Add(rid.GetValue<int>());
                }
                undo["records"] = ids;
                result = new { accepted = id, records = ids.Select(x => x!.GetValue<int>()).ToArray() };
                break;
            }
            case "skill":
            {
                var name = apply["skill"]!.GetValue<string>();
                var draft = apply["draft"]!.GetValue<string>();
                if (!File.Exists(draft)) throw new EvolveException($"The draft {draft} is gone. Run 'oml evolve propose' again.", 1);
                string? backup = null;
                if (CompanySkills.Installed().TryGetValue(name, out var installed))
                {
                    backup = Path.Combine(EvolveDir(folder), "backup", $"{id}-{name}.md");
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(installed, backup, overwrite: true);
                }
                var (code, output) = Captured(() => CompanySkills.Run(["add", draft, "--force"]));
                if (code != 0) throw new EvolveException("oml skills add refused the draft: " + (output?["error"]?.GetValue<string>() ?? "no output"), 1, "Fix the draft file, then accept again.");
                undo["skill"] = name;
                undo["backup"] = backup;
                result = new { accepted = id, skill = name, file = output?["file"]?.GetValue<string>(), open = $"oml doc {name}" };
                break;
            }
            case "patch":
            {
                var file = apply["file"]!.GetValue<string>();
                if (EvolveProposals.IsRules(file)) throw new EvolveException("A proposal never changes rules.md.", 2);
                if (!File.Exists(file)) throw new EvolveException($"File not found: {file}", 1);
                var before = File.ReadAllText(file);
                var lines = ((JsonArray)apply["insert"]!).Select(x => x!.GetValue<string>()).ToList();
                if (lines.All(l => before.Contains(l, StringComparison.Ordinal))) throw new EvolveException($"{Path.GetFileName(file)} has these lines already.", 1);
                var after = EvolveProposals.InsertUnder(before, apply["heading"]!.GetValue<string>(), lines);
                if (before.Contains("\r\n")) after = after.Replace("\n", "\r\n");
                var backup = Path.Combine(EvolveDir(folder), "backup", $"{id}-{Path.GetFileName(file)}");
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.WriteAllText(backup, before);
                File.WriteAllText(file, after);
                undo["file"] = file;
                undo["backup"] = backup;
                undo["after"] = Hash(after);
                undo["insert"] = new JsonArray(lines.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray());
                result = new { accepted = id, file, lines = lines.Count };
                break;
            }
            default: throw new EvolveException($"Proposal {id} has an unknown kind.", 1);
        }
        p["status"] = "accepted";
        p["acceptedAt"] = Now();
        Accepted(state).Add(new JsonObject
        {
            ["id"] = id, ["title"] = p["title"]!.DeepClone(), ["kind"] = p["kind"]!.DeepClone(), ["at"] = Now(), ["commands"] = p["commands"]!.DeepClone(),
            ["keys"] = new JsonArray((p["lines"] as JsonArray ?? []).Select(l => (JsonNode)JsonValue.Create(l!["key"]!.GetValue<string>())!).ToArray()),
            ["undo"] = undo,
        });
        SaveState(folder, state);
        TerminalOutput.PrintObject(result);
        return 0;
    }

    static int Undo(Opts o)
    {
        if (o.Words.Count != 1) throw new EvolveException("Give one proposal id.", 2, "Usage: oml evolve undo <id>");
        var folder = o.Folder;
        var id = o.Words[0];
        var state = LoadState(folder);
        var entry = Accepted(state).OfType<JsonObject>().LastOrDefault(a => a["id"]?.GetValue<string>() == id && a["undoneAt"] is null)
            ?? throw new EvolveException($"Proposal {id} is not accepted, or was undone already.", 1, "See: oml evolve history");
        var undo = (JsonObject)entry["undo"]!;
        object result;
        switch (entry["kind"]!.GetValue<string>())
        {
            case "memory":
            {
                var retracted = new List<int>();
                foreach (var rid in (JsonArray)undo["records"]!)
                {
                    var (code, output) = Captured(() => MemoryCommand.Run(["retract", rid!.GetValue<int>().ToString(CultureInfo.InvariantCulture), "--why", $"undone with oml evolve undo {id}", "--src", "evolve", "--dir", folder]));
                    if (code != 0 && output?["error"]?.GetValue<string>() is { } error && !error.Contains("already", StringComparison.Ordinal)) throw new EvolveException("oml memory retract failed: " + error, 1);
                    retracted.Add(rid.GetValue<int>());
                }
                result = new { undone = id, retracted };
                break;
            }
            case "skill":
            {
                var name = undo["skill"]!.GetValue<string>();
                if (CompanySkills.Installed().ContainsKey(name)) Captured(() => CompanySkills.Run(["remove", name]));
                if (undo["backup"]?.GetValue<string>() is { } backup && File.Exists(backup)) Captured(() => CompanySkills.Run(["add", backup, "--force"]));
                result = new { undone = id, skill = name, restored = undo["backup"] is not null };
                break;
            }
            case "patch":
            {
                var file = undo["file"]!.GetValue<string>();
                var now = File.ReadAllText(file);
                if (Hash(now) == undo["after"]!.GetValue<string>()) File.WriteAllText(file, File.ReadAllText(undo["backup"]!.GetValue<string>()));
                else
                {
                    // The file changed since: take out only the inserted lines, when they are still there together.
                    var nl = now.Contains("\r\n") ? "\r\n" : "\n";
                    var block = string.Join(nl, ((JsonArray)undo["insert"]!).Select(x => x!.GetValue<string>())) + nl;
                    if (!now.Contains(block, StringComparison.Ordinal)) throw new EvolveException($"{Path.GetFileName(file)} changed since the proposal was accepted, and the added lines are not there as they were. Remove them by hand.", 1);
                    File.WriteAllText(file, now.Replace(block, "", StringComparison.Ordinal));
                }
                result = new { undone = id, file };
                break;
            }
            default: throw new EvolveException($"Proposal {id} has an unknown kind.", 1);
        }
        entry["undoneAt"] = Now();
        if (Proposals(state)[id] is JsonObject p) p["status"] = "undone";
        SaveState(folder, state);
        TerminalOutput.PrintObject(result);
        return 0;
    }

    static int History(Opts o)
    {
        var state = LoadState(o.Folder);
        var accepted = Accepted(state);
        JsonArray? effects = null;
        if (o.Files.Count > 0 || Evidence.DefaultFile() is not null)
            effects = EvolveAnalysis.Build(LogFiles(o), null, o.Folder, []).Effects(accepted);
        TerminalOutput.PrintNode(new JsonObject
        {
            ["accepted"] = new JsonArray(accepted.OfType<JsonObject>().Select(a => (JsonNode)new JsonObject
            {
                ["id"] = a["id"]!.DeepClone(), ["title"] = a["title"]!.DeepClone(), ["kind"] = a["kind"]!.DeepClone(), ["at"] = a["at"]!.DeepClone(),
                ["undoneAt"] = a["undoneAt"]?.DeepClone(),
                ["effect"] = effects?.OfType<JsonObject>().FirstOrDefault(e => e["id"]?.GetValue<string>() == a["id"]!.GetValue<string>())?.DeepClone(),
                ["undo"] = a["undoneAt"] is null ? $"oml evolve undo {a["id"]}" : null,
            }).ToArray()),
            ["rejected"] = ((JsonObject)state["rejected"]!).Count,
        });
        return 0;
    }

    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")))).ToLowerInvariant()[..16];

    // Runs another oml command of the launcher and reads its JSON, without printing it.
    static (int Code, JsonNode? Output) Captured(Func<int> run)
    {
        var (previous, previousRun) = (Console.Out, TerminalOutput.InRun);
        var sw = new StringWriter();
        Console.SetOut(sw);
        TerminalOutput.InRun = true; // machine JSON, never the human view
        int code;
        try { code = run(); }
        finally { Console.SetOut(previous); TerminalOutput.InRun = previousRun; }
        JsonNode? json = null;
        try { json = JsonNode.Parse(sw.ToString()); } catch (JsonException) { }
        return (code, json);
    }
}
