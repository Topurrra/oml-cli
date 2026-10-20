using System.Text;
using System.Text.RegularExpressions;

namespace OmlCli;

// `oml spec <key>`: prints one section of skill/reference/spec.md. Both the reference and the key index in
// skill/SKILL.md are embedded at build time, so this works from a release zip with no docs folder.
sealed class SpecSection
{
    public int Level;
    public string Title = "";
    public string Anchor = "";
    public List<string> Body = new();
    public List<SpecSection> Children = new();
}

static class SpecDocs
{
    // Topics that live inside a larger section or whose SKILL.md row names a broader section.
    // Each target must be a heading in spec.md; 'oml spec --verify' (and tests/spec-check.ps1) fail if one is not.
    static readonly (string[] Keys, string Heading)[] Aliases =
    {
        (new[] { "flow", "nodes", "node", "nodeKinds", "actions", "serverActions", "serviceActions", "clientActions",
                 "assign", "if", "forEach", "switch", "execute", "refreshData", "destination", "raiseException", "end",
                 "jsonDeserialize", "jsonSerialize", "javaScript" }, "Actions and flow nodes"),
        (new[] { "comment", "message", "download", "excelToRecordList", "recordListToExcel", "sendEmail" }, "Additional flow nodes"),
        (new[] { "tidyFlows" }, "tidyFlows"),
        (new[] { "removeUnusedDependencies" }, "Remove unused dependencies"),
        (new[] { "insertNodes", "addWidgets", "addParts", "addVariables", "setAccess", "set", "rename", "addExceptionHandler" }, "Edits"),
        (new[] { "attributes", "attribute", "types", "type" }, "Data model"),
        (new[] { "widgets", "widget", "onClick" }, "Reactive UI and Mobile UI"),
        (new[] { "preparation" }, "Traditional Web UI"),
        (new[] { "access" }, "Platform elements"),
        (new[] { "overview", "intro", "notation" }, "Spec reference"),
    };

    static List<SpecSection>? sections;
    static List<(string[] Keys, string Description, string Section)>? index;

    public static List<SpecSection> Sections => sections ??= ParseSections(Resource("OmlCli.spec.md"));
    public static List<(string[] Keys, string Description, string Section)> Index => index ??= ParseIndex(Resource("OmlCli.SKILL.md"));

    static string Resource(string name)
    {
        using var stream = typeof(SpecDocs).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"This build has no embedded resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // GitHub heading anchor: lower case, drop punctuation, spaces become hyphens.
    static string Slug(string title) =>
        Regex.Replace(Regex.Replace(title.ToLowerInvariant(), @"[^\p{L}\p{N} _-]", ""), " ", "-");

    static List<SpecSection> ParseSections(string text)
    {
        var all = new List<SpecSection>();
        var stack = new List<SpecSection>();
        SpecSection? current = null;
        var fence = false;
        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```")) fence = !fence;
            var heading = fence ? null : Regex.Match(line, @"^(#{1,6}) (.+?)\s*$");
            if (heading is { Success: true })
            {
                var section = new SpecSection { Level = heading.Groups[1].Length, Title = heading.Groups[2].Value };
                section.Anchor = Slug(section.Title);
                while (stack.Count > 0 && stack[^1].Level >= section.Level) stack.RemoveAt(stack.Count - 1);
                stack.LastOrDefault()?.Children.Add(section);
                stack.Add(section);
                all.Add(section);
                current = section;
            }
            else current?.Body.Add(line);
        }
        foreach (var s in all)
        {
            while (s.Body.Count > 0 && s.Body[^1].Trim().Length == 0) s.Body.RemoveAt(s.Body.Count - 1);
            while (s.Body.Count > 0 && s.Body[0].Trim().Length == 0) s.Body.RemoveAt(0);
        }
        return all;
    }

    // The key index is the table under "## 4. Spec keys" in SKILL.md: | keys | what it does | section |.
    static List<(string[], string, string)> ParseIndex(string text)
    {
        var rows = new List<(string[], string, string)>();
        var inTable = false;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("## ")) inTable = line.Contains("Spec keys");
            if (!inTable || !line.StartsWith("| `")) continue;
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 3) continue;
            var keys = Regex.Matches(cells[0], @"`([A-Za-z][A-Za-z0-9]*)`").Select(m => m.Groups[1].Value).ToArray();
            rows.Add((keys, string.Join("|", cells[1..^1]), cells[^1]));
        }
        return rows;
    }

    static SpecSection? Heading(string title) =>
        Sections.FirstOrDefault(s => s.Title.Equals(title, StringComparison.OrdinalIgnoreCase));

    static IEnumerable<(string Key, string Heading)> KeyTargets()
    {
        foreach (var (keys, heading) in Aliases) foreach (var k in keys) yield return (k, heading);
        foreach (var (keys, _, section) in Index) foreach (var k in keys) yield return (k, section);
    }

    static List<SpecSection> Resolve(string query)
    {
        var q = Norm(query);
        // 1. a key or topic name: aliases first, then the SKILL.md index (a key can map to several sections).
        var named = KeyTargets().Where(t => Norm(t.Key) == q).Select(t => Heading(t.Heading)).OfType<SpecSection>().Distinct().ToList();
        if (named.Count > 0) return named;
        // 2. a section heading, exact then partial.
        var exact = Sections.Where(s => Norm(s.Title) == q).ToList();
        if (exact.Count > 0) return exact;
        if (q.Length >= 3)
        {
            var partial = Sections.Where(s => Norm(s.Title).Contains(q)).OrderBy(s => s.Title.Length).ToList();
            if (partial.Count > 0) return partial;
        }
        // 3. a backticked name mentioned in a section: the section that mentions it most.
        var mention = new Regex("`" + Regex.Escape(query) + "`", RegexOptions.IgnoreCase);
        return Sections.Select(s => (s, n: mention.Matches(string.Join("\n", s.Body)).Count)).Where(x => x.n > 0)
            .OrderByDescending(x => x.n).Select(x => x.s).ToList();
    }

    public static IEnumerable<string> AllNames() =>
        KeyTargets().Select(t => t.Key).Concat(Sections.Select(s => s.Title)).Distinct(StringComparer.OrdinalIgnoreCase);

    public static int Show(string query)
    {
        var found = Resolve(query);
        if (found.Count == 0)
        {
            var q = Norm(query);
            var close = AllNames().Select(n => (n, d: Norm(n).Contains(q) || q.Contains(Norm(n)) ? 0 : Distance(Norm(n), q)))
                .OrderBy(x => x.d).ThenBy(x => x.n.Length).Take(5).Select(x => x.n);
            Console.Error.WriteLine($"Unknown spec key or topic '{query}'. Closest: {string.Join(", ", close)}. Run 'oml spec' for the list.");
            return 2;
        }
        var main = found[0];
        var sb = new StringBuilder();
        sb.Append(new string('#', main.Level)).Append(' ').AppendLine(main.Title);
        if (main.Body.Count > 0) sb.AppendLine().AppendLine(string.Join("\n", main.Body));
        if (main.Children.Count > 0)
            sb.AppendLine().AppendLine("Subtopics: " + string.Join(", ", main.Children.Select(c => $"oml spec \"{c.Title}\"")));
        if (found.Count > 1)
            sb.AppendLine().AppendLine("See also: " + string.Join(", ", found.Skip(1).Take(3).Select(c => $"oml spec \"{c.Title}\"")));
        sb.AppendLine().Append($"Full reference: skill/reference/spec.md#{main.Anchor}");
        Console.WriteLine(sb.ToString());
        return 0;
    }

    public static int List()
    {
        var seen = new HashSet<string>();
        foreach (var (all, description, section) in Index)
        {
            var keys = all.Length > 1 ? all.Where(k => k != "flow").ToArray() : all; // "flow" only says where a node kind goes
            var line = $"{string.Join(", ", keys)}  -  {description}";
            Console.WriteLine(seen.Add(string.Join(",", keys)) ? line : $"{line} [{section}]");
        }
        Console.WriteLine();
        Console.WriteLine("Flow node kinds (assign, if, forEach, switch, execute, aggregate, sql, javaScript, ...) and section titles also work.");
        Console.WriteLine("oml spec <key>  prints one key. oml spec check <spec.json>  checks a spec. oml spec schema  writes a JSON Schema.");
        return 0;
    }

    // Maintainer check: every key and alias must still lead to a heading in spec.md.
    public static int Verify()
    {
        var bad = KeyTargets().Where(t => Heading(t.Heading) is null).Select(t => $"'{t.Key}' -> heading '{t.Heading}' not found in spec.md")
            .Distinct().ToList();
        foreach (var b in bad) Console.WriteLine(b);
        if (bad.Count == 0) Console.WriteLine($"ok: {KeyTargets().Count()} keys resolve to {Sections.Count} sections.");
        return bad.Count == 0 ? 0 : 1;
    }

    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1); // swapped letters
            }
        return d[a.Length, b.Length];
    }

    // Top-level key descriptions for the schema, from the same SKILL.md index.
    public static Dictionary<string, string> KeyDescriptions()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (keys, description, _) in Index) foreach (var k in keys) map.TryAdd(k, description);
        return map;
    }
}
