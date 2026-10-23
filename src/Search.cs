using System.Text;

namespace OmlCli;

// Local ranked search for `oml doc --search`, `oml spec --search`, `oml warn` and `oml find --text`: BM25F over three
// fields (title, keys, text) with light English normalization. The query adds typo fixes against the index's own words,
// synonyms of its rare words (a small OutSystems map), and concept links from the glossary (SearchGlossary.cs, whose
// entries are also small documents that point at a section); an exact key or title gets a grep-like boost, and a weak
// flag marks a poor best hit. No model, no files, no network: the index is built in memory, and equal scores sort by
// id, so the same query always gives the same answer.
// Compiled into the launcher and into Core (module search).
sealed class SearchDoc
{
    public required string Id, Kind, Title, Open;
    public string Keys = "";       // second field: keys, ids, related keys (module search: the description)
    public string Body = "";       // third field: the text (module search: expressions, SQL, labels)
    public string[] Names = [];    // exact names: a query equal to one of them (or to the title) gets the exact boost
    public string? Parent { get; set; } // the section a table row belongs to
    public bool TitleIsName { get; set; } = true; // false: the title is not an exact name (a glossary entry)
}

sealed record SearchHit(SearchDoc Doc, double Score, string Snippet);

// Which parts are on, and the weights. tests/search-check.ps1 compares the parts against each other.
sealed record SearchOptions
{
    public bool Normalize = true, Fields = true, Synonyms = true, Typos = true, Exact = true, Substring, GlossaryDocs = true;

    // Weights tuned on the tune set of tests/search-bench.json; later changes were cross-validated on the held, blind2 and
    // blind3 sets too, so only blind4 is blind (tests/search-check.ps1). Do not tune on blind4.
    public double TitleWeight = 5, KeysWeight = 1.5, BodyWeight = 1, K1 = 1.6, TitleB = 0.6, KeysB = 0.5, BodyB = 0.5;
    public double SynonymWeight = 0.4, TypoWeight = 0.7, ExactQuery = 25, ExactWord = 6;
    // Synonyms only for a query word with an idf of at least this: a word common in the docs has enough matches of its
    // own, and its synonyms mostly added noise (cross-validated on the tune, held and blind2 sets).
    public double SynonymMinIdf = 2.5;
    // Concept links (everyday words to OutSystems constructs, SearchText.Concepts); 0 turns them off.
    public double ConceptWeight = 0.2, ConceptMinWords = 2;
    // Weak result: the best hit holds less than WeakCoverage of the query's words and scores below WeakScore.
    public double WeakCoverage = 0.6, WeakScore = 5;

    public static SearchOptions Mode(string mode) => mode switch
    {
        "full" => new(),
        "typos" => new() { ConceptWeight = 0, GlossaryDocs = false },
        "synonyms" => new() { Typos = false, ConceptWeight = 0, GlossaryDocs = false },
        "bm25f" => new() { Typos = false, Synonyms = false, ConceptWeight = 0, GlossaryDocs = false },
        // Plain BM25: one field, textbook k1 and b, no normalization.
        "bm25" => new() { Normalize = false, Fields = false, Typos = false, Synonyms = false, Exact = false, TitleWeight = 1, K1 = 1.2, TitleB = 0.75, ConceptWeight = 0, GlossaryDocs = false },
        "grep" => new() { Substring = true, GlossaryDocs = false },
        _ => throw new ArgumentException($"Unknown search mode '{mode}'. Use full, typos, synonyms, bm25f, bm25 or grep."),
    };
}

static class SearchText
{
    // Common English words that say nothing about the topic. Short on purpose: the idf already discounts common words.
    static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "at", "by", "for", "with", "from", "into", "onto", "about",
        "is", "are", "was", "were", "be", "been", "being", "am", "it", "its", "this", "that", "these", "those", "there", "here",
        "i", "me", "my", "we", "our", "us", "you", "your", "he", "she", "they", "them", "their",
        "do", "does", "did", "doing", "done", "how", "what", "why", "when", "where", "which", "who", "whom",
        "can", "could", "should", "would", "will", "shall", "may", "might", "must", "so", "if", "then", "than",
        "not", "no", "some", "any", "very", "just", "also", "too", "please", "want", "need", "way", "get", "got",
        "e", "g", "eg", "ie", "etc",
    };

    // OutSystems words that mean the same thing to a person who asks. A query word also searches the other words of
    // its groups, at a lower weight. Every word is normalized like the text, so plurals and -ing forms match too.
    static readonly string[][] Groups =
    {
        ["popup", "modal", "dialog", "popover", "overlay"],
        ["button", "btn"],
        ["table", "list", "grid", "tablerecords"],
        ["fetch", "aggregate", "query", "load", "retrieve"],
        ["role", "permission", "access", "authorization", "authorize", "admin", "manager"],
        ["input", "parameter", "argument", "param", "arg"],
        ["event", "trigger", "handler", "triggerevent"],
        ["entity", "table", "record"],
        ["public", "expose", "exposed"],
        ["style", "css", "stylesheet", "theme", "class"],
        ["error", "exception", "fail", "failure", "crash", "raise"],
        ["validate", "validation", "check", "valid", "invalid"],
        ["timer", "schedule", "batch", "cron", "background", "async", "job", "nightly", "daily"],
        ["rest", "api", "endpoint", "http"],
        ["email", "mail"],
        ["block", "component", "webblock", "widget"],
        ["screen", "page"],
        ["delete", "remove"],
        ["confirm", "confirmation"],
        ["paging", "pagination"],
        ["search", "filter"],
        ["dropdown", "combo", "select"],
        ["static", "enum", "lookup"],
        ["javascript", "js", "script"],
        ["sql", "query"],
        ["flicker", "flash", "blink", "rerender"],
        ["slow", "performance", "fast", "speed"],
        ["login", "logged", "signed", "anonymous", "registered", "authenticate", "authentication"],
        ["secret", "password", "token", "credential"],
        ["upload", "binary", "file"],
        ["variable", "local"],
        ["action", "logic", "flow"],
        ["module", "espace"],
        ["reference", "dependency", "dependencies"],
        ["transaction", "commit", "rollback"],
        ["excel", "xlsx", "spreadsheet"],
        ["export", "download"],
        ["rename", "name"],
        ["save", "create", "update", "createorupdate", "persist", "crud"],
        ["test", "bdd"],
        ["mobile", "phone", "offline", "device"],
        ["deploy", "deployment", "lifetime", "publish"],
        ["index", "unique"],
        ["site", "siteproperty", "setting", "config", "configuration"],
        ["exception", "handler", "onexception"],
        ["layout", "responsive"],
        ["dark", "theme"],
        ["accessibility", "a11y", "aria"],
        ["image", "picture", "icon"],
        ["font", "woff2"],
        ["detail", "edit", "form"],
        ["link", "navigate", "navigation", "destination", "redirect"],
        ["loading", "spinner", "isdatafetched"],
        ["empty", "blankslate", "null"],
        ["chart", "graph"],
        ["date", "time"],
        ["user", "userid", "getuserid"],
    };

    static List<(string[] When, string[] Add)>? concepts;
    // Concept links from the glossary (SearchGlossary.cs): each everyday phrase as normalized terms (all of them must be
    // in a query), and the terms of its OutSystems concept that it adds. One-way: "loop" adds For Each, not the reverse.
    public static List<(string[] When, string[] Add)> Concepts => concepts ??= SearchGlossary.Entries
        .SelectMany(e => e.Everyday.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(phrase => (Terms(phrase).Distinct().ToArray(), Terms(e.Concept + " " + e.Adds).Distinct().ToArray())))
        .Where(c => c.Item1.Length > 0).ToList();

    static Dictionary<string, string[]>? synonyms;
    static Dictionary<string, string[]> Synonyms => synonyms ??= BuildSynonyms();

    static Dictionary<string, string[]> BuildSynonyms()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var group in Groups)
        {
            var terms = group.Select(w => Stem(w.ToLowerInvariant())).Distinct().ToArray();
            foreach (var t in terms)
            {
                if (!map.TryGetValue(t, out var set)) map[t] = set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var other in terms) if (other != t) set.Add(other);
            }
        }
        return map.ToDictionary(p => p.Key, p => p.Value.Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }

    // Every word of the synonym map, for the maintainer check (DocSearch.Verify).
    public static IEnumerable<string> SynonymWords() => Groups.SelectMany(g => g).Distinct();

    // The other words of a word's synonym groups (both normalized), or none.
    public static string[] SynonymsOf(string term) => Synonyms.TryGetValue(term, out var list) ? list : [];

    // Letters and digits only, lower case: the form exact matches compare.
    public static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // Words of a text: runs of letters and digits, with '.', '_', '#' and '-' kept between two of them (List.Current.Name, z-index).
    public static IEnumerable<string> Words(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            var inner = c is '.' or '_' or '#' or '-' && start >= 0 && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]);
            if (char.IsLetterOrDigit(c) || inner) { if (start < 0) start = i; continue; }
            if (start >= 0) { yield return text[start..i]; start = -1; }
        }
    }

    // Parts of one word: split at '.', '_', '#', '-', at a lower-to-upper change (onAfter), before the last capital of a
    // capital run followed by lower case (HTTPRequest: HTTP, Request), and between letters and digits.
    public static List<string> Parts(string word)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < word.Length; i++)
        {
            var c = word[i];
            if (c is '.' or '_' or '#' or '-') { Flush(); continue; }
            if (sb.Length > 0)
            {
                var p = word[i - 1];
                var split = char.IsUpper(c) && (char.IsLower(p) || char.IsDigit(p))
                            || char.IsUpper(c) && char.IsUpper(p) && i + 1 < word.Length && char.IsLower(word[i + 1])
                            || char.IsDigit(c) != char.IsDigit(p);
                if (split) Flush();
            }
            sb.Append(c);
        }
        Flush();
        return parts;
        void Flush() { if (sb.Length > 0) parts.Add(sb.ToString()); sb.Clear(); }
    }

    // Normalized terms of a text, in order. Normalize off: lower-case runs of letters and digits only (the plain baseline).
    public static List<string> Terms(string text, bool normalize = true) => Tokens(text, normalize).Select(t => t.Term).ToList();

    // The same, each with the lower-case word it came from (typo fixes compare words before stemming).
    public static List<(string Raw, string Term)> Tokens(string text, bool normalize = true)
    {
        var tokens = new List<(string, string)>();
        if (!normalize)
        {
            var sb = new StringBuilder();
            foreach (var c in text + " ")
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (sb.Length > 0) { tokens.Add((sb.ToString(), sb.ToString())); sb.Clear(); }
            return tokens;
        }
        foreach (var word in Words(text))
        {
            var parts = Parts(word);
            foreach (var part in parts)
            {
                var lower = part.ToLowerInvariant();
                if (!Stop.Contains(lower)) tokens.Add((lower, Stem(lower)));
            }
            // A word of several parts also keeps its joined form: OnAfterFetch is on, after, fetch and onafterfetch.
            if (parts.Count > 1) tokens.Add((Norm(word), Stem(Norm(word))));
        }
        return tokens;
    }

    public static bool IsStopWord(string word) => Stop.Contains(word);

    static bool Vowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    // Light English stemming: plurals, -ing and -ed, and a final e, so that page, pages and paging meet. Not a full
    // stemmer: it leaves validation and validate apart (the synonym map joins such words).
    public static string Stem(string w)
    {
        if (w.Length <= 3 || !w.All(char.IsAsciiLetterLower)) return w;
        if (Fixed.TryGetValue(w, out var stem)) return stem;
        if (w.EndsWith("ies") && w.Length > 4) w = w[..^3] + "y";
        else if (w.EndsWith("sses")) w = w[..^2];
        else if (w.EndsWith("xes") || w.EndsWith("ches") || w.EndsWith("shes") || w.EndsWith("zes")) w = w[..^2];
        else if (w.EndsWith('s') && !w.EndsWith("ss") && !w.EndsWith("us") && !w.EndsWith("is")) w = w[..^1];
        if (w.EndsWith("ely") || w.EndsWith("ally")) w = w[..^2];        // securely, manually
        else if (w.EndsWith("able") && w.Length > 6) w = w[..^4];       // callable, reusable
        if (w.EndsWith("ing") && w.Length >= 6) w = Undouble(w[..^3]);
        else if (w.EndsWith("ed") && w.Length >= 5 && !w.EndsWith("eed")) w = Undouble(w[..^2]);
        if (w.EndsWith('e') && w.Length > 3) w = w[..^1];
        return w;
    }

    // Words whose -ed or -ing must stay, or they would meet an unrelated word: logged is not log, setting is not set,
    // typing is not type. DocSearch.Verify fails on a synonym word whose stem is that short.
    static readonly Dictionary<string, string> Fixed = new(StringComparer.Ordinal)
    {
        ["logged"] = "logged", ["setting"] = "setting", ["settings"] = "setting", ["typing"] = "typing",
        // Found with `oml doc --search-stems` (tests/search-stems.txt): page is not paging, a date is not a .dat file,
        // a locale is not a local, a site is not sit, none is not non, a note is not the word not, bare is not bar.
        ["paging"] = "paging", ["date"] = "date", ["dates"] = "date", ["locale"] = "locale", ["locales"] = "locale",
        ["site"] = "site", ["sites"] = "site", ["none"] = "none", ["note"] = "note", ["notes"] = "note", ["bare"] = "bare",
    };

    // running -> runn -> run; but call, pass and buzz keep their double letter.
    static string Undouble(string w) =>
        w.Length > 2 && w[^1] == w[^2] && !Vowel(w[^1]) && w[^1] is not ('l' or 's' or 'z') ? w[..^1] : w;

    // Optimal string alignment distance (swapped neighbours count as one edit). Stops early above max.
    public static int Distance(string a, string b, int max = int.MaxValue)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var best = int.MaxValue;
            for (var j = 1; j <= b.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                best = Math.Min(best, d[i, j]);
            }
            if (best > max) return max + 1;
        }
        return d[a.Length, b.Length];
    }

    // One line of text for a hit: the line with the most query terms, markdown marks removed, cut to about max
    // characters around the first matching word.
    public static string Snippet(string body, IReadOnlyCollection<string> terms, bool normalize, int max = 160)
    {
        var lines = body.Split('\n').Select(Clean).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return "";
        var best = lines.Select((line, i) => (line, i, n: Terms(line, normalize).Distinct().Count(terms.Contains)))
            .OrderByDescending(x => x.n).ThenBy(x => x.i).First();
        var text = best.line;
        if (text.Length <= max) return text;
        // Start a little before the first word that matches, at a word boundary; end at a word boundary.
        int at = 0, pos = 0;
        if (best.n > 0)
            foreach (var word in Words(text))
            {
                pos = text.IndexOf(word, pos, StringComparison.Ordinal);
                if (Terms(word, normalize).Any(terms.Contains)) { at = pos; break; }
                pos += word.Length;
            }
        var from = at <= max / 3 ? 0 : text.LastIndexOf(' ', at - max / 3) + 1;
        var end = Math.Min(text.Length, from + max - 6);
        if (end < text.Length && text.LastIndexOf(' ', end - 1, end - from) is var space && space > from + max / 2) end = space;
        return (from > 0 ? "..." : "") + text[from..end].Trim() + (end < text.Length ? "..." : "");
    }

    static string Clean(string line)
    {
        var t = line.Trim();
        if (t.StartsWith("|---") || t.StartsWith("```") || t.StartsWith("//")) t = t.TrimStart('/').Trim();
        if (t.StartsWith("|---") || t.StartsWith("```")) return "";
        t = t.TrimStart('#', '-', '*', '>', ' ').Replace("**", "").Replace(" | ", "; ").Trim('|', ' ');
        return System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ");
    }
}

// BM25F (Robertson's simple form): per term, the field frequencies are length-normalized per field, weighted, summed,
// then saturated once with k1. idf over documents.
sealed class SearchIndex
{
    readonly IReadOnlyList<SearchDoc> docs;
    readonly SearchOptions options;
    readonly Dictionary<string, int>[][] tf;   // [doc][field] term -> count
    readonly int[][] length;                   // [doc][field]
    readonly double[] average = new double[3];
    readonly Dictionary<string, List<int>> postings = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> words = new(StringComparer.Ordinal);   // lower-case word -> its term, for typo fixes
    readonly Dictionary<string, int> wordCounts = new(StringComparer.Ordinal); // lower-case word -> times it occurs
    readonly Dictionary<string, List<int>> exact = new(StringComparer.Ordinal); // Norm(name or title), and its stem -> docs
    readonly string[] lowered;                 // substring baseline: all text, lower case

    public SearchIndex(IReadOnlyList<SearchDoc> docs, SearchOptions? options = null)
    {
        this.docs = docs;
        this.options = options ?? new SearchOptions();
        tf = new Dictionary<string, int>[docs.Count][];
        length = new int[docs.Count][];
        lowered = new string[docs.Count];
        for (var d = 0; d < docs.Count; d++)
        {
            var doc = docs[d];
            var fields = this.options.Fields ? new[] { doc.Title, doc.Keys, doc.Body } : new[] { doc.Title + "\n" + doc.Keys + "\n" + doc.Body, "", "" };
            tf[d] = new Dictionary<string, int>[3];
            length[d] = new int[3];
            for (var f = 0; f < 3; f++)
            {
                var tokens = SearchText.Tokens(fields[f], this.options.Normalize);
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var (raw, t) in tokens)
                {
                    counts[t] = counts.GetValueOrDefault(t) + 1;
                    words.TryAdd(raw, t);
                    wordCounts[raw] = wordCounts.GetValueOrDefault(raw) + 1;
                }
                tf[d][f] = counts;
                length[d][f] = tokens.Count;
            }
            Link(d);
            if (this.options.Substring) lowered[d] = (doc.Title + "\n" + doc.Keys + "\n" + doc.Body + "\n" + string.Join(" ", doc.Names)).ToLowerInvariant();
        }
        for (var f = 0; f < 3; f++) average[f] = docs.Count == 0 ? 1 : Math.Max(1, average[f] / docs.Count);
    }

    // A saved index (LibraryIndex): the counts the constructor above makes, read back without tokenizing the text again.
    internal SearchIndex(IReadOnlyList<SearchDoc> docs, Dictionary<string, int>[][] tf, int[][] length, Dictionary<string, string> words, Dictionary<string, int> wordCounts)
    {
        this.docs = docs;
        options = new SearchOptions();
        this.tf = tf;
        this.length = length;
        lowered = new string[docs.Count];
        foreach (var (k, v) in words) this.words[k] = v;
        foreach (var (k, v) in wordCounts) this.wordCounts[k] = v;
        for (var d = 0; d < docs.Count; d++) Link(d);
        for (var f = 0; f < 3; f++) average[f] = docs.Count == 0 ? 1 : Math.Max(1, average[f] / docs.Count);
    }

    internal IReadOnlyList<SearchDoc> Docs => docs;
    internal Dictionary<string, int>[][] Tf => tf;
    internal int[][] Lengths => length;
    internal Dictionary<string, string> Words => words;
    internal Dictionary<string, int> WordCounts => wordCounts;

    // A document's postings, field lengths and exact names, from its counts.
    void Link(int d)
    {
        for (var f = 0; f < 3; f++)
        {
            average[f] += length[d][f];
            foreach (var t in tf[d][f].Keys)
            {
                if (!postings.TryGetValue(t, out var list)) postings[t] = list = new List<int>();
                if (list.Count == 0 || list[^1] != d) list.Add(d);
            }
        }
        var doc = docs[d];
        foreach (var name in (doc.TitleIsName ? doc.Names.Append(doc.Title) : doc.Names).Select(SearchText.Norm).Where(n => n.Length > 0)
                     .SelectMany(n => new[] { n, SearchText.Stem(n) }).Distinct())
        {
            if (!exact.TryGetValue(name, out var list)) exact[name] = list = new List<int>();
            list.Add(d);
        }
    }

    public int Count => docs.Count;

    // The query's own terms, each with the terms it stands for: itself, plus its typo fixes when the index lacks it.
    List<(string Term, string[] Matches)> Own(string query)
    {
        var own = new List<(string, string[])>();
        // shortcut: a query is cut to its first 64 terms; nobody types more into a search.
        foreach (var (raw, term) in SearchText.Tokens(query, options.Normalize).DistinctBy(t => t.Term).Take(64))
        {
            // A word of the synonym map is a known word (modal), not a typo, even when the index lacks it.
            var typo = options.Typos && !postings.ContainsKey(term) && raw.Length >= 4 && raw.All(char.IsLetter) && SearchText.SynonymsOf(term).Length == 0;
            own.Add((term, typo ? [term, .. Corrections(raw)] : [term]));
        }
        return own;
    }

    // The query terms with their weights: the query's own terms (1), typo fixes for words the index does not have, and
    // synonyms of both (lower weights). A term keeps its highest weight.
    public Dictionary<string, double> QueryTerms(string query)
    {
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        void Add(string t, double w) { if (w > weights.GetValueOrDefault(t)) weights[t] = w; }
        var own = Own(query);
        foreach (var (term, matches) in own)
        {
            Add(term, 1);
            foreach (var fix in matches.Skip(1)) Add(fix, options.TypoWeight);
        }
        if (options.Synonyms)
            foreach (var (t, w) in weights.ToList())
                if (!postings.ContainsKey(t) || Idf(t) >= options.SynonymMinIdf) // a common word has enough matches of its own
                    foreach (var s in SearchText.SynonymsOf(t)) Add(s, w * options.SynonymWeight);
        if (options.ConceptWeight > 0)
        {
            var have = own.Select(o => o.Term).ToHashSet(StringComparer.Ordinal);
            foreach (var (when, add) in SearchText.Concepts.Where(c => c.When.Length >= options.ConceptMinWords && c.When.All(have.Contains)))
                foreach (var t in add) Add(t, options.ConceptWeight);
        }
        return weights;
    }

    // idf of a term; a term the index lacks has the highest idf.
    double Idf(string term)
    {
        var df = postings.TryGetValue(term, out var list) ? list.Count : 0;
        return Math.Log(1 + (docs.Count - df + 0.5) / (df + 0.5));
    }

    // Typo fixes: the terms of the index words within one edit of the word as typed, compared before stemming (two
    // edits for words of 8 letters or more); the closest, at most three.
    IEnumerable<string> Corrections(string raw)
    {
        var max = raw.Length >= 8 ? 2 : 1;
        return words.Keys.Where(k => k.Length >= 3 && Math.Abs(k.Length - raw.Length) <= max && k.All(char.IsLetter))
            .Select(k => (term: words[k], d: SearchText.Distance(k, raw, max))).Where(x => x.d <= max)
            .GroupBy(x => x.term).Select(g => (term: g.Key, d: g.Min(x => x.d)))
            .OrderBy(x => x.d).ThenByDescending(x => postings[x.term].Count).ThenBy(x => x.term, StringComparer.Ordinal).Take(3).Select(x => x.term);
    }

    // Exact matches come first, in this order of kinds: a spec key, a warning, a spec section, a recipe, a doc section.
    static int KindRank(string kind) => kind switch { "key" => 5, "warning" => 4, "spec" => 3, "recipe" => 2, _ => 1 };

    public List<SearchHit> Search(string query, int top, Func<SearchDoc, bool>? filter = null)
    {
        var terms = QueryTerms(query);
        var scores = new Dictionary<int, double>();
        var tier = new Dictionary<int, int>();
        if (options.Substring)
        {
            var parts = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (var d = 0; d < docs.Count; d++)
                if (parts.Count(w => lowered[d].Contains(w)) is var n and > 0) scores[d] = n;
        }
        else
        {
            double[] weight = [options.TitleWeight, options.KeysWeight, options.BodyWeight], b = [options.TitleB, options.KeysB, options.BodyB];
            foreach (var (term, wq) in terms)
            {
                if (!postings.TryGetValue(term, out var list)) continue;
                var idf = Math.Log(1 + (docs.Count - list.Count + 0.5) / (list.Count + 0.5));
                foreach (var d in list)
                {
                    double x = 0;
                    for (var f = 0; f < 3; f++)
                        if (tf[d][f].TryGetValue(term, out var n))
                            x += weight[f] * n / (1 - b[f] + b[f] * length[d][f] / average[f]);
                    scores[d] = scores.GetValueOrDefault(d) + wq * idf * x / (options.K1 + x);
                }
            }
            if (options.Exact) AddExact(query, scores, tier);
        }
        var ranked = scores.Where(s => (s.Value > 0 || tier.ContainsKey(s.Key)) && (filter is null || filter(docs[s.Key])))
            .OrderByDescending(s => tier.GetValueOrDefault(s.Key)).ThenByDescending(s => s.Value).ThenBy(s => docs[s.Key].Id, StringComparer.Ordinal)
            .Select(s => (doc: docs[s.Key], score: s.Value)).ToList();
        // Each hit is a different place to open: a hit that opens the same command as a better one, or a section whose
        // table row ranks above it, is left out.
        var shown = new List<(SearchDoc doc, double score)>();
        foreach (var hit in ranked)
        {
            if (shown.Any(s => s.doc.Parent == hit.doc.Id
                               || hit.doc.Open.Length > 0 && string.Equals(s.doc.Open, hit.doc.Open, StringComparison.OrdinalIgnoreCase))) continue;
            shown.Add(hit);
            if (shown.Count == top) break;
        }
        var matched = terms.Keys.ToHashSet(StringComparer.Ordinal);
        return shown.Select(s => new SearchHit(s.doc, Math.Round(s.score, 2), SearchText.Snippet(s.doc.Body.Length > 0 ? s.doc.Body : s.doc.Keys, matched, options.Normalize))).ToList();
    }

    // A result to treat with care: nothing found, or a best hit that holds less than WeakCoverage of the query's words with a
    // low score (an exact name is never weak). Calibrated on the pooled benchmark sets to flag at most about 10% of the
    // answerable queries; it flags about half of the unanswerable ones, which often share all their words with the docs.
    public bool Weak(IReadOnlyList<SearchHit> hits, string query) =>
        hits.Count == 0 || hits[0].Score < options.WeakScore && Coverage(hits[0].Doc, query) < options.WeakCoverage;

    // Grep-like: the whole query equal to a name or title (any case, singular or plural) is ranked first, by kind; an
    // identifier in a longer query (a word with a capital after its first letter, or with '.' or '_': replaceWidgets,
    // List.Current) equal to a name lifts that document.
    void AddExact(string query, Dictionary<int, double> scores, Dictionary<int, int> tier)
    {
        var q = SearchText.Norm(query);
        foreach (var key in new[] { q, SearchText.Stem(q) }.Distinct())
            if (key.Length > 0 && exact.TryGetValue(key, out var whole))
                foreach (var d in whole)
                {
                    if (tier.ContainsKey(d)) continue;
                    tier[d] = KindRank(docs[d].Kind);
                    scores[d] = scores.GetValueOrDefault(d) + options.ExactQuery;
                }
        var parts = SearchText.Words(query).ToList();
        if (parts.Count < 2) return;
        foreach (var w in parts.Where(w => w.Skip(1).Any(char.IsUpper) || w.Contains('.') || w.Contains('_')).Select(SearchText.Norm).Distinct())
            if (exact.TryGetValue(w, out var list))
                foreach (var d in list) scores[d] = scores.GetValueOrDefault(d) + options.ExactWord;
    }

    // Share of the query's own words (or their typo fixes) that a document holds: 1 when it has them all. Synonyms do
    // not count. `oml warn` lists a close id only when it holds at least half.
    public double Coverage(SearchDoc doc, string query)
    {
        var d = IndexOf(doc);
        var own = Own(query);
        if (own.Count == 0) return 0;
        return own.Count(o => o.Matches.Any(t => tf[d].Any(field => field.ContainsKey(t)))) / (double)own.Count;
    }



    // Stems that several different words of the index share, each word with its count: the stemmer's merges, for a
    // person to review (tests/search-stems.txt). Words seen fewer than `min` times, and plain letters or digits, are left out.
    public IEnumerable<(string Term, string[] Words)> Merges(int min) =>
        words.Where(w => wordCounts[w.Key] >= min && w.Key.All(char.IsAsciiLetterLower))
            .GroupBy(w => w.Value).Where(g => g.Count() > 1).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Select(w => $"{w.Key} {wordCounts[w.Key]}").Order(StringComparer.Ordinal).ToArray()));

    // The fields (0 title, 1 keys, 2 body) in which a query matched one document.

    public IEnumerable<int> MatchedFields(SearchDoc doc, string query)
    {
        var d = IndexOf(doc);
        var terms = QueryTerms(query).Keys.ToList();
        for (var f = 0; f < 3; f++) if (terms.Any(tf[d][f].ContainsKey)) yield return f;
    }

    int IndexOf(SearchDoc doc)
    {
        for (var i = 0; i < docs.Count; i++) if (ReferenceEquals(docs[i], doc)) return i;
        throw new ArgumentException("not a document of this index");
    }
}
