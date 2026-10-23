using System.Text;
using System.Text.RegularExpressions;

namespace OmlCli;

// `oml doc [<topic>[#section]]` and `oml warn <id | title words>`: the knowledge files in skill/reference, embedded
// at build time as OmlCli.doc.<topic>.md. Runs in the launcher, so it works without Service Studio.
static class DocCommand
{
    const string Prefix = "OmlCli.doc.";
    const string DocUsage = "Usage: oml doc [<topic>[#section]]   (no topic lists the topics)\n       oml doc --search \"<words>\" [--top N] [--in <folder> | --library]   (ranked search of the topics, spec keys, recipes, warnings and commands; --library: the OutSystems 11 docs)";
    const string WarnUsage = "Usage: oml warn <id | title words>   (for example: oml warn NoErrorHandlingInFlow, oml warn security warning)";

    // The knowledge files, plus `rules`: skill/rules.md, embedded once as OmlCli.rules.md (the dashboard shows it too).
    internal static List<string> Topics() => typeof(DocCommand).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix) && n.EndsWith(".md")).Select(n => n[Prefix.Length..^3]).Append("rules").Order(StringComparer.Ordinal).ToList();

    internal static string Text(string topic) => SpecDocs.Resource(topic == "rules" ? "OmlCli.rules.md" : Prefix + topic + ".md");

    static int Fail(string message) { Console.Error.WriteLine(message); return 2; }

    public static int Run(string[] args) => args switch
    {
        [] => List(),
        ["--help" or "-h"] => Help(DocUsage),
        ["--search", .. var rest] => DocSearch.Run(rest, specOnly: false),
        ["--search-verify"] => DocSearch.Verify(),
        ["--search-bench", .. var rest] => DocSearch.Bench(rest),
        ["--search-list"] => DocSearch.List(),
        ["--search-terms", .. var rest] => DocSearch.Terms(rest),
        ["--search-stems", .. var rest] => DocSearch.Stems(rest),
        [var topic] => Show(topic),
        _ => Fail(DocUsage),
    };

    static int Help(string usage) { Console.WriteLine(usage); return 0; }

    static int List()
    {
        foreach (var topic in Topics()) Console.WriteLine($"{topic}  -  {Summary(Text(topic))}");
        var company = CompanySkills.Installed();
        if (company.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Company skills (oml skills; they override OutSystems best practices, never rules):");
            foreach (var (name, file) in company) Console.WriteLine($"{name}  -  {CompanySkills.Header(File.ReadAllText(file))!.Value.Description}");
        }
        Console.WriteLine();
        Console.WriteLine("oml doc <topic> prints one topic; oml doc <topic>#<section> prints one section (oml doc ui-reactive#forms).");
        Console.WriteLine("oml warn <id> prints the cause and fix of one validation message.");
        Console.WriteLine("oml doc --search \"<words>\" finds the section, spec key, recipe or warning that answers a question.");
        return 0;
    }

    // First sentence of the first paragraph under the title.
    static string Summary(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')) ?? "";
        var end = line.IndexOf(". ", StringComparison.Ordinal);
        return end < 0 ? line : line[..(end + 1)];
    }

    static int Show(string query)
    {
        var hash = query.IndexOf('#');
        var (name, section) = hash < 0 ? (query, null) : (query[..hash], query[(hash + 1)..]);
        if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
        // A company skill (oml skills): its names never clash with a topic.
        if (CompanySkills.Installed().TryGetValue(name, out var skill))
        {
            var body = CompanySkills.Text(skill);
            if (string.IsNullOrWhiteSpace(section)) { Console.WriteLine(body); return 0; }
            return ShowSection(name, body, section);
        }
        var topics = Topics();
        var q = SpecDocs.Norm(name);
        var matches = topics.Where(t => SpecDocs.Norm(t) == q).ToList();
        if (matches.Count == 0 && q.Length >= 3) matches = topics.Where(t => SpecDocs.Norm(t).StartsWith(q)).ToList();
        if (matches.Count != 1)
        {
            var close = matches.Count > 1 ? matches : Closest(topics, q);
            return Fail($"Unknown doc topic '{name}'. Closest: {string.Join(", ", close)}. Run 'oml doc' for the list.");
        }
        var text = Text(matches[0]).TrimEnd();
        if (string.IsNullOrWhiteSpace(section)) { Console.WriteLine(text); return 0; }
        return ShowSection(matches[0], text, section);
    }

    static List<string> Closest(IEnumerable<string> names, string q) =>
        names.Select(n => (n, d: SpecDocs.Norm(n).Contains(q) || q.Contains(SpecDocs.Norm(n)) ? 0 : SpecDocs.Distance(SpecDocs.Norm(n), q)))
            .OrderBy(x => x.d).ThenBy(x => x.n.Length).Take(3).Select(x => x.n).ToList();

    // A section runs from its heading to the next heading of the same or a higher level.
    static int ShowSection(string topic, string text, string query)
    {
        var lines = text.Split('\n');
        var headings = new List<(int Index, int Level, string Title)>();
        var fence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```")) fence = !fence;
            if (!fence && Regex.Match(lines[i], @"^(#{2,6}) (.+?)\s*$") is { Success: true } m)
                headings.Add((i, m.Groups[1].Length, m.Groups[2].Value));
        }
        var q = SpecDocs.Norm(query);
        var found = headings.Where(h => SpecDocs.Norm(h.Title) == q || SpecDocs.Slug(h.Title) == query.ToLowerInvariant()).ToList();
        if (found.Count == 0 && q.Length >= 3) found = headings.Where(h => SpecDocs.Norm(h.Title).StartsWith(q)).ToList();
        if (found.Count == 0 && q.Length >= 3) found = headings.Where(h => SpecDocs.Norm(h.Title).Contains(q)).ToList();
        if (found.Count == 0)
            return Fail($"No section '{query}' in {topic}. Sections: {string.Join(", ", headings.Select(h => SpecDocs.Slug(h.Title)))}.");
        if (found.Count > 1)
            return Fail($"Section '{query}' matches several sections of {topic}: {string.Join(", ", found.Select(h => SpecDocs.Slug(h.Title)))}. Use one of them.");
        var start = found[0];
        var end = headings.FirstOrDefault(h => h.Index > start.Index && h.Level <= start.Level).Index;
        if (end <= start.Index) end = lines.Length;
        Console.WriteLine(string.Join("\n", lines[start.Index..end]).TrimEnd());
        return 0;
    }

    public static int Warn(string[] args)
    {
        if (args is ["--help" or "-h"]) return Help(WarnUsage);
        var query = string.Join(" ", args).Trim().Trim('`');
        if (query.Length == 0) return Fail(WarnUsage);
        var rows = WarningTable.Rows;
        var found = rows.Where(r => r.Ids.Any(i => i.Equals(query, StringComparison.OrdinalIgnoreCase))).ToList();
        if (found.Count == 0)
        {
            var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            found = rows.Where(r => words.All(w => (r.Title + " " + string.Join(" ", r.Ids)).ToLowerInvariant().Contains(w))).ToList();
        }
        if (found.Count == 0)
        {
            // Rank the rows with the search engine (ids, titles, causes and fixes; typos and synonyms). A row is close
            // only when it holds at least half of the query's own words (or their typo fixes); otherwise say so.
            var index = DocSearch.Index;
            var close = index.Search(query, 5, d => d.Kind == "warning" && d.Names.Length > 0)
                .Where(h => index.Coverage(h.Doc, query) >= 0.5).Select(h => h.Doc.Names[0]).ToList();
            return Fail(close.Count == 0
                ? $"No validation message matches '{query}', and none is a close match. Full list: oml doc warnings."
                : $"No validation message matches '{query}'. Closest: {string.Join(", ", close)}. Full list: oml doc warnings.");
        }
        var sb = new StringBuilder();
        foreach (var group in found.GroupBy(r => r.Section))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("## " + group.Key).AppendLine("| Id | Title | Cause | Fix |").AppendLine("|---|---|---|---|");
            foreach (var r in group) sb.AppendLine(r.Line);
        }
        Console.Write(sb.ToString());
        return 0;
    }
}
