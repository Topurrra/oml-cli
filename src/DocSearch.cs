using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// `oml doc --search` and `oml spec --search`: one ranked search over everything the CLI knows without Service Studio.
// The corpus is built in memory from the embedded files: every `oml doc` topic split into sections (and table rows),
// every warnings.md row, the spec reference sections and its key index, and every recipe's header and keys.
// Each hit says how to open the whole answer: an `oml doc`, `oml spec` or `oml warn` command, or a recipe path.
static class DocSearch
{
    const string Usage = "Usage: oml doc --search \"<words>\" [--top N] [--in <folder of .md files> | --library]";
    const int DefaultTop = 3, MaxTop = 20;

    static List<SearchDoc>? corpus;
    static SearchIndex? index;
    internal static List<SearchDoc> Corpus => corpus ??= Build();
    internal static SearchIndex Index => index ??= new SearchIndex(Corpus);

    static int Fail(string message) { Console.Error.WriteLine(message); return 2; }

    // args: the words of the query (one or several arguments), --top N, --in <folder>. specOnly: `oml spec --search`.
    public static int Run(string[] args, bool specOnly)
    {
        var words = new List<string>();
        var top = DefaultTop;
        string? folder = null;
        var library = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--top")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[++i], out top) || top < 1 || top > MaxTop) return Fail($"--top takes a number from 1 to {MaxTop}. {Usage}");
            }
            else if (args[i] == "--library" && !specOnly) library = true;
            else if (args[i] == "--in" && !specOnly)
            {
                if (i + 1 >= args.Length) return Fail(Usage);
                folder = args[++i];
            }
            else if (args[i].StartsWith("--")) return Fail($"Unknown option {args[i]}. {Usage}");
            else words.Add(args[i]);
        }
        var query = string.Join(" ", words).Trim();
        if (query.Length == 0) return Fail(specOnly ? "Usage: oml spec --search \"<words>\" [--top N]" : Usage);
        SearchIndex searched;
        var notes = new List<string>();
        if (library && folder is not null) return Fail($"--in and --library do not go together. {Usage}");
        if (library)
        {
            var folders = Library.Folders();
            if (folders.Count == 0) return Fail("No library yet. Run 'oml library fetch' first (it downloads the OutSystems 11 docs).");
            try { searched = LibraryIndex(folders, notes); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { return Fail($"Cannot read the library at {Clip(Library.Root, 300)}: {Clip(e.Message, 300)}"); }
        }
        else if (folder is null) searched = Index;
        else
        {
            if (!Directory.Exists(folder)) return Fail($"Folder not found: {Clip(Path.GetFullPath(folder), 300)}");
            try { searched = new SearchIndex(Folder(folder, notes, MaxFiles, MaxBytes, "file")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { return Fail($"Cannot read {Clip(folder, 300)}: {Clip(e.Message, 300)}"); }
        }
        var hits = searched.Search(query, top, specOnly ? d => d.Kind is "spec" or "key" : null);
        // Weak is judged on this index's own hits; a strong company match makes the answer not weak.
        var weak = hits.Count > 0 && searched.Weak(hits, query);
        if (!specOnly && !library && folder is null && WithCompany(query, top, hits) is { } merged) (hits, weak) = (merged.Hits, weak && !merged.Strong);
        var result = new JsonObject
        {
            ["query"] = query,
            ["hits"] = new JsonArray(hits.Select(h => (JsonNode)new JsonObject
            {
                ["kind"] = h.Doc.Kind, ["title"] = h.Doc.Title, ["open"] = h.Doc.Open, ["snippet"] = h.Snippet, ["score"] = h.Score,
            }).ToArray()),
        };
        if (notes.Count > 0) result["notes"] = new JsonArray(notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
        if (hits.Count == 0) result["hint"] = specOnly ? "No match. 'oml spec' lists every key." : "No match. 'oml doc' lists the topics; 'oml spec' lists the spec keys.";
        else if (weak)
        {
            result["weak"] = true;
            result["hint"] = "Weak match: rephrase with OutSystems terms (entity, screen, aggregate, ...) or open the top hits; the docs may not cover this."
                + (library || folder is not null ? "" : " A platform question (Service Center, LifeTime, servers, errors)? Add --library.");
        }

        TerminalOutput.PrintNode(result);
        return 0;
    }

    // Company skills (oml skills) override OutSystems best practices, so their hits come first: all of them when the
    // company match is not weak, else only a company hit that scores at least as high as the best oml hit.
    static (List<SearchHit> Hits, bool Strong)? WithCompany(string query, int top, List<SearchHit> hits)
    {
        var skills = CompanySkills.Installed();
        if (skills.Count == 0) return null;
        var docs = skills.SelectMany(s => Markdown(CompanySkills.Text(s.Value), s.Key, CompanySkills.Kind, slug => slug is null ? $"oml doc {s.Key}" : $"oml doc {s.Key}#{slug}", rows: true)).ToList();
        var company = new SearchIndex(docs);
        var found = company.Search(query, top);
        var strong = found.Count > 0 && !company.Weak(found, query);
        if (!strong) found = found.Take(1).Where(h => hits.Count == 0 || h.Score >= hits[0].Score).ToList();
        return (found.Concat(hits).Take(top).ToList(), strong);
    }

    static List<SearchDoc> Build()
    {
        var docs = new List<SearchDoc>();
        foreach (var topic in DocCommand.Topics())
            docs.AddRange(Markdown(DocCommand.Text(topic), topic, "doc", slug => slug is null ? $"oml doc {topic}" : $"oml doc {topic}#{slug}",
                rows: topic != "warnings", numbered: topic == "rules"));
        foreach (var row in WarningTable.Rows)
        {
            var name = row.Ids.FirstOrDefault() ?? row.Title;
            docs.Add(new SearchDoc
            {
                Id = "warn:" + (row.Ids.FirstOrDefault() ?? SearchText.Norm(row.Title)), Kind = "warning", Title = row.Ids.Length > 0 ? $"{row.Title} ({string.Join(", ", row.Ids)})" : row.Title,
                Open = row.Ids.Length > 0 ? $"oml warn {name}" : $"oml warn \"{row.Title}\"",
                Keys = string.Join(" ", row.Ids) + "\n" + row.Section, Names = row.Ids, Body = $"{Plain(row.Cause)} Fix: {Plain(row.Fix)}",
                Parent = "warnings#" + SpecDocs.Slug(row.Section), // a row ranked above its warnings.md section hides the section
            });
        }
        docs.AddRange(Spec());
        docs.AddRange(Recipes());
        docs.AddRange(Commands());
        // Glossary entries: an everyday word alone ("enum", "modal") finds the OutSystems concept's main section.
        var targets = docs.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var (concept, target, adds, everyday) in SearchGlossary.Entries)
        {
            if (!targets.TryGetValue(target, out var t)) continue; // --search-verify reports it
            var words = string.Join(", ", everyday.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            docs.Add(new SearchDoc
            {
                Id = "glossary:" + SearchText.Norm(concept), Kind = t.Kind, Title = concept, Open = t.Open, Parent = t.Id, TitleIsName = false,
                Keys = words, Body = $"OutSystems calls {words}: {concept} ({adds}).",
            });
        }
        // Ids are unique: the benchmark and the verification name hits by id.
        foreach (var group in docs.GroupBy(d => d.Id).Where(g => g.Count() > 1))
        {
            var n = 1;
            foreach (var d in group.Skip(1)) d.Id += "~" + ++n;
        }
        return docs;
    }

    // Sections of a markdown text: the text under the first heading, then one entry per heading of level 2 or more with
    // the text up to the next heading. With rows, each table row is an entry of its own (title: its first cell). With
    // numbered, each numbered line is one too (rules.md: "Rule 2: Never delete an existing entity").
    internal static List<SearchDoc> Markdown(string text, string name, string kind, Func<string?, string> open, bool rows, bool numbered = false)
    {
        var docs = new List<SearchDoc>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var fence = false;
        string topTitle = name;
        SearchDoc? current = null;
        var body = new List<string>();
        string[]? header = null;
        void Close()
        {
            if (current is null) return;
            current.Body = string.Join("\n", body).Trim();
            docs.Add(current);
            body.Clear();
        }
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```")) fence = !fence;
            var heading = fence ? Match.Empty : Regex.Match(line, @"^(#{1,6}) (.+?)\s*$");
            if (heading.Success)
            {
                Close();
                var title = Regex.Replace(heading.Groups[2].Value, @"\s*\{\s*#[^}]*\}\s*$", ""); // "Title { #anchor }" in the OutSystems docs
                if (heading.Groups[1].Length == 1 && current is null)
                {
                    topTitle = title;
                    current = new SearchDoc { Id = name, Kind = kind, Title = title, Open = open(null), Keys = name };
                }
                else current = new SearchDoc { Id = $"{name}#{SpecDocs.Slug(title)}", Kind = kind, Title = Clip(Plain(title), 80), Open = open(Clip(SpecDocs.Slug(title), 80)), Keys = topTitle };
                header = null;
                continue;
            }
            current ??= new SearchDoc { Id = name, Kind = kind, Title = topTitle, Open = open(null), Keys = name };
            // Without rows (warnings.md, whose rows are warning entries of their own), a table stays out of the section's
            // text, so the section does not outrank the row that answers.
            if (rows || fence || !line.StartsWith('|')) body.Add(line);
            if (numbered && !fence && Regex.Match(line, @"^(\d+)\. (.+)$") is { Success: true } item)
            {
                var rule = item.Groups[1].Value;
                var lead = Regex.Match(item.Groups[2].Value, @"^\*\*(.+?)\*\*") is { Success: true } bold ? bold.Groups[1].Value : item.Groups[2].Value;
                docs.Add(new SearchDoc
                {
                    Id = $"{current.Id}/rule-{rule}", Kind = kind, Title = Clip($"Rule {rule}: {Plain(lead).TrimEnd('.', ':')}", 80), Open = current.Open,
                    Parent = current.Id, Keys = topTitle, Names = [$"rule {rule}"], Body = Plain(item.Groups[2].Value),
                });
                continue;
            }
            if (!rows || fence || !line.StartsWith('|')) { if (!line.StartsWith('|')) header = null; continue; }
            var cells = line.Trim().Trim('|').Split(" | ").Select(c => c.Trim()).ToArray();
            if (header is null) { header = cells; continue; }
            if (Regex.IsMatch(line, @"^[\s|:-]+$")) continue; // the |---|---| line under the header
            var first = Plain(cells[0]);
            if (first.Length == 0) continue;
            docs.Add(new SearchDoc
            {
                Id = $"{current.Id}/{Clip(SearchText.Norm(first), 40)}", Kind = kind, Title = Clip(first, 80), Open = current.Open, Parent = current.Id,
                Keys = current.Title + "\n" + topTitle, Names = Regex.Matches(cells[0], "`([^`]+)`").Select(m => m.Groups[1].Value).Append(first).ToArray(),
                Body = string.Join("; ", cells.Skip(1).Select(Plain).Where(c => c.Length > 0)),
            });
        }
        Close();
        return docs;
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    // Markdown marks out of a table cell or title: backticks, bold, links.
    static string Plain(string s) => Regex.Replace(s.Replace("`", "").Replace("**", ""), @"\[([^\]]+)\]\([^)]*\)", "$1").Trim();

    static IEnumerable<SearchDoc> Spec()
    {
        var aliases = SpecDocs.AliasTargets().ToLookup(a => a.Heading, a => a.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var s in SpecDocs.Sections.Where(s => s.Title != "Spec keys"))
        {
            var names = aliases[s.Title].ToArray();
            var section = Markdown(string.Join("\n", s.Body), "spec#" + s.Anchor, "spec", _ => SpecOpen(s.Title), rows: true);
            // The parser saw no heading: its one entry without a parent is the section itself, the others are its rows.
            var main = section.Single(d => d.Parent is null);
            main.Title = s.Title;
            main.Keys = string.Join(" ", names);
            main.Names = names;
            foreach (var row in section.Where(d => d.Parent is not null)) row.Keys = s.Title;
            foreach (var d in section) yield return d;
        }
        foreach (var (keys, description, sectionTitle) in SpecDocs.Index)
        {
            if (keys.Length == 0) continue;
            var shown = keys.Length > 1 ? keys.Where(k => k != "flow").ToArray() : keys; // as `oml spec` lists them
            yield return new SearchDoc
            {
                // The section of this row, not `oml spec <key>`: a key such as screens has one row per UI kind.
                Id = "key:" + shown[0], Kind = "key", Title = string.Join(", ", shown), Open = SpecOpen(Plain(sectionTitle)),
                Keys = sectionTitle, Names = keys, Body = Plain(description),
            };
        }
    }

    static string SpecOpen(string title) => title.Contains(' ') ? $"oml spec \"{title}\"" : $"oml spec {title}";

    // A recipe: its header comment (what it builds, the base, related keys) and the keys, node kinds, widget types and
    // edit operations it uses.
    static IEnumerable<SearchDoc> Recipes()
    {
        const string prefix = "OmlCli.recipes.";
        foreach (var resource in typeof(DocSearch).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix) && n.EndsWith(".jsonc")).Order(StringComparer.Ordinal))
        {
            var name = resource[prefix.Length..^6];
            var text = SpecDocs.Resource(resource);
            yield return new SearchDoc
            {
                Id = "recipe:" + name, Kind = "recipe", Title = name, Open = $"skill/recipes/{name}.jsonc", Names = [name],
                Body = RecipeHeader(text),
                Keys = string.Join(" ", Regex.Matches(text, @"""([A-Za-z][A-Za-z0-9]*)""\s*:").Select(m => m.Groups[1].Value)
                    .Concat(Regex.Matches(text, @"""(?:kind|op|type)""\s*:\s*""([A-Za-z][A-Za-z0-9 ]*)""").Select(m => m.Groups[1].Value))
                    .Distinct(StringComparer.Ordinal)),
            };
        }
    }

    // One entry per usage line of `oml --help` (CliUsage): commands that no doc topic describes, such as `oml image`, are
    // found too ("convert png to svg"). The title is `oml <command>`, the text the line's description, then its usage;
    // `oml <command> --help` prints the command's lines. Lines of one command share that command, so only the best one
    // is shown. A usage title instead scored 0.008 MRR higher on tune but flagged 3 more answerable queries as weak.
    static IEnumerable<SearchDoc> Commands() =>
        CliUsage.Text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("oml ")).Select(line =>
        {
            var command = line.Split(' ')[1];
            var described = Regex.Match(line, @"^(.+?)\s+\(([^()]+)\)$");
            return new SearchDoc
            {
                Id = "command:" + command, Kind = "command", Title = "oml " + command, Open = $"oml {command} --help", TitleIsName = false,
                Body = described.Success ? described.Groups[2].Value + "\n" + described.Groups[1].Value : line,
            };
        });

    // The comment lines before the JSON starts, without the slashes.
    internal static string RecipeHeader(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Split('\n').TakeWhile(l => l.TrimStart().StartsWith("//")).Select(l => l.TrimStart()[2..].Trim()));

    // The library's saved index (LibraryIndex.cs), built from the fetched files when it is missing or stale.
    internal static SearchIndex LibraryIndex(List<string> folders, List<string> notes) =>
        OmlCli.LibraryIndex.Get(folders, notes, (fs, ns) => fs.SelectMany(f => Folder(f, ns, LibraryFiles, LibraryBytes, "library")).ToList());

    const int MaxFiles = 2000, LibraryFiles = 10000;
    const long MaxBytes = 20 * 1024 * 1024, LibraryBytes = 64 * 1024 * 1024;

    // --in: the .md files of a folder (and its subfolders), read now and never stored. A hit opens the file's path.
    // Links and junctions (a loop), hidden and system entries and folders it may not read are skipped; at most 2,000
    // files and 20 MB are read, and a file with a NUL byte is not text. Each limit hit or file skipped adds a note.
    // --library: the same reader over each fetched source; a hit opens the file's full path (the library is outside the
    // project), and the kind is "library".
    static List<SearchDoc> Folder(string folder, List<string> notes, int maxFiles, long maxBytes, string kind)
    {
        var root = Path.GetFullPath(folder);
        var docs = new List<SearchDoc>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
        };
        long bytes = 0;
        var count = 0;
        var binary = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.md", options).Order(StringComparer.Ordinal))
        {
            var relative = kind == "library" ? file.Replace('\\', '/') : Path.GetRelativePath(Directory.GetCurrentDirectory(), file).Replace('\\', '/');
            var size = new FileInfo(file).Length;
            if (count == maxFiles || bytes + size > maxBytes)
            {
                notes.Add($"Stopped at {count} files and {bytes / 1024} KB: at most {maxFiles} files and {maxBytes / 1024 / 1024} MB are read. Search a smaller folder.");
                break;
            }
            var text = File.ReadAllText(file);
            count++;
            bytes += size;
            if (text.Contains('\0')) { binary.Add(relative); continue; }
            docs.AddRange(Markdown(text, relative, kind, slug => slug is null ? relative : $"{relative}#{slug}", rows: true));
        }
        if (binary.Count > 0)
            notes.Add($"Skipped {binary.Count} binary .md file(s): {Clip(string.Join(", ", binary.Take(5)), 300)}{(binary.Count > 5 ? ", ..." : "")}");
        return docs;
    }

    // Maintainer check (`oml doc --search-verify`, tests/search-check.ps1): every heading of every topic and of the spec
    // is in the index and found by its own title (top 3), every hit's command opens it, and every recipe has a header.
    public static int Verify()
    {
        var problems = new List<string>();
        var ids = Corpus.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var topic in DocCommand.Topics())
        {
            var fence = false;
            foreach (var line in DocCommand.Text(topic).Split('\n'))
            {
                if (line.TrimStart().StartsWith("```")) fence = !fence;
                if (fence || Regex.Match(line, @"^#{2,6} (.+?)\s*$") is not { Success: true } m) continue;
                var id = $"{topic}#{SpecDocs.Slug(m.Groups[1].Value)}";
                if (!ids.Contains(id)) problems.Add($"heading '{m.Groups[1].Value}' of {topic} is not in the search index");
            }
        }
        foreach (var s in SpecDocs.Sections.Where(s => s.Title != "Spec keys"))
            if (!ids.Contains("spec#" + s.Anchor)) problems.Add($"spec heading '{s.Title}' is not in the search index");
        foreach (var d in Corpus.Where(d => d.Parent is null && d.Kind is "doc" or "spec"))
        {
            // A table row of it, or any entry with the same command, counts: it opens the same text.
            var hits = Index.Search(d.Title, 3);
            var found = hits.Select(h => h.Doc.Id).ToList();
            if (!hits.Any(h => Answers(h.Doc, d.Id))) problems.Add($"'{d.Title}' ({d.Id}) is not in the top 3 for its own title: {string.Join(", ", found)}");
            if (!Opens(d)) problems.Add($"'{d.Open}' does not open {d.Id}");
        }
        foreach (var d in Corpus.Where(d => d.Kind == "recipe" && !Glossary(d)))
        {
            if (d.Body.Split('\n').Count(l => l.Length > 0) < 2) problems.Add($"recipe {d.Title} has no header comment of at least 2 lines");
            else if (!Index.Search(d.Body.Split('\n')[0], 3).Any(h => h.Doc == d)) problems.Add($"recipe {d.Title} is not in the top 3 for its first header line");
        }
        foreach (var d in Corpus.Where(d => (d.Kind is "warning" or "key" || d.Id.StartsWith("rules")) && !Glossary(d)))
            if (!Opens(d)) problems.Add($"'{d.Open}' does not open {d.Id}");
        // Exact names win: every spec key and every warning id, searched alone, gives an entry of that name first.
        foreach (var d in Corpus.Where(d => d.Kind is "warning" or "key" && !Glossary(d)))
            foreach (var n in d.Names)
                if (Index.Search(n, 1).FirstOrDefault()?.Doc is not { } first || !first.Names.Contains(n, StringComparer.OrdinalIgnoreCase))
                    problems.Add($"'{n}' alone does not give an entry named {n} first");
        // Synonym words: a stem of 3 letters or less from a longer word meets unrelated words (logged -> log), and a
        // stop word is never searched.
        foreach (var w in SearchText.SynonymWords())
        {
            var stem = SearchText.Stem(w);
            if (stem.Length < 4 && w.Length > 4)
                problems.Add($"synonym '{w}' stems to '{stem}', which unrelated words share");
            if (SearchText.IsStopWord(w)) problems.Add($"synonym '{w}' is a stop word");
        }
        foreach (var (concept, target, _, _) in SearchGlossary.Entries)
            if (!ids.Contains(target)) problems.Add($"glossary entry '{concept}' opens '{target}', which is not an id of the index");
        foreach (var p in problems) Console.WriteLine(p);
        if (problems.Count == 0) Console.WriteLine($"ok: {Corpus.Count} entries; every heading is indexed, found by its title and opened by its command; every recipe has a header.");
        return problems.Count == 0 ? 0 : 1;
    }

    // True when the hit's command prints the part it was found in.
    static bool Opens(SearchDoc d)
    {
        var output = new StringWriter();
        var (previous, previousError) = (Console.Out, Console.Error);
        Console.SetOut(output);
        Console.SetError(TextWriter.Null);
        int code;
        try
        {
            var args = Args(d.Open);
            code = args[1] switch
            {
                "doc" => DocCommand.Run(args[2..]),
                "spec" => SpecCommand.Run(args[2..]),
                "warn" => DocCommand.Warn(args[2..]),
                _ => 2,
            };
        }
        finally { Console.SetOut(previous); Console.SetError(previousError); }
        var text = output.ToString().Replace("\r\n", "\n");
        return code == 0 && d.Kind switch
        {
            "warning" => d.Names.Length == 0 || d.Names.All(n => text.Contains("`" + n + "`")),
            // The command prints a section, heading first: a key's own section, a row's section, or the entry itself.
            "key" => Heading(text) == Plain(d.Keys),
            _ => Heading(text) == (d.Parent is null ? d.Title : ById[d.Parent].Title),
        };
    }

    static bool Glossary(SearchDoc d) => d.Id.StartsWith("glossary:", StringComparison.Ordinal);

    static string Heading(string text) => Plain(Regex.Replace(text.Split('\n')[0], "^#+ ", ""));

    // The arguments of an open command: words, or one double-quoted text.
    static string[] Args(string command) =>
        Regex.Matches(command, "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();

    // A hit answers an expected id when it is that entry, a row of that section, the section that holds that row, or
    // any entry with the same command (the command prints the expected part too).
    static bool Answers(SearchDoc hit, string expected) =>
        hit.Id == expected || hit.Parent == expected || expected.StartsWith(hit.Id + "/", StringComparison.Ordinal)
        || ById.TryGetValue(expected, out var e) && hit.Open.Length > 0 && string.Equals(hit.Open, e.Open, StringComparison.OrdinalIgnoreCase);

    static Dictionary<string, SearchDoc>? byId;
    static Dictionary<string, SearchDoc> ById => byId ??= Corpus.ToDictionary(d => d.Id, StringComparer.Ordinal);

    // Maintainer view (`oml doc --search-terms "<text>"`): the normalized terms of a text, and the weighted terms a query
    // searches (its own terms, typo fixes from the doc index, synonyms). tests/search-check.ps1 checks normalization with it.
    public static int Terms(string[] args)
    {
        var text = string.Join(" ", args);
        var result = new JsonObject
        {
            ["terms"] = new JsonArray(SearchText.Terms(text).Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
            ["query"] = new JsonObject(Index.QueryTerms(text).OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(Math.Round(p.Value, 2))))),
        };
        TerminalOutput.PrintNode(result);
        return 0;
    }

    // Maintainer listing (`oml doc --search-stems [min]`): the stems that several words of the docs share, one line
    // each (`stem: word count, word count`), for a person to review; tests/search-stems.txt holds the reviewed list.
    public static int Stems(string[] args)
    {
        var min = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 3;
        foreach (var (term, words) in Index.Merges(min)) Console.WriteLine($"{term}: {string.Join(", ", words)}");
        return 0;
    }

    // Maintainer listing (`oml doc --search-list`): every entry's id, kind and title, for writing benchmark queries.
    public static int List()
    {
        foreach (var d in Corpus) Console.WriteLine($"{d.Id}	{d.Kind}	{d.Title}");
        return 0;
    }

    // Maintainer benchmark (`oml doc --search-bench <bench.json> [--mode M] [--set S]`, tests/search-check.ps1): for each
    // query, the rank of the first hit whose id is one of the expected ids (or a row of an expected section).
    public static int Bench(string[] args)
    {
        try { return RunBench(args); }
        // A bad option value, a missing file, or a bench file of the wrong shape: a usage error, not a crash.
        catch (Exception e) when (e is ArgumentException or FormatException or IndexOutOfRangeException or IOException or JsonException
                                      or InvalidOperationException or NullReferenceException)
        { return Fail($"Search benchmark: {e.Message}"); }
    }

    static int RunBench(string[] args)
    {
        if (args.Length == 0) return Fail("Usage: oml doc --search-bench <bench.json> [--mode full|synonyms|bm25f|bm25|grep] [--set <name>]... [--misses]");
        var options = new SearchOptions();
        var misses = args.Contains("--misses");
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--mode" && i + 1 < args.Length) options = SearchOptions.Mode(args[++i]);
            else if (args[i] == "--set" && i + 1 < args.Length)
            {
                // Tuning: --set TitleWeight=3 (any public number of SearchOptions; for a switch, 0 or 1).
                var (key, value) = (args[++i].Split('=')[0], double.Parse(args[i].Split('=')[1], System.Globalization.CultureInfo.InvariantCulture));
                var field = typeof(SearchOptions).GetField(key) ?? throw new ArgumentException($"No search option {key}");
                field.SetValue(options, field.FieldType == typeof(bool) ? value != 0 : value);
            }
        }
        var bench = JsonNode.Parse(File.ReadAllText(args[0]), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!;
        var build = System.Diagnostics.Stopwatch.StartNew();
        var engine = new SearchIndex(Corpus.Where(d => options.GlossaryDocs || !d.Id.StartsWith("glossary:")).ToList(), options); // the first use of Corpus reads the embedded files

        var buildMs = build.ElapsedMilliseconds;
        var ids = Corpus.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var results = new JsonArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // { "tune": [...], "held": [...], "blind2": [...], "blind3": [...], "blind4": [...] }: tune is for tuning; the others were written blind.
        foreach (var (set, q) in new[] { "tune", "held", "blind2", "blind3", "blind4" }.SelectMany(set => (bench[set]?.AsArray() ?? []).Select(q => (set, q))))
        {
            var query = q!["q"]!.GetValue<string>();
            var expect = q["expect"]?.AsArray().Select(e => e!.GetValue<string>()).ToArray() ?? [];
            foreach (var e in expect) if (!ids.Contains(e)) return Fail($"Benchmark query '{query}' expects '{e}', which is not an id of the index.");
            var hits = engine.Search(query, 10);
            var rank = hits.FindIndex(h => expect.Any(e => Answers(h.Doc, e))) + 1;
            var row = new JsonObject
            {
                ["q"] = query, ["type"] = q["type"]?.GetValue<string>() ?? "plain", ["set"] = set, ["answerable"] = expect.Length > 0, ["rank"] = rank,
                ["weak"] = engine.Weak(hits, query), // what `oml doc --search` reports as weak
                ["s1"] = hits.Count > 0 ? hits[0].Score : 0, ["s2"] = hits.Count > 1 ? hits[1].Score : 0,
                ["cov"] = hits.Count > 0 ? Math.Round(engine.Coverage(hits[0].Doc, query), 2) : 0,
            };

            if (misses && rank != 1) row["top"] = new JsonArray(hits.Take(3).Select(h => (JsonNode)JsonValue.Create(h.Doc.Id)!).ToArray());
            results.Add(row);
        }
        var output = new JsonObject { ["buildMs"] = buildMs, ["ms"] = watch.ElapsedMilliseconds, ["queries"] = results.Count, ["results"] = results };
        TerminalOutput.PrintNode(output);
        return 0;
    }
}
