using System.Text;

namespace OmlCli;

// Text-level work on OutSystems expressions that must not touch string literals ("..."), dates (#...#), comments or members of
// another path. No model types: used by the rewrite edit and testable on its own.
public static class ExpressionText
{
    // Each whole occurrence of the identifier path `from` ("Session.UserName") becomes `to`. A longer path that starts with it keeps its
    // tail ("Session.UserName.Length" -> "UserName.Length"); "XSession.UserName", "a.Session.UserName" and "Session.UserNames" are not
    // occurrences.
    public static string ReplacePath(string text, string from, string to)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(from) || !text.Contains(from.Split('.')[^1], StringComparison.Ordinal)) return text;
        // An expression that is not a plain path keeps its own precedence inside the text it goes into.
        if (!System.Text.RegularExpressions.Regex.IsMatch(to.Trim(), @"^[A-Za-z_][\w.]*(\(\))?$|^""([^""]|"""")*""$|^\d+(\.\d+)?$")) to = "(" + to.Trim() + ")";
        var sb = new StringBuilder();
        int i = 0, n = text.Length;
        var last = '\0';
        while (i < n)
        {
            var c = text[i];
            int j;
            if (c == '"')
            {
                j = i + 1;
                while (j < n) { if (text[j] == '"') { if (j + 1 < n && text[j + 1] == '"') { j += 2; continue; } break; } j++; }
                j = Math.Min(j + 1, n);
            }
            else if (c == '#') { j = text.IndexOf('#', i + 1); j = j < 0 ? n : j + 1; }
            else if (c == '/' && i + 1 < n && text[i + 1] == '/') { j = text.IndexOf('\n', i); if (j < 0) j = n; }
            else if (c == '/' && i + 1 < n && text[i + 1] == '*') { j = text.IndexOf("*/", i + 2, StringComparison.Ordinal); j = j < 0 ? n : j + 2; }
            else if (char.IsLetter(c) || c == '_')
            {
                // A whole path: identifiers joined by dots (spaces around a dot are not written by Service Studio).
                j = i;
                while (j < n && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                while (j + 1 < n && text[j] == '.' && (char.IsLetter(text[j + 1]) || text[j + 1] == '_'))
                { j++; while (j < n && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++; }
                var path = text[i..j];
                if (last != '.' && (path == from || path.StartsWith(from + ".", StringComparison.Ordinal)))
                {
                    sb.Append(to).Append(path, from.Length, path.Length - from.Length);
                    last = path[^1]; i = j; continue;
                }
            }
            else j = i + 1;
            sb.Append(text, i, j - i);
            if (!char.IsWhiteSpace(text[j - 1])) last = text[j - 1];
            i = j;
        }
        return sb.ToString();
    }
}
