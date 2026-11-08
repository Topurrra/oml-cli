using System.Text;
using OutSystems.Model;

namespace OmlCli;

// `oml publish <file>` while a module of the same name is open in Service Studio. Service Studio's MCP cannot load a
// file into an open module (omlMerge takes only files of its own edit sessions, by hash). When the file differs from
// the open module only in plain texts (theme style sheets, script JavaScript), oml sets those texts in the open module
// through an edit session (applyModelApiCode, then omlMerge) and publishes it. The decisions are here; Studio.cs calls.
static class StudioPush
{
    // One text to set: eSpace.<Collection>.Named(<Name>).<Property> = <Text>.
    internal sealed record TextEdit(string Collection, string Kind, string Name, string Property, string Text);

    static readonly (string kind, string collection, string property)[] Texts =
    [
        ("theme", "MobileThemes", "StyleSheet"),
        ("script", "Scripts", "JavaScript"),
    ];

    // The texts to set in the open module, and every other difference (any one of them means the file cannot be pushed).
    internal static (List<TextEdit> edits, List<string> other) Differences(IESpace open, IESpace file)
    {
        var (a, b) = (ReviewModel.Read(open), ReviewModel.Read(file));
        if (a.Key != b.Key) return ([], [$"the file is another module ({b.Module}) than the open one ({a.Module})"]);
        var changes = Review.Compare(a, b, new Dictionary<string, int>());
        var other = new List<string>();
        foreach (var c in changes)
        {
            var pushable = c.What == "changed" && Texts.Any(t => t.kind == c.Kind);
            if (!pushable) { other.Add(c.What == "changed" ? $"{c.Kind} {c.Name}: {string.Join("; ", c.Details.Take(2))}" : $"{c.Kind} {c.Name} {c.What}"); continue; }
            foreach (var d in c.Details.Where(d => !IsTextDetail(d))) other.Add($"{c.Kind} {c.Name}: {d}");
        }
        var edits = new List<TextEdit>();
        if (WebUi.IsTraditional(file))
        {
            if (changes.Count > 0) other.Add("a Traditional Web module: only Reactive and Mobile modules are changed this way");
            return (edits, other);
        }
        foreach (var (kind, collection, property) in Texts)
            foreach (var now in Content.Items(file, collection))
            {
                var name = Model.NameOf(now);
                if (Content.Items(open, collection).FirstOrDefault(x => Model.NameOf(x) == name) is not { } was) continue; // an added one is in `other`
                var text = Reflect.Get(now, property) as string ?? "";
                if ((Reflect.Get(was, property) as string ?? "") != text) edits.Add(new(collection, kind, name, property, text));
            }
        return (edits, other);
    }

    // Review's detail lines for a text property: "style sheet changed (...)", "JavaScript added (...)".
    static bool IsTextDetail(string detail) =>
        detail.StartsWith("style sheet ", StringComparison.Ordinal) || detail.StartsWith("JavaScript ", StringComparison.Ordinal);

    // Model API code that sets each whole text. Whole texts, not Replace anchors: an anchor with a line ending that
    // differs in the open module silently matches nothing. Service Studio refuses code that names `System`, so the
    // code uses no namespaces beyond OutSystems.Model and the `.Named` lookup.
    internal static string Code(IEnumerable<TextEdit> edits)
    {
        var code = new StringBuilder("eSpace => {\n");
        foreach (var e in edits) code.Append($"    eSpace.{e.Collection}.Named({Literal(e.Name)}).{e.Property} = {Literal(e.Text)};\n");
        return code.Append('}').ToString();
    }

    static string Literal(string text) => "@\"" + text.Replace("\"", "\"\"") + "\"";

    // After the edit: the edit-session file must hold exactly the file's texts; returns the ones that differ.
    internal static List<string> Mismatches(IESpace edited, IEnumerable<TextEdit> edits) =>
        edits.Where(e => Content.Items(edited, e.Collection).FirstOrDefault(x => Model.NameOf(x) == e.Name) is not { } item
                         || (Reflect.Get(item, e.Property) as string ?? "") != e.Text)
             .Select(e => $"{e.Kind} {e.Name}").ToList();

    // The refusal when the file differs in more than texts.
    internal static string Refusal(string module, List<string> other) =>
        $"'{module}' is open in Service Studio and this file differs from the open copy in more than theme style sheets and scripts " +
        $"({other.Count} difference{(other.Count == 1 ? "" : "s")}: {string.Join("; ", other.Take(3))}{(other.Count > 3 ? "; ..." : "")}). " +
        $"Service Studio's MCP cannot load a file into an open module. Close '{module}' in Service Studio and run again (oml then opens the file), " +
        $"or run 'oml publish {module}' to publish the open copy as it is.";

    // Service Studio's publish messages say "Upload Skipped" when the server already has this version: nothing new deployed.
    internal static bool UploadSkipped(System.Text.Json.Nodes.JsonNode? result) =>
        result?["messages"] is System.Text.Json.Nodes.JsonArray messages
        && messages.Any(m => m is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && s.Contains("skipped", StringComparison.OrdinalIgnoreCase));
}
