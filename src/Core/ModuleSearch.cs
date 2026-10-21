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

    public static object Find(IESpace m, string query, int top)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var unreadable = new Unreadable();
        var (docs, elements) = Read(m);
        var index = new SearchIndex(docs, Options);
        var terms = index.QueryTerms(query).Keys.ToHashSet(StringComparer.Ordinal);
        var hits = index.Search(query, top).Select(h =>
        {
            var element = elements[h.Doc.Id];
            var fields = index.MatchedFields(h.Doc, query).ToList();
            var why = string.Join("; ", fields.Select(f => f == 0 ? FieldNames[f]
                : $"{FieldNames[f]}: {SearchText.Snippet(f == 1 ? h.Doc.Keys : h.Doc.Body, terms, true, 100)}"));
            return new
            {
                kind = h.Doc.Kind,
                name = h.Doc.Title.Length > 0 ? h.Doc.Title : null,
                path = Unreadable.Try(() => "path of " + h.Doc.Title, () => ElementPaths.PathTo(m, element), null),
                why,
                score = h.Score,
            };
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
