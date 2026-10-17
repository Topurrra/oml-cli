using System.Text;
using System.Text.Json.Nodes;

namespace OmlCli;

// Comments in stylesheets and scripts end up inside the module and cost tokens every time an agent reads them.
// Remove them when a spec is read; keep "/*!" comments, the usual marker for license notices.
static class Minify
{
    static readonly string[] CodeKeys = { "styleSheet", "javaScript" };

    /// Strips comments from every styleSheet/javaScript string in the spec. Returns the characters removed.
    public static int Spec(JsonNode? node)
    {
        var removed = 0;
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var text)
                        && CodeKeys.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)))
                    {
                        var clean = key.Equals("javaScript", StringComparison.OrdinalIgnoreCase) ? Js(text) : Css(text);
                        removed += text.Length - clean.Length;
                        obj[key] = clean;
                    }
                    else removed += Spec(value);
                }
                break;
            case JsonArray array:
                foreach (var item in array) removed += Spec(item);
                break;
        }
        return removed;
    }

    public static string Css(string css) => Strip(css, lineComments: false);

    // Only block comments and whole-line "//" comments: a "//" after code can be a URL or a regex.
    public static string Js(string js) => Strip(js, lineComments: true);

    static string Strip(string text, bool lineComments)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        char quote = '\0';
        var lineStart = true;
        while (i < text.Length)
        {
            var c = text[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < text.Length) { sb.Append(text[++i]); }
                else if (c == quote) quote = '\0';
                i++;
                continue;
            }
            if (c is '"' or '\'' or '`') { quote = c; sb.Append(c); i++; lineStart = false; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                if (i + 2 < text.Length && text[i + 2] == '!') sb.Append(text, i, end - i); // license notice
                i = end;
                continue;
            }
            if (lineComments && lineStart && c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '\n') lineStart = true;
            else if (!char.IsWhiteSpace(c)) lineStart = false;
            sb.Append(c);
            i++;
        }
        // Drop the blank lines the removed comments leave behind.
        var lines = sb.ToString().Split('\n').Select(l => l.TrimEnd('\r', ' ', '\t')).Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }
}
