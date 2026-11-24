using OutSystems.Model;

namespace OmlCli;

// `oml find <module.oml> --text "<words>"`: this module's own elements ranked by the search engine of `oml doc --search`.
// Three fields: the element's name (strongest), its description, and the text inside it (expressions, SQL, labels,
// weakest). Each hit has the path that describe, tree and edits accept, and says which fields matched.
static class ModuleSearch
{
    // Code bodies can be long (JavaScript, SQL): a strong length normalization keeps them from outranking a matching name.
    internal static readonly SearchOptions Options = new() { TitleWeight = 4, KeysWeight = 2, BodyWeight = 0.5, BodyB = 0.9 };
    static readonly string[] FieldNames = ["name", "description or kind", "text"];

    // The top hits of one index, each with why it matched (the fields and a snippet).
    static List<(SearchHit hit, string why)> Rank(List<SearchDoc> docs, string query, int top)
    {
        var index = new SearchIndex(docs, Options);
        var terms = index.QueryTerms(query).Keys.ToHashSet(StringComparer.Ordinal);
        return index.Search(query, top).Select(h => (h, string.Join("; ", index.MatchedFields(h.Doc, query).Select(f => f == 0 ? FieldNames[f]
            : $"{FieldNames[f]}: {SearchText.Snippet(f == 1 ? h.Doc.Keys : h.Doc.Body, terms, true, 100)}")))).ToList();
    }

    // Several modules: one index over all their elements, so the scores compare across modules. Each module is read and
    // closed before the next (`open` gives it and its owner to dispose); a module that cannot be read is an error entry.
    public static object FindMany(List<string> files, Func<string, (IESpace module, IDisposable owner)> open, string query, int top)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var unreadable = new Unreadable();
        var docs = new List<SearchDoc>();
        var where = new Dictionary<string, (string module, string file, string? path)>(StringComparer.Ordinal);
        var errors = new List<object>();
        for (var i = 0; i < files.Count; i++)
        {
            try
            {
                var (m, owner) = open(files[i]);
                using (owner)
                {
                    var (found, elements) = Read(m);
                    foreach (var doc in found)
                    {
                        var element = elements[doc.Id];
                        var id = i + ":" + doc.Id;
                        where[id] = (m.Name, files[i], Unreadable.Try(() => "path of " + doc.Title, () => ElementPaths.PathTo(m, element), null));
                        doc.Id = id;
                        docs.Add(doc);
                    }
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { errors.Add(new { file = files[i], error = e.Message }); }
        }
        var hits = Rank(docs, query, top).Select(r => new
        {
            module = where[r.hit.Doc.Id].module,
            kind = r.hit.Doc.Kind,
            name = r.hit.Doc.Title.Length > 0 ? r.hit.Doc.Title : null,
            path = where[r.hit.Doc.Id].path,
            file = where[r.hit.Doc.Id].file,
            r.why,
            score = r.hit.Score,
        }).ToList();
        var result = System.Text.Json.JsonSerializer.SerializeToNode(new
        {
            query,
            hits,
            modules = files.Count - errors.Count,
            searched = docs.Count,
            ms = watch.ElapsedMilliseconds,
            errors = errors.Count > 0 ? errors : null,
            hint = hits.Count == 0 ? "No element matches. Try other words, or 'oml inspect --in <folder> --own' for the overview." : null,
        })!.AsObject();
        if (unreadable.Items() is { } items) result["unreadable"] = System.Text.Json.JsonSerializer.SerializeToNode(items);
        return result;
    }

    public static object Find(IESpace m, string query, int top)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var unreadable = new Unreadable();
        var (docs, elements) = Read(m);
        var hits = Rank(docs, query, top).Select(r => new
        {
            kind = r.hit.Doc.Kind,
            name = r.hit.Doc.Title.Length > 0 ? r.hit.Doc.Title : null,
            path = Unreadable.Try(() => "path of " + r.hit.Doc.Title, () => ElementPaths.PathTo(m, elements[r.hit.Doc.Id]), null),
            r.why,
            score = r.hit.Score,
        }).ToList();
        var result = System.Text.Json.JsonSerializer.SerializeToNode(new
        {
            query,
            hits,
            searched = docs.Count,
            ms = watch.ElapsedMilliseconds,
            hint = hits.Count == 0 ? "No element matches. Try other words, or 'oml inspect <module.oml> --own' for the overview." : null,
        })!.AsObject();
        if (unreadable.Items() is { } items) result["unreadable"] = System.Text.Json.JsonSerializer.SerializeToNode(items);
        return result;
    }

    // One document per named element or widget of this module (not of its references). Expressions and other unnamed
    // parts (arguments, assignments, filters) give their text to the nearest element above them. Generated elements
    // (entity actions, derived types) are left out: their names only repeat their entity's.
    internal static (List<SearchDoc> docs, Dictionary<string, IObjectSignature> elements) Read(IESpace m)
    {
        var docs = new List<SearchDoc>();
        var elements = new Dictionary<string, IObjectSignature>(StringComparer.Ordinal);
        var text = new Dictionary<SearchDoc, List<string>>();
        var seen = new HashSet<IObjectSignature>();
        var pending = new Stack<(IObjectSignature o, SearchDoc? owner)>();
        pending.Push(((IObjectSignature)m, null));
        while (pending.TryPop(out var item))
        {
            var children = Unreadable.Try(() => "children of " + Unreadable.Where(item.o), () => ElementPaths.Children(item.o).ToArray(), []);
            foreach (var c in children)
            {
                if (!seen.Add(c) || c is IReference) continue;
                if (c is IExpression e)
                {
                    if (item.owner is not null && Unreadable.Try(() => "text of " + Unreadable.Where(c), () => e.Text, null) is { Length: > 0 } t) text[item.owner].Add(t);
                    continue;
                }
                if (ReviewModel.Generated(c)) continue;
                var name = Model.NameOf(c);
                var widget = c is OutSystems.Model.UI.IWidgetSignature;
                if (name is null && !widget) { pending.Push((c, item.owner)); continue; }
                var kind = widget ? ElementPaths.Kind(c) : ReviewModel.Kind(c);
                var doc = new SearchDoc
                {
                    Id = c.GlobalKey.ToString()!, Kind = kind, Title = name ?? "", Open = "",
                    Keys = (Reflect.Get(c, "Description") as string ?? "") + "\n" + kind, Names = name is null ? [] : [name],
                };
                if (!elements.TryAdd(doc.Id, c)) continue;
                docs.Add(doc);
                text[doc] = new List<string>();
                // Text that is a property and not a child expression: a SQL statement, a script, a Text widget's text.
                foreach (var property in TextProperties)
                    if (Reflect.Property(c, property) is not null && Text(Reflect.Get(c, property)) is { Length: > 0 } t && t != name) text[doc].Add(t);
                pending.Push((c, doc));
            }
        }
        foreach (var doc in docs) doc.Body = string.Join("\n", text[doc].Distinct());
        return (docs, elements);
    }

    static readonly string[] TextProperties = ["Statement", "JavaScript", "Text", "Label"];
    static string? Text(object? value) => value is IExpression e ? e.Text : value as string;
}
