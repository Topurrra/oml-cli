using System.Text.RegularExpressions;

namespace OmlCli;

// The frontend skills in skill/frontend (UI design rules and UI workflows for OutSystems, adapted from jakubkrehel/skills,
// MIT), embedded as OmlCli.frontend.<folder>\<file>.md. `oml doc <name>` prints one: `frontend` is the README, `<skill>`
// its SKILL.md and `<skill>/<file>` a linked file. They are not doc topics: the topic list, SKILL.md's Topics line and
// the topic budgets stay as they are. `oml doc --search` adds the best matching skill after its doc hits (WithBest).
static class FrontendSkills
{
    const string Prefix = "OmlCli.frontend.";
    const string Readme = "frontend";

    static Dictionary<string, string>? names;

    // name -> resource name
    static Dictionary<string, string> Names => names ??= Build();

    static Dictionary<string, string> Build()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in typeof(FrontendSkills).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix) && n.EndsWith(".md")))
        {
            var parts = resource[Prefix.Length..^3].Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var name = parts switch
            {
                [var file] when file.Equals("README", StringComparison.OrdinalIgnoreCase) => Readme,
                [var skill, var file] when file.Equals("SKILL", StringComparison.OrdinalIgnoreCase) => skill,
                [var skill, var file] => $"{skill}/{file}",
                _ => null,
            };
            if (name is not null) found[name] = resource;
        }
        return found;
    }

    // The skills (folders with a SKILL.md), in name order.
    internal static List<string> Skills() => Names.Keys.Where(n => n != Readme && !n.Contains('/')).Order(StringComparer.Ordinal).ToList();

    internal static List<string> All() => Names.Keys.Order(StringComparer.Ordinal).ToList();

    // The file's text without its frontmatter, or null when no frontend file has that name.
    internal static string? Text(string name)
    {
        if (!Names.TryGetValue(name.Replace('\\', '/'), out var resource)) return null;
        return Regex.Replace(SpecDocs.Resource(resource), @"\A---\n.*?\n---\n", "", RegexOptions.Singleline).Trim();
    }

    static SearchIndex? index;

    // `oml doc --search`: the best matching frontend skill is added after the doc hits, never in their place, so the ranking
    // of the docs (tests/search-bench.json) does not change. Only a match that is not weak is added; a query that is a
    // skill's name puts that skill first.
    internal static List<SearchHit> WithBest(string query, List<SearchHit> hits)
    {
        index ??= new SearchIndex(SearchDocs().ToList());
        var found = index.Search(query, 1);
        if (found.Count == 0 || hits.Any(h => h.Doc.Open == found[0].Doc.Open)) return hits;
        if (SpecDocs.Norm(query) == SpecDocs.Norm(found[0].Doc.Title)) return [found[0], .. hits];
        return index.Weak(found, query) ? hits : [.. hits, found[0]];
    }

    // One search entry per skill: the title is its name, the keys its description, the body its section headings.
    internal static IEnumerable<SearchDoc> SearchDocs()
    {
        foreach (var skill in Skills())
        {
            var raw = SpecDocs.Resource(Names[skill]);
            var description = CompanySkills.Header(raw)?.Description ?? "";
            var headings = Regex.Matches(raw, @"^## (.+?)\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value)
                .Where(h => h is not "Before you finish" and not "Reporting" and not "Report");
            yield return new SearchDoc
            {
                Id = "frontend:" + skill, Kind = "skill", Title = skill, Open = $"oml doc {skill}", Names = [skill],
                Keys = description, Body = string.Join(". ", headings), Shown = description,
            };
        }
    }
}
