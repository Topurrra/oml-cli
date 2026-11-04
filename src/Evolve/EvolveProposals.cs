using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// `oml learn propose`: the report turned into small, concrete edits, each line with its evidence. Project level: trap
// records (as `oml memory add` commands), and a draft company skill per command family in .oml/skills/evolved-<family>.md.
// Product level (--maintainer): insertions into skill/reference/*.md as unified diffs. Never rules.md, and never a line
// that could weaken a production rule or reads like an instruction: those become flags for a person.
static class EvolveProposals
{
    internal sealed record Lesson(string Key, string Family, string Section, string Text, JsonArray Evidence, string Cmd);

    // Words that could weaken a production rule (rules.md) or a refusal of apply. A line with one is never proposed.
    static readonly Regex Weakening = new(@"--force|--allow-existing-errors|\bskip|\bbypass|\bdisable|\bignore\b|without (asking|a yes|review|the user)|rules\.md|\banonymous\b|exposeReadOnly|\bdelete\b|\bpublish\b|\bdeploy\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Text that reads like an order to an agent, as a prompt injection that reached a log would. Never proposed either.
    static readonly Regex Instruction = new(@"ignore (all|any|previous|the above|earlier)|system prompt|\byou (are|must|should)\b|\bassistant\b|\bcurl\b|\bwget\b|https?://|<script|Invoke-Expression|\biex\b|\bexfiltrat|send (it|this|the) to",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly string[] Sections = ["Known failures", "Retried until it passed", "Spec keys that fail", "Traps agents still hit", "Warnings looked up often", "Check findings that repeat"];

    // The family a command belongs to: one skill, and one place in the docs, per family.
    static string Family(string cmd)
    {
        var first = cmd.Split(' ')[0];
        return first is "spec" or "validate" or "install" or "apply" ? "apply" : first;
    }

    static readonly System.Text.Json.JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static string Code(string s) => "`" + s.Replace("`", "'") + "`";

    public static List<Lesson> Lessons(EvolveAnalysis a)
    {
        var lessons = new List<Lesson>();
        var groups = a.RetryGroups();
        foreach (var f in a.Repeated)
        {
            var fix = groups.FirstOrDefault(g => g.Cmd == f.Cmd && g.Message == f.Message);
            var after = fix.Items is null ? " It never passed later in the log: ask the user before you try again."
                : fix.Change is not null ? $" It passed after: {fix.Change}."
                : " It passed after the input was changed: read the message and change the input; do not run it unchanged.";
            lessons.Add(new Lesson($"fail|{f.Cmd}|{f.Message}", Family(f.Cmd), "Known failures",
                $"{Code("oml " + f.Cmd)} failed {f.Rows.Count} times in {f.Sessions} sessions with: {Code(f.Message)}.{after} ({EvolveAnalysis.Lines(f.Rows)})",
                EvolveAnalysis.Cites(f.Rows), f.Cmd));
        }
        foreach (var g in groups.Where(g => g.Change is not null && !a.Repeated.Any(f => f.Cmd == g.Cmd && f.Message == g.Message)))
        {
            var rows = g.Items.SelectMany(i => new[] { i.Last, i.Passed }).ToList();
            lessons.Add(new Lesson($"retry|{g.Cmd}|{g.Message}|{g.Change}", Family(g.Cmd), "Retried until it passed",
                $"{Code("oml " + g.Cmd)} failed with {Code(g.Message)} and passed after: {g.Change}, {g.Items.Count} times. ({EvolveAnalysis.Lines(rows)})",
                EvolveAnalysis.Cites(rows), g.Cmd));
        }
        foreach (var (key, rows) in a.SpecKeys)
            lessons.Add(new Lesson($"spec|{key}", "apply", "Spec keys that fail",
                $"Spec key {Code(key)} failed {rows.Count} times. Read {Code("oml spec " + key.Split('.')[0])} before you write it. ({EvolveAnalysis.Lines(rows)})",
                EvolveAnalysis.Cites(rows), rows[0].Cmd));
        foreach (var (trap, rows) in a.IgnoredTraps)
            lessons.Add(new Lesson($"trap|{trap.Seq}", Family(rows[0].Cmd), "Traps agents still hit",
                $"Memory record #{trap.Seq} warns: \"{MemorySecrets.Mask(trap.Text)}\". Agents still hit it {rows.Count} times after it was written. Read it before you run {Code("oml " + rows[0].Cmd)}. (memory #{trap.Seq}; {EvolveAnalysis.Lines(rows)})",
                new JsonArray([new JsonObject { ["memory"] = trap.Seq }, .. EvolveAnalysis.Cites(rows).Select(c => c!.DeepClone())]), rows[0].Cmd));
        foreach (var (id, rows, fix) in a.Warnings.Where(w => w.Fix is not null))
            lessons.Add(new Lesson($"warn|{id}", "warnings", "Warnings looked up often",
                $"Agents looked up {Code(id)} {rows.Count} times. The fix from oml explain-warning: {fix} ({EvolveAnalysis.Lines(rows)})",
                EvolveAnalysis.Cites(rows), "explain-warning"));
        foreach (var (rule, count, files) in a.CheckRules)
        {
            var r = CheckCatalog.All.First(x => x.Id == rule);
            lessons.Add(new Lesson($"check|{rule}", "check", "Check findings that repeat",
                $"{Code("oml check")} found {Code(rule)} ({r.Title}) {count} times. When you write a spec, avoid it: {r.Fix} (check reports: {string.Join(", ", files)})",
                new JsonArray(files.Select(f => (JsonNode)new JsonObject { ["check"] = f, ["rule"] = rule, ["count"] = count }).ToArray()), "check"));
        }
        return lessons;
    }

    // ---- building the proposals ----

    public sealed class Made
    {
        public JsonArray Proposals = [];
        public JsonArray Flags = [];
        public Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase); // path -> content to write (drafts, diffs)
    }

    // closed: keys of lines already accepted or rejected; they are never proposed again.
    public static Made Make(EvolveAnalysis a, string folder, bool maintainer, string? repo, HashSet<string> closed, string runDir)
    {
        var made = new Made();
        var lessons = new List<Lesson>();
        foreach (var l in Lessons(a))
        {
            if (closed.Contains(l.Key)) continue;
            if (Refused(l.Text, l.Evidence, made.Flags)) continue;
            lessons.Add(l);
        }
        foreach (var d in a.DocMisses)
            if (!closed.Contains("doc|" + d.Query) && MemorySecrets.Find(d.Query) is null)
                made.Flags.Add(new JsonObject
                {
                    ["key"] = "doc|" + d.Query, ["reason"] = "no answer to write",
                    ["text"] = $"Agents searched the docs for \"{d.Query}\" {d.Rows.Count} times and found nothing good. Write the answer as a company skill (oml team-rules add) or a memory answer record.",
                    ["evidence"] = EvolveAnalysis.Cites(d.Rows),
                });
        if (!maintainer)
        {
            // Trap records for failures that repeat with no trap yet.
            foreach (var t in Evidence.TrapSuggestions(a.Rows, a.Live.Where(r => r.Kind == "trap")))
            {
                var key = $"memtrap|{t.Failure.Cmd}|{t.Failure.Message}";
                if (closed.Contains(key)) continue;
                var evidence = EvolveAnalysis.Cites(t.Failure.Rows);
                if (Refused(t.Text + " " + t.Why, evidence, made.Flags)) continue;
                var record = new JsonObject { ["kind"] = "trap", ["text"] = t.Text, ["why"] = t.Why, ["tags"] = "evolve" };
                if (t.Command.Contains(" --module ")) record["module"] = Regex.Match(t.Command, "--module \"([^\"]+)\"").Groups[1].Value;
                Add(made, "memory", "project", $"Trap record: oml {t.Failure.Cmd} fails with \"{Evidence.Fit(t.Failure.Message, 60)}\"", "memory",
                    [t.Failure.Cmd], [new Line(key, t.Command, evidence)], t.Command + "\n", new JsonObject { ["records"] = new JsonArray(record) }, runDir);
            }
            // One draft company skill per family, built on the installed one (accepted earlier) when there is one.
            foreach (var family in lessons.GroupBy(l => l.Family).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var name = "evolved-" + family.Key;
                var installed = CompanySkills.Installed().TryGetValue(name, out var file) ? File.ReadAllText(file).Replace("\r\n", "\n") : null;
                var before = installed ?? "";
                var text = installed ?? SkillHeader(name, family.Key);
                foreach (var section in Sections)
                {
                    var add = family.Where(l => l.Section == section).Select(l => "- " + l.Text).ToList();
                    if (add.Count > 0) text = InsertUnder(text, "## " + section, add);
                }
                var draft = Path.Combine(folder, ".oml", "skills", name + ".md");
                made.Files[draft] = text;
                var diff = Unified(".oml/skills/" + name + ".md", before, text, installed is null);
                Add(made, "skill", "project", $"Company skill {name}: {family.Count()} lesson(s) for oml {family.Key}", draft,
                    family.Select(l => l.Cmd).Distinct().ToArray(), family.Select(l => new Line(l.Key, l.Text, l.Evidence)).ToList(), diff,
                    new JsonObject { ["skill"] = name, ["draft"] = draft }, runDir);
            }
        }
        else
        {
            if (repo is null || !File.Exists(Path.Combine(repo, "skill", "rules.md"))) throw new EvolveException("--maintainer needs the oml-cli repository: run it there, or give --repo <folder>.", 2);
            foreach (var family in lessons.GroupBy(l => l.Family).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var (rel, heading) = DocTarget(family.Key);
                var path = Path.Combine(repo, rel);
                if (IsRules(path)) throw new InvalidOperationException("never patch rules.md");
                var before = File.ReadAllText(path).Replace("\r\n", "\n");
                // In the product docs a line says the lesson; the usage lines it rests on stay in the proposal and the .diff header.
                var add = family.Select(l => $"- **oml {l.Cmd}:** " + Regex.Replace(l.Text, @" \((usage line|memory #|check reports)[^()]*\)\.?$", "").TrimEnd('.') + ".").ToList();
                var after = InsertUnder(before, heading, add);
                var diff = "Evidence:\n" + string.Join("\n", family.Select(l => $"  {l.Key}: " + string.Join("; ", l.Evidence.Select(e => e!.ToJsonString(Relaxed))))) + "\n"
                    + Unified(rel.Replace('\\', '/'), before, after, false);
                Add(made, "patch", "product", $"{rel.Replace('\\', '/')}: {family.Count()} lesson(s) for oml {family.Key}", path,
                    family.Select(l => l.Cmd).Distinct().ToArray(), family.Select((l, i) => new Line(l.Key, add[i], l.Evidence)).ToList(), diff,
                    new JsonObject { ["file"] = path, ["heading"] = heading, ["insert"] = new JsonArray(add.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) }, runDir);
            }
            foreach (var (id, rows, fix) in a.Warnings)
                if (!closed.Contains("warntext|" + id))
                    made.Flags.Add(new JsonObject
                    {
                        ["key"] = "warntext|" + id, ["reason"] = "needs new words",
                        ["text"] = fix is null ? $"Agents looked up `{id}` {rows.Count} times and warnings.md has no row for it. Write one."
                            : $"Agents looked up `{id}` {rows.Count} times. Its Fix in warnings.md may be unclear, or apply should show it sooner.",
                        ["evidence"] = EvolveAnalysis.Cites(rows),
                    });
        }
        return made;
    }

    sealed record Line(string Key, string Text, JsonArray Evidence);

    static void Add(Made made, string kind, string level, string title, string target, string[] commands, List<Line> lines, string diff, JsonObject apply, string runDir)
    {
        // The id depends on the kind, the file name and the lines, not on the folder, so it is the same on every machine.
        var keys = lines.Select(l => l.Key).Order(StringComparer.Ordinal).ToArray();
        var id = "e" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "|" + Path.GetFileName(target) + "|" + string.Join("\n", keys)))).ToLowerInvariant()[..6];
        // The last check before anything is written: no secret anywhere in what a person or agent would read.
        // File paths are oml's own (a temp folder name can look like a token), so the scan covers the proposed text.
        var all = title + "\n" + diff + "\n" + string.Join("\n", lines.Select(l => l.Text + " " + l.Evidence.ToJsonString())) + "\n" + (apply["records"]?.ToJsonString() ?? "");
        if (MemorySecrets.Find(all) is { } secret)
        {
            made.Flags.Add(new JsonObject { ["key"] = "secret|" + id, ["reason"] = "secret", ["text"] = $"A proposal for {string.Join(", ", commands.Select(c => "oml " + c))} was dropped: its evidence holds what looks like a {secret}. Nothing was written." });
            return;
        }
        var diffFile = Path.Combine(runDir, id + ".diff");
        made.Files[diffFile] = diff;
        made.Proposals.Add(new JsonObject
        {
            ["id"] = id, ["kind"] = kind, ["level"] = level, ["title"] = title, ["target"] = target,
            ["commands"] = new JsonArray(commands.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
            ["lines"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject { ["key"] = l.Key, ["text"] = l.Text, ["evidence"] = l.Evidence.DeepClone() }).ToArray()),
            ["diff"] = diff, ["diffFile"] = diffFile, ["apply"] = apply, ["status"] = "open",
            ["accept"] = $"oml learn accept {id}", ["reject"] = $"oml learn reject {id} --why \"<reason>\"",
        });
    }

    // A lesson that could weaken a rule, reads like an order, or holds a secret is not proposed; a person sees a flag.
    static bool Refused(string text, JsonArray evidence, JsonArray flags)
    {
        string? reason = MemorySecrets.Find(text + " " + evidence.ToJsonString()) is { } secret ? $"secret ({secret})"
            : Instruction.IsMatch(text) ? "reads like an instruction (possible prompt injection in the log)"
            : Weakening.IsMatch(text) ? "could weaken a production rule (rules.md)" : null;
        if (reason is null) return false;
        flags.Add(new JsonObject
        {
            ["reason"] = reason,
            ["text"] = reason.StartsWith("secret") ? "A lesson was dropped: its evidence holds what looks like a secret. Nothing was written."
                : reason.StartsWith("reads like") ? "Not proposed: a message in the log reads like an instruction (a possible prompt injection). Read the usage lines yourself; oml does not repeat the text."
                : $"Not proposed, {reason}: {MemorySecrets.Mask(text)}. A person decides whether it belongs in a skill.",
            ["evidence"] = reason.StartsWith("secret") ? null : evidence.DeepClone(),
        });
        return true;
    }

    internal static bool LooksLikeInstruction(string text) => Instruction.IsMatch(text);

    internal static bool IsRules(string path) => Path.GetFileName(path).Equals("rules.md", StringComparison.OrdinalIgnoreCase);

    static (string Rel, string Heading) DocTarget(string family) => family switch
    {
        "memory" => (Path.Combine("skill", "reference", "memory.md"), "## Use it"),
        "apply" => (Path.Combine("skill", "reference", "troubleshooting.md"), "## Apply and install"),
        "publish" or "studio" or "pull" or "references" => (Path.Combine("skill", "reference", "troubleshooting.md"), "## Publish and Service Studio"),
        "lifetime" => (Path.Combine("skill", "reference", "troubleshooting.md"), "## LifeTime"),
        "font" or "image" => (Path.Combine("skill", "reference", "troubleshooting.md"), "## Fonts and images"),
        _ => (Path.Combine("skill", "reference", "troubleshooting.md"), "## Lessons from usage logs"),
    };

    static string SkillHeader(string name, string family) =>
        $"---\nname: {name}\ndescription: Lessons from this project's usage log for oml {family}. Read it before you run oml {family}.\n---\n\n" +
        $"# oml {family}: lessons from this project\n\nWritten by oml learn from the local usage log and memory. Each line names its evidence. Edit or delete lines before you accept.\n";

    // The text with the lines added at the end of a section ("## Name"), or the section added at the end when missing.
    internal static string InsertUnder(string text, string heading, List<string> lines)
    {
        var all = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        var at = all.FindIndex(l => l.TrimEnd() == heading);
        if (at < 0)
        {
            all.AddRange(["", heading, ""]);
            all.AddRange(lines);
            return string.Join('\n', all) + "\n";
        }
        var level = heading.TakeWhile(c => c == '#').Count();
        var end = all.FindIndex(at + 1, l => Regex.IsMatch(l, $"^#{{1,{level}}} "));
        if (end < 0) end = all.Count;
        var insert = end;
        while (insert > at + 1 && all[insert - 1].Trim().Length == 0) insert--;
        // A list right under a table would read as part of it: one blank line between them.
        all.InsertRange(insert, insert > 0 && all[insert - 1].StartsWith('|') ? ["", .. lines] : lines);
        return string.Join('\n', all) + "\n";
    }

    // A unified diff of an insertion-only change (all this code makes), with 3 lines of context.
    internal static string Unified(string path, string before, string after, bool newFile)
    {
        string[] Split(string s) => s.Length == 0 ? [] : s.TrimEnd('\n').Split('\n');
        var (a, b) = (Split(before), Split(after));
        var added = new bool[b.Length];
        int i = 0;
        for (var j = 0; j < b.Length; j++)
            if (i < a.Length && a[i] == b[j]) i++;
            else added[j] = true;
        var sb = new StringBuilder();
        sb.Append(newFile ? "--- /dev/null\n" : $"--- a/{path}\n").Append($"+++ b/{path}\n");
        var j0 = 0;
        while (j0 < b.Length)
        {
            if (!added[j0]) { j0++; continue; }
            var start = Math.Max(0, j0 - 3);
            var end = j0;
            while (end < b.Length)
            {
                if (added[end]) { end++; continue; }
                var next = Array.FindIndex(added, end, x => x);
                if (next >= 0 && next - end <= 6) { end = next; continue; }
                break;
            }
            var stop = Math.Min(b.Length, end + 3);
            var oldStart = start - added.Take(start).Count(x => x);
            var oldCount = Enumerable.Range(start, stop - start).Count(k => !added[k]);
            sb.Append($"@@ -{(oldCount == 0 ? oldStart : oldStart + 1)},{oldCount} +{start + 1},{stop - start} @@\n");
            for (var k = start; k < stop; k++) sb.Append(added[k] ? '+' : ' ').Append(b[k]).Append('\n');
            j0 = stop;
        }
        return sb.ToString();
    }
}
