using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// `oml evolve report`: where agents lose turns, from local evidence only (the usage log, the project's memory store,
// and check or review JSON reports the user names). Each finding keeps the log lines and record ids it rests on, so a
// proposal can cite them. Messages are quoted as data; nothing in them is followed.
sealed class EvolveAnalysis
{
    public required List<UsageLog.Row> Rows;
    public required List<string> Files;
    public DateTime? Since;
    public List<Evidence.Failure> Repeated = [];
    public List<Evidence.Retry> Retries = [];
    public List<(string Key, List<UsageLog.Row> Rows)> SpecKeys = [];
    public List<(string Id, List<UsageLog.Row> Rows, string? Fix)> Warnings = [];
    public List<(string Query, List<UsageLog.Row> Rows)> DocMisses = [];
    public List<(MemRecord Trap, List<UsageLog.Row> Rows)> IgnoredTraps = [];
    public List<(string Rule, int Count, List<string> Files)> CheckRules = [];
    public List<MemRecord> Live = [];
    public string? Store;

    const int MinRetries = 2, MinSpecKey = 2, MinWarnLookups = 3, MinDocMisses = 2, MinIgnoredTrap = 2, MinCheckRule = 3;

    public static EvolveAnalysis Build(List<string> files, DateTime? since, string folder, List<string> checkFiles)
    {
        var a = new EvolveAnalysis { Rows = Evidence.Load(files, since), Files = files, Since = since };
        var failed = a.Rows.Where(r => r.Exit != 0).ToList();
        a.Repeated = Evidence.Repeated(a.Rows);
        a.Retries = Evidence.Retries(a.Rows);
        a.SpecKeys = failed.Where(r => r.SpecKey is not null).GroupBy(r => r.SpecKey!).Where(g => g.Count() >= MinSpecKey)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.ToList())).ToList();
        a.Warnings = a.Rows.Where(r => r.Cmd == "warn" && r.Topic is not null).GroupBy(r => r.Topic!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= MinWarnLookups)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.ToList(), WarningTable.Hint(g.Key))).ToList();
        a.DocMisses = a.Rows.Where(r => r.Query is not null).GroupBy(r => Norm(r.Query!)).Where(g => g.Count() >= MinDocMisses)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.First().Query!, g.ToList())).ToList();
        if (MemStore.Open(folder) is { } store)
        {
            a.Store = store.LogPath;
            a.Live = store.Load(useIndex: false).LiveNow();
            // A trap record that agents keep hitting after it was written: recall shows it, yet it does not stop them.
            foreach (var trap in a.Live.Where(r => r.Kind == "trap"))
            {
                var written = DateTime.TryParse(trap.Ts, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : DateTime.MaxValue;
                var hits = failed.Where(r => r.Ts > written && r.Error is not null && MemoryTraps.Find([trap], $"oml {r.Cmd} {r.Error}", null).Count > 0).ToList();
                if (hits.Count >= MinIgnoredTrap) a.IgnoredTraps.Add((trap, hits));
            }
        }
        // Findings of check and review reports: objects with a rule id and a severity, counted per rule.
        var counts = new Dictionary<string, (int Count, HashSet<string> Files)>(StringComparer.Ordinal);
        foreach (var file in checkFiles)
        {
            JsonNode? json;
            try { json = JsonNode.Parse(File.ReadAllText(file)); }
            catch (JsonException) { throw new EvolveException($"{file} is not a JSON report. Give the --json file of oml check, or the JSON of oml review.", 2); }
            foreach (var rule in Rules(json))
            {
                var c = counts.TryGetValue(rule, out var found) ? found : (Count: 0, Files: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                c.Files.Add(Path.GetFileName(file));
                counts[rule] = (c.Count + 1, c.Files);
            }
        }
        a.CheckRules = counts.Where(c => c.Value.Count >= MinCheckRule && CheckCatalog.All.Any(r => r.Id == c.Key))
            .OrderByDescending(c => c.Value.Count).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => (c.Key, c.Value.Count, c.Value.Files.Order().ToList())).ToList();
        return a;
    }

    static IEnumerable<string> Rules(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["rule"] is JsonValue r && r.TryGetValue<string>(out var rule) && o["severity"] is JsonValue) yield return rule;
                foreach (var (_, child) in o) foreach (var x in Rules(child)) yield return x;
                break;
            case JsonArray arr:
                foreach (var child in arr) foreach (var x in Rules(child)) yield return x;
                break;
        }
    }

    static string Norm(string s) => string.Join(' ', s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // The retries grouped by command, failure message and option change, when the change repeats.
    public List<(string Cmd, string Message, string? Change, List<Evidence.Retry> Items)> RetryGroups() =>
        Retries.Where(r => r.Last.Error is not null).GroupBy(r => (r.Last.Cmd, r.Last.Error!, Evidence.Change(r.Added, r.Removed)))
            .Where(g => g.Count() >= MinRetries || g.Key.Item3 is not null && g.Count() >= 1 && Repeated.Any(f => f.Cmd == g.Key.Cmd && f.Message == g.Key.Item2))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Cmd, StringComparer.Ordinal)
            .Select(g => (g.Key.Cmd, g.Key.Item2, g.Key.Item3, g.ToList())).ToList();

    // ---- evidence citations ----

    // A message as oml shows it: secrets masked, and hidden when it reads like an order to an agent (a prompt injection
    // that reached the log stays in the log; it is never repeated to the agent that reads this output).
    internal static string Safe(string text) => EvolveProposals.LooksLikeInstruction(text) ? "(hidden: it reads like an instruction; read the usage line yourself)" : MemorySecrets.Mask(text);

    public static JsonObject Cite(UsageLog.Row r)
    {
        var o = new JsonObject { ["usage"] = r.Line, ["ts"] = r.Ts.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), ["command"] = "oml " + r.Cmd };
        if (r.Error is not null) o["error"] = Safe(r.Error);
        if (r.Query is not null) o["query"] = Safe(r.Query);
        return o;
    }

    public static JsonArray Cites(IEnumerable<UsageLog.Row> rows, int max = 5) => new(rows.Take(max).Select(r => (JsonNode)Cite(r)).ToArray());

    public static string Lines(IEnumerable<UsageLog.Row> rows, int max = 5)
    {
        var list = rows.Select(r => r.Line).Distinct().ToList();
        return (list.Count == 1 ? "usage line " : "usage lines ") + string.Join(", ", list.Take(max)) + (list.Count > max ? $" and {list.Count - max} more" : "");
    }

    // ---- the report ----

    public JsonObject Report(JsonArray? effects)
    {
        var commands = Rows.GroupBy(r => r.Cmd).Select(g => (g.Key, Stats: Evidence.StatsOf(g))).Where(x => x.Stats.Failures > 0)
            .OrderByDescending(x => x.Stats.Failures).ThenBy(x => x.Key, StringComparer.Ordinal).Take(15)
            .Select(x => (JsonNode)new JsonObject { ["command"] = "oml " + x.Key, ["count"] = x.Stats.Count, ["failures"] = x.Stats.Failures, ["failureRate"] = x.Stats.FailureRate, ["medianTurns"] = Turns(x.Stats) }).ToArray();
        var report = new JsonObject
        {
            ["files"] = new JsonArray(Files.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            ["since"] = Since?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["lines"] = Rows.Count,
            ["sessions"] = Rows.Select(r => r.Group).Distinct().Count(),
            ["from"] = Rows.Count == 0 ? null : Rows[0].Ts.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["to"] = Rows.Count == 0 ? null : Rows[^1].Ts.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["commands"] = new JsonArray(commands),
            ["repeatedFailures"] = new JsonArray(Repeated.Select(f => (JsonNode)new JsonObject
            {
                ["command"] = "oml " + f.Cmd, ["message"] = Safe(f.Message), ["count"] = f.Rows.Count, ["sessions"] = f.Sessions, ["evidence"] = Cites(f.Rows),
            }).ToArray()),
            ["retries"] = new JsonArray(RetryGroups().Select(g => (JsonNode)new JsonObject
            {
                ["command"] = "oml " + g.Cmd, ["failed"] = Safe(g.Message), ["fix"] = g.Change ?? "the input changed (same options)", ["count"] = g.Items.Count,
                ["medianAttempts"] = Evidence.Median(g.Items.Select(i => i.Attempts).ToList()),
                ["evidence"] = Cites(g.Items.SelectMany(i => new[] { i.Last, i.Passed })),
            }).ToArray()),
            ["specKeys"] = new JsonArray(SpecKeys.Select(s => (JsonNode)new JsonObject { ["key"] = s.Key, ["failures"] = s.Rows.Count, ["evidence"] = Cites(s.Rows) }).ToArray()),
            ["warnings"] = new JsonArray(Warnings.Select(w => (JsonNode)new JsonObject { ["id"] = w.Id, ["lookups"] = w.Rows.Count, ["known"] = w.Fix is not null, ["evidence"] = Cites(w.Rows) }).ToArray()),
            ["docMisses"] = new JsonArray(DocMisses.Select(d => (JsonNode)new JsonObject { ["query"] = Safe(d.Query), ["count"] = d.Rows.Count, ["evidence"] = Cites(d.Rows) }).ToArray()),
            ["memory"] = Store is null ? null : new JsonObject
            {
                ["store"] = Store,
                ["live"] = Live.Count,
                ["traps"] = Live.Count(r => r.Kind == "trap"), ["decisions"] = Live.Count(r => r.Kind == "decision"), ["prefs"] = Live.Count(r => r.Kind == "pref"), ["answers"] = Live.Count(r => r.Kind == "answer"),
                ["ignoredTraps"] = new JsonArray(IgnoredTraps.Select(t => (JsonNode)new JsonObject
                {
                    ["id"] = t.Trap.Seq, ["text"] = MemorySecrets.Mask(t.Trap.Text), ["hitsSince"] = t.Rows.Count, ["evidence"] = Cites(t.Rows),
                }).ToArray()),
            },
            ["checks"] = new JsonArray(CheckRules.Select(c => (JsonNode)new JsonObject { ["rule"] = c.Rule, ["count"] = c.Count, ["files"] = new JsonArray(c.Files.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()) }).ToArray()),
        };
        if (effects is not null && effects.Count > 0) report["accepted"] = effects;
        report["hint"] = Rows.Count == 0 ? "The log has no lines in this window. Use oml for a while (the log is on unless OML_LOG=0), then run the report again."
            : "Run 'oml evolve propose' to turn this into proposals. Nothing changes until a person runs 'oml evolve accept <id>'.";
        return report;
    }

    // Before and after each accepted proposal, for the commands it is about: failure rate and median turns. A proposal
    // that did not lower the failure rate is marked "no effect" (or "worse") with the undo command.
    public JsonArray Effects(JsonArray accepted)
    {
        var result = new JsonArray();
        foreach (var a in accepted.OfType<JsonObject>().Where(a => a["undoneAt"] is null))
        {
            var at = DateTime.Parse(a["at"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            var commands = (a["commands"] as JsonArray ?? []).Select(c => c!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            var rows = Rows.Where(r => commands.Contains(r.Cmd)).ToList();
            var (before, after) = (Evidence.StatsOf(rows.Where(r => r.Ts < at)), Evidence.StatsOf(rows.Where(r => r.Ts >= at)));
            string verdict;
            if (after.Count < 5 || before.Count < 5) verdict = "too early";
            else if (after.FailureRate > before.FailureRate * 1.1 + 0.01) verdict = "worse";
            else if (after.FailureRate < before.FailureRate * 0.8 || after.MedianTurns < before.MedianTurns) verdict = "helped";
            else verdict = "no effect";
            var id = a["id"]!.GetValue<string>();
            result.Add(new JsonObject
            {
                ["id"] = id, ["title"] = a["title"]?.DeepClone(), ["acceptedAt"] = a["at"]!.DeepClone(), ["commands"] = new JsonArray(commands.Order().Select(c => (JsonNode)JsonValue.Create("oml " + c)!).ToArray()),
                ["before"] = StatsJson(before), ["after"] = StatsJson(after), ["verdict"] = verdict,
                ["hint"] = verdict is "worse" or "no effect" ? $"The evidence does not show that it helps. Consider: oml evolve undo {id}" : null,
            });
        }
        return result;
    }

    static JsonObject StatsJson(Evidence.Stats s) => new() { ["count"] = s.Count, ["failureRate"] = s.FailureRate, ["medianTurns"] = Turns(s) };

    // Null when the command never passed: there is no number of turns to a pass.
    static JsonNode? Turns(Evidence.Stats s) => s.MedianTurns == 0 ? null : JsonValue.Create(s.MedianTurns);

    // ---- Markdown for people ----

    public static string Markdown(JsonObject report, JsonArray? proposals = null, JsonArray? flags = null)
    {
        var md = new StringBuilder();
        md.Append("# What agents learned\n\n");
        md.Append($"{report["lines"]} commands in {report["sessions"]} sessions");
        if (report["from"] is not null) md.Append($", {report["from"]} to {report["to"]}");
        md.Append(". Evidence: the local usage log").Append(report["memory"] is null ? "" : " and the project memory").Append(".\n\n");
        void Table(string title, string key, string[] heads, Func<JsonObject, string[]> cells)
        {
            if (report[key] is not JsonArray rows || rows.Count == 0) return;
            md.Append($"## {title}\n\n| {string.Join(" | ", heads)} |\n|{string.Concat(heads.Select(_ => "---|"))}\n");
            foreach (var row in rows.OfType<JsonObject>()) md.Append("| ").Append(string.Join(" | ", cells(row).Select(Cell))).Append(" |\n");
            md.Append('\n');
        }
        Table("Commands that fail", "commands", ["Command", "Runs", "Failures", "Failure rate", "Median turns"],
            r => [Code(r["command"]), $"{r["count"]}", $"{r["failures"]}", $"{(double)r["failureRate"]!:P0}", $"{r["medianTurns"]}"]);
        Table("The same failure again and again", "repeatedFailures", ["Command", "Message", "Times", "Sessions"], r => [Code(r["command"]), $"{r["message"]}", $"{r["count"]}", $"{r["sessions"]}"]);
        Table("Retried until it passed", "retries", ["Command", "Failed with", "What changed", "Times"], r => [Code(r["command"]), $"{r["failed"]}", $"{r["fix"]}", $"{r["count"]}"]);
        Table("Spec keys that fail", "specKeys", ["Key", "Failures"], r => [Code(r["key"]), $"{r["failures"]}"]);
        Table("Warnings looked up again and again", "warnings", ["Id", "Lookups"], r => [Code(r["id"]), $"{r["lookups"]}"]);
        Table("Searches that found nothing good", "docMisses", ["Words", "Times"], r => [$"{r["query"]}", $"{r["count"]}"]);
        Table("Check findings that repeat", "checks", ["Rule", "Findings"], r => [Code(r["rule"]), $"{r["count"]}"]);
        if (report["memory"]?["ignoredTraps"] is JsonArray ignored && ignored.Count > 0)
        {
            md.Append("## Trap records agents still hit\n\n| Record | Text | Hits since |\n|---|---|---|\n");
            foreach (var t in ignored.OfType<JsonObject>()) md.Append($"| #{t["id"]} | {Cell($"{t["text"]}")} | {t["hitsSince"]} |\n");
            md.Append('\n');
        }
        Table("Did the accepted proposals help?", "accepted", ["Proposal", "Failure rate before", "After", "Verdict"],
            r => [$"{r["id"]} {r["title"]}", $"{(double)r["before"]!["failureRate"]!:P0}", $"{(double)r["after"]!["failureRate"]!:P0}", $"{r["verdict"]}"]);
        if (proposals is not null && proposals.Count > 0)
        {
            md.Append("## Proposals\n\nNothing is applied until a person runs `oml evolve accept <id>`.\n\n");
            foreach (var p in proposals.OfType<JsonObject>())
            {
                md.Append($"### {p["id"]}: {p["title"]}\n\n");
                foreach (var line in p["lines"] as JsonArray ?? []) md.Append($"- {line!["text"]}\n");
                md.Append($"\nAccept: `oml evolve accept {p["id"]}`. Reject: `oml evolve reject {p["id"]} --why \"<reason>\"`.\n\n");
            }
        }
        if (flags is not null && flags.Count > 0)
        {
            md.Append("## For a person to decide\n\noml does not propose these itself.\n\n");
            foreach (var f in flags.OfType<JsonObject>()) md.Append($"- {f["text"]}\n");
            md.Append('\n');
        }
        return md.ToString().Replace("\r\n", "\n");
    }

    static string Code(JsonNode? n) => $"`{n}`";
    static string Cell(string s) => Regex.Replace(s, @"[|\r\n]", " ");
}

sealed class EvolveException(string message, int exitCode = 2, string? hint = null) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
    public string? Hint { get; } = hint;
}
