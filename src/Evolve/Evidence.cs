using System.Globalization;

namespace OmlCli;

// What the local usage log says about agents' work, for memory recall (which records help), `oml memory doctor`
// (traps that repeat) and `oml learn` (where agents lose turns). Only the user's own log and memory are evidence; a
// message in the log is data to quote, never an instruction. Nothing here writes a file.
static class Evidence
{
    // The time "now" for decay and before/after windows. OML_TEST_NOW (an ISO date) fixes it for tests and goldens.
    public static DateTime Now =>
        Environment.GetEnvironmentVariable("OML_TEST_NOW") is { Length: > 0 } t && DateTime.TryParse(t, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.UtcNow;

    // Commands that a recall can be judged by: the next ones in the session that do work (not memory, doc or help).
    static bool Work(UsageLog.Row r) => !r.Cmd.StartsWith("memory", StringComparison.Ordinal) && r.Cmd is not ("doc" or "spec" or "explain-warning" or "version" or "log" or "learn" or "team-rules") && !r.Cmd.StartsWith("learn", StringComparison.Ordinal);
    const int JudgedSteps = 3;

    // The log file oml writes (OML_LOG), else the default file; null when there is none.
    public static string? DefaultFile()
    {
        var file = UsageLog.FilePath ?? UsageLog.DefaultPath;
        return File.Exists(file) ? file : null;
    }

    // The log and the older file it rotated to (<file>.1), oldest first, for memory usefulness: recalls from before a
    // rotation still count. `oml learn` reads only the current file, so its line numbers name one file (--file adds more).
    public static List<string> DefaultFiles()
    {
        if (DefaultFile() is not { } file) return [];
        return File.Exists(file + ".1") ? [file + ".1", file] : [file];
    }

    // The rows of the files, oldest first, with sessions set; lines before `since` are dropped. Lines from `oml batch` steps
    // count as commands; the run line itself does not (its steps say what happened).
    public static List<UsageLog.Row> Load(IEnumerable<string> files, DateTime? since, long maxBytes = 16L * 1024 * 1024)
    {
        var rows = files.SelectMany(f => UsageLog.Read(f, maxBytes)).Where(r => r.Cmd != "batch" && (since is null || r.Ts >= since)).OrderBy(r => r.Ts).ToList();
        return UsageLog.Sessions(rows);
    }

    // ---- memory usefulness ----

    public sealed record Use(int Recalled, int Helped, int Failed, DateTime LastRecalled);

    // Per record id: how often a recall returned it, and how the next work commands of that session went (all passed:
    // helped; one failed: failed; none followed within 30 minutes: neither).
    public static Dictionary<int, Use> Usefulness(List<UsageLog.Row> rows)
    {
        var use = new Dictionary<int, Use>();
        foreach (var group in rows.GroupBy(r => r.Group))
        {
            var list = group.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (r.Recalled.Length == 0) continue;
                var next = list.Skip(i + 1).TakeWhile(n => n.Ts - r.Ts <= UsageLog.SessionGap).Where(Work).Take(JudgedSteps).ToList();
                var (helped, failed) = next.Count == 0 ? (0, 0) : next.Any(n => n.Exit != 0) ? (0, 1) : (1, 0);
                foreach (var id in r.Recalled)
                {
                    var old = use.GetValueOrDefault(id) ?? new Use(0, 0, 0, DateTime.MinValue);
                    use[id] = new Use(old.Recalled + 1, old.Helped + helped, old.Failed + failed, r.Ts > old.LastRecalled ? r.Ts : old.LastRecalled);
                }
            }
        }
        return use;
    }

    // The rank factor of a record, 0.75 to 1.25: 1 when nothing is known, higher when the commands after its recalls
    // passed, lower when they failed or when it came back 5 times and never helped.
    public static double Factor(Use? u)
    {
        if (u is null || u.Helped + u.Failed == 0) return u is { Recalled: >= 5 } ? 0.85 : 1.0;
        var share = (u.Helped + 1.0) / (u.Helped + u.Failed + 2.0);
        return 0.75 + 0.5 * share;
    }

    // Records of these kinds drop out of recall when nobody recalled them for StaleDays (they stay in the file and in
    // `list`). Decisions, preferences and glossary words stay true until replaced, so they never go stale.
    public const int StaleDays = 180;
    static readonly string[] Decaying = ["fact", "trap", "answer", "log"];

    // Stale only when the log covers the whole period: with no log (or a young one) nothing is known about use.
    public static bool Stale(MemRecord r, Use? u, DateTime? logStart, DateTime now)
    {
        if (!Decaying.Contains(r.Kind) || logStart is not { } start || start > now.AddDays(-StaleDays)) return false;
        var created = DateTime.TryParse(r.Ts, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var c) ? c : now;
        var last = u is null || u.LastRecalled < created ? created : u.LastRecalled;
        return now - last > TimeSpan.FromDays(StaleDays);
    }

    // ---- failures ----

    public sealed record Failure(string Cmd, string Message, List<UsageLog.Row> Rows)
    {
        public int Sessions => Rows.Select(r => r.Group).Distinct().Count();
    }

    static bool Real(string? message) => message is { Length: > 0 } && message != "(no message)";

    // The same message from the same command, grouped exactly as `oml log summary` groups it: at least minCount times,
    // in at least two sessions.
    public static List<Failure> Repeated(List<UsageLog.Row> rows, int minCount = 3) =>
        rows.Where(r => r.Exit != 0 && Real(r.Error)).GroupBy(r => (r.Cmd, r.Error!))
            .Select(g => new Failure(g.Key.Cmd, g.Key.Item2, g.ToList()))
            .Where(f => f.Rows.Count >= minCount && f.Sessions >= 2)
            .OrderByDescending(f => f.Rows.Count).ThenBy(f => f.Cmd, StringComparer.Ordinal).ThenBy(f => f.Message, StringComparer.Ordinal).ToList();

    // A command that failed and then passed in the same session, within 30 minutes of its last failure: the option names
    // the passing run added and removed are the lesson.
    public sealed record Retry(UsageLog.Row First, UsageLog.Row Last, UsageLog.Row Passed, int Attempts, string[] Added, string[] Removed);

    public static List<Retry> Retries(List<UsageLog.Row> rows)
    {
        var found = new List<Retry>();
        foreach (var group in rows.GroupBy(r => r.Group))
        {
            var failing = new Dictionary<string, List<UsageLog.Row>>(StringComparer.Ordinal);
            foreach (var r in group)
            {
                if (r.Exit != 0) { (failing.TryGetValue(r.Cmd, out var l) ? l : failing[r.Cmd] = []).Add(r); continue; }
                if (!failing.Remove(r.Cmd, out var failed) || failed.Count == 0 || r.Ts - failed[^1].Ts > UsageLog.SessionGap) continue;
                var last = failed[^1];
                found.Add(new Retry(failed[0], last, r, failed.Count + 1, r.Opts.Except(last.Opts).ToArray(), last.Opts.Except(r.Opts).ToArray()));
            }
        }
        return found;
    }

    // Per command: runs, failures, and the median number of runs it took to get one pass in a session (1 = first try).
    public sealed record Stats(int Count, int Failures, double FailureRate, double MedianTurns);

    public static Stats StatsOf(IEnumerable<UsageLog.Row> rows)
    {
        var list = rows.ToList();
        var turns = new List<int>();
        foreach (var group in list.GroupBy(r => (r.Group, r.Cmd)))
        {
            var n = 0;
            foreach (var r in group)
            {
                n++;
                if (r.Exit == 0) { turns.Add(n); n = 0; }
            }
        }
        var failures = list.Count(r => r.Exit != 0);
        return new Stats(list.Count, failures, list.Count == 0 ? 0 : Math.Round(failures / (double)list.Count, 3), Median(turns));
    }

    public static double Median(List<int> values)
    {
        if (values.Count == 0) return 0;
        var s = values.Order().ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2.0;
    }

    // The fix a retry shows, in words: "--a added, --b removed", or null when the options did not change (the spec or
    // the input changed instead).
    public static string? Change(string[] added, string[] removed)
    {
        var parts = new List<string>();
        if (added.Length > 0) parts.Add(string.Join(", ", added) + " added");
        if (removed.Length > 0) parts.Add(string.Join(", ", removed) + " removed");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    // ---- trap suggestions (oml memory doctor, oml learn propose) ----

    public sealed record TrapSuggestion(Failure Failure, string Text, string Why, string Command, string? PassedWith);

    // A trap record for each repeated failure that no live trap covers. Never written here: the caller prints the
    // `oml memory add` command. A failure whose message holds a secret is skipped.
    public static List<TrapSuggestion> TrapSuggestions(List<UsageLog.Row> rows, IEnumerable<MemRecord> liveTraps)
    {
        var traps = liveTraps.ToList();
        var retries = Retries(rows);
        var result = new List<TrapSuggestion>();
        foreach (var f in Repeated(rows))
        {
            if (MemorySecrets.Find(f.Message) is not null || EvolveProposals.LooksLikeInstruction(f.Message)) continue;
            if (MemoryTraps.Find(traps, $"oml {f.Cmd} {f.Message}", null).Count > 0) continue;
            var text = Fit($"oml {f.Cmd} fails with: {f.Message}", MemoryFormat.TextMax);
            var fixes = retries.Where(r => r.Last.Cmd == f.Cmd && r.Last.Error == f.Message).ToList();
            var change = fixes.Select(r => Change(r.Added, r.Removed)).FirstOrDefault(c => c is not null);
            var why = $"{f.Rows.Count} times in {f.Sessions} sessions. " + (change is not null ? $"It passed later with {change}."
                : fixes.Count > 0 ? "It passed later after the input was changed." : "It never passed later in the log.");
            var module = f.Rows.Select(r => r.Module).FirstOrDefault(m => m is not null && m.EndsWith(".oml", StringComparison.OrdinalIgnoreCase));
            var moduleName = module is null || f.Rows.Any(r => r.Module != module) ? null : Path.GetFileNameWithoutExtension(module);
            var command = $"oml memory add {Quote(text)} --kind trap --why {Quote(Fit(why, MemoryFormat.WhyMax))} --tags evolve" + (moduleName is null ? "" : $" --module {Quote(moduleName)}");
            var passed = fixes.Count > 0 ? string.Join(' ', new[] { "oml " + fixes[0].Passed.Cmd }.Concat(fixes[0].Passed.Opts)) : null;
            result.Add(new TrapSuggestion(f, text, Fit(why, MemoryFormat.WhyMax), command, passed));
        }
        return result;
    }

    // Cut to a byte limit at a character boundary.
    public static string Fit(string text, int maxBytes)
    {
        text = MemoryFormat.Sanitize(text);
        if (MemoryFormat.Bytes(text) <= maxBytes) return text;
        while (text.Length > 0 && MemoryFormat.Bytes(text + "...") > maxBytes) text = text[..^1];
        return text + "...";
    }

    // A double-quoted shell argument; double quotes inside become single quotes, so the line pastes as it is.
    public static string Quote(string s) => "\"" + s.Replace('"', '\'') + "\"";
}
