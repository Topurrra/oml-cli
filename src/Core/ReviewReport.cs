using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// The `oml review` reports made from its full JSON: Markdown for a pull request or an approval, short release notes
// for testers, and one offline HTML page in the style of the check page.
static class ReviewReport
{
    static IEnumerable<JsonObject> List(JsonObject d, string key) => d[key]!.AsArray().Select(n => n!.AsObject());
    static string S(JsonNode? n) => n?.GetValue<string>() ?? "";
    static int N(JsonObject d, string key) => d["summary"]![key]!.GetValue<int>();

    static string Many(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    static string Counts(JsonObject d) =>
        $"{N(d, "added")} added, {N(d, "removed")} removed, {N(d, "recreated")} recreated, {N(d, "renamed")} renamed, {N(d, "changed")} changed";

    // Text in Markdown stays text: characters with an inline meaning there are escaped (names, expressions, SQL), and
    // so are @ and #123 or !123, which would mention people or link issues and merge requests. No text starts a line,
    // so line-start markers (#, -, 1., >) need no other escape.
    static readonly Regex Special = new(@"[\\`*_\[\]<|~&@]|[#!](?=\d)", RegexOptions.CultureInvariant);
    static string Md(string text) => Special.Replace(text.Replace("\r", "").Replace("\n", " "), m => "\\" + m.Value);

    static string RuleLabel(JsonObject r) => $"{r["rule"]} ({Md(S(r["ruleName"]))}){(r["info"] is not null ? ", info" : "")}";

    public static string Markdown(JsonObject d)
    {
        var md = new StringBuilder();
        md.AppendLine($"# Review: {Md(S(d["module"]))}").AppendLine();
        md.AppendLine($"Before: {Md(Path.GetFileName(S(d["before"])))}. After: {Md(Path.GetFileName(S(d["after"])))}.").AppendLine();
        md.AppendLine($"**{Counts(d)}.** {Many(N(d, "risks"), "risk")}, {N(d, "info")} to look at. {Many(N(d, "newFindings"), "new check finding")} ({N(d, "resolvedFindings")} resolved).").AppendLine();
        md.AppendLine($"> {Md(Review.NotCovered)}").AppendLine();

        md.AppendLine("## Risks").AppendLine();
        var risks = List(d, "risks").ToList();
        if (risks.Count == 0) md.AppendLine("No production-rule risks found.");
        else
        {
            md.AppendLine("| Rule | Element | What |").AppendLine("|---|---|---|");
            foreach (var r in risks) md.AppendLine($"| {RuleLabel(r)} | {Md(S(r["element"]))} | {Md(S(r["text"]))} |");
        }
        md.AppendLine();

        md.AppendLine("## Changes").AppendLine();
        var changes = List(d, "changes").ToList();
        if (changes.Count == 0) md.AppendLine("No changes.").AppendLine();
        foreach (var (id, title) in Review.Areas)
        {
            var inArea = changes.Where(c => S(c["area"]) == id).ToList();
            if (inArea.Count == 0) continue;
            md.AppendLine($"### {title}").AppendLine();
            foreach (var c in inArea)
            {
                md.AppendLine($"- **{Md(S(c["kind"]))} {Md(S(c["name"]))}**: {S(c["change"])}");
                foreach (var detail in c["details"]!.AsArray()) md.AppendLine($"  - {Md(S(detail))}");
                foreach (var text in c["texts"]?.AsArray().Select(t => t!.AsObject()) ?? [])
                {
                    var lines = text["lines"]!.AsArray().Select(S).ToList();
                    // A fence longer than any run of backticks in the lines, so code never closes it.
                    var fence = new string('`', Math.Max(3, lines.Select(l => Regex.Matches(l, "`+").Select(m => m.Length).DefaultIfEmpty(0).Max()).DefaultIfEmpty(0).Max() + 1));
                    md.AppendLine($"  - Changed lines of {Md(S(text["subject"]))}:").AppendLine();
                    md.AppendLine($"    {fence}diff");
                    foreach (var line in lines) md.AppendLine("    " + line);
                    md.AppendLine($"    {fence}").AppendLine();
                }
            }
            md.AppendLine();
        }

        md.AppendLine("## New check findings").AppendLine();
        var findings = List(d, "newFindings").ToList();
        if (findings.Count == 0) md.AppendLine("No new findings.");
        else
        {
            md.AppendLine("| Severity | Rule | Element | Finding |").AppendLine("|---|---|---|---|");
            foreach (var f in findings) md.AppendLine($"| {Md(S(f["severity"]))} | {Md(S(f["rule"]))} | {Md(S(f["element"]))} | {Md(S(f["title"]))} |");
        }
        foreach (var (key, title) in new[] { ("skipped", "Not checked"), ("warnings", "Warnings") })
        {
            var items = d[key]!.AsArray();
            if (items.Count == 0) continue;
            md.AppendLine().AppendLine($"## {title}").AppendLine();
            foreach (var item in items) md.AppendLine($"- {Md(S(item))}");
        }
        if (d["unreadable"]!.AsArray() is { Count: > 0 } unreadable)
        {
            md.AppendLine().AppendLine("## Parts that could not be read").AppendLine();
            foreach (var u in unreadable) md.AppendLine($"- {Md(S(u!["part"]))}: {Md(S(u["reason"]))}");
        }
        md.AppendLine().AppendLine($"Made by oml review {Md(S(d["omlVersion"]))}.");
        return md.ToString();
    }

    static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // Release notes for testers: one plain line per changed element, then what needs care.
    public static string Notes(JsonObject d)
    {
        var text = new StringBuilder();
        text.AppendLine($"# Release notes: {Md(S(d["module"]))}").AppendLine();
        var changes = List(d, "changes").ToList();
        text.AppendLine(changes.Count == 0 ? "No changes to test." : "What changed, to test:").AppendLine();
        foreach (var c in changes)
        {
            var (kind, name, details) = (S(c["kind"]), S(c["name"]), c["details"]!.AsArray().Select(S).ToList());
            var line = S(c["change"]) switch
            {
                "added" => $"New {kind} {name}.",
                "removed" => $"Removed {kind} {name}.",
                "recreated" => $"Recreated {kind} {name}: test it as a new element.",
                "renamed" => $"Renamed {kind} {details[0]["renamed ".Length..]}.",
                _ => $"Changed {kind} {name}.",
            };
            // What a tester looks at: the first changes of a changed element, the main part of a new one, nothing for one that is gone.
            var useful = S(c["change"]) switch { "changed" or "renamed" => details.Where(x => !x.StartsWith("renamed from", StringComparison.Ordinal)).ToList(),
                "added" => details, _ => [] };
            var shown = useful.Take(S(c["change"]) == "added" ? 1 : 3).ToList();
            if (shown.Count > 0) line += " " + Capital(string.Join("; ", shown)) + (useful.Count > shown.Count ? "; and more" : "") + ".";
            text.AppendLine("- " + Md(line));
        }
        var risks = List(d, "risks").ToList();
        if (risks.Count > 0)
        {
            text.AppendLine().AppendLine("Test with care (production risks):").AppendLine();
            foreach (var r in risks) text.AppendLine("- " + Md(Capital(S(r["text"]))));
        }
        var findings = List(d, "newFindings").ToList();
        if (findings.Count > 0)
        {
            text.AppendLine().AppendLine("New check findings:").AppendLine();
            foreach (var f in findings) text.AppendLine($"- {Md(S(f["title"]))}: {Md(S(f["element"]))} ({Md(S(f["severity"]))})");
        }
        text.AppendLine().AppendLine(Md(Review.NotCovered));
        return text.ToString();
    }

    // The page shows file names only: a full path tells readers of a shared page about the author's folders.
    public static string Html(JsonObject d)
    {
        var page = d.DeepClone().AsObject();
        foreach (var key in new[] { "before", "after" }) page[key] = Path.GetFileName(S(d[key]));
        return CheckPage.Embed(page.ToJsonString(), "OmlCli.Review.template.html", "/*__REVIEW_JSON__*/");
    }
}
