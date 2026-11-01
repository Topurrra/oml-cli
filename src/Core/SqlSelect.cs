using System.Text;
using System.Text.RegularExpressions;

namespace OmlCli;

// The column list of a SQL query's first SELECT, for the check that SELECT columns follow the outputs' attribute order
// (rule sql-column-order). No model types. Each column is its name (the alias, else the last identifier of a plain column
// reference), or null when it has none, such as COUNT(1). {Entity}.* expands through the caller's lookup. null: the
// statement cannot be read this way (no SELECT first, a bare *, or {X}.* for X not among the outputs), so nothing is checked.
static class SqlSelect
{
    static readonly Regex Lead = new(@"^\s*(?:(?:DISTINCT|ALL)\s+)?(?:TOP\s*(?:\(\s*[^()]*\)|\d+)(?:\s+PERCENT)?(?:\s+WITH\s+TIES)?\s+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex EntityStar = new(@"^\{\s*([^{}]+?)\s*\}\s*\.\s*\*$", RegexOptions.CultureInvariant);
    static readonly Regex Alias = new(@"(?:\bAS\s+|\s)(\[[^\]]+\]|""[^""]+""|[A-Za-z_]\w*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Column = new(@"^(?:(?:\{[^{}]+\}|\[[^\]]+\]|[A-Za-z_]\w*)\s*\.\s*)*(\[[^\]]+\]|[A-Za-z_]\w*)$", RegexOptions.CultureInvariant);
    static readonly HashSet<string> NotAliases = new(StringComparer.OrdinalIgnoreCase) { "END", "NULL", "ASC", "DESC" };

    public static List<string?>? Columns(string statement, Func<string, List<string>?> entityColumns)
    {
        var text = StripComments(statement).Trim();
        if (!Regex.IsMatch(text, @"^SELECT\b", RegexOptions.IgnoreCase)) return null;
        var list = Lead.Replace(SelectList(text[6..]), "", 1);
        var columns = new List<string?>();
        foreach (var raw in SplitTop(list))
        {
            var item = raw.Trim();
            if (item.Length == 0) return null;
            if (item == "*" || item.EndsWith(".*") && EntityStar.Match(item) is not { Success: true }) return null;
            if (EntityStar.Match(item) is { Success: true } star)
            {
                if (entityColumns(star.Groups[1].Value) is not { } names) return null;
                columns.AddRange(names);
                continue;
            }
            columns.Add(NameOf(item));
        }
        return columns;
    }

    static string? NameOf(string item)
    {
        if (Column.Match(item) is { Success: true } plain) return Unquote(plain.Groups[1].Value);
        // An alias follows the expression at the top level: "x AS Name", "x Name", "x [Name]" (not the END of a CASE).
        if (Alias.Match(item) is not { Success: true } alias || NotAliases.Contains(alias.Groups[1].Value) || !Depth0(item, alias.Index)) return null;
        // Without AS, the word must follow an operand: in "a + b", b is not an alias.
        var before = item[..alias.Index].TrimEnd();
        if (!alias.Value.TrimStart().StartsWith("AS", StringComparison.OrdinalIgnoreCase) && (before.Length == 0 || "+-*/%=<>|&,(".Contains(before[^1]))) return null;
        return Unquote(alias.Groups[1].Value);
    }

    static string Unquote(string name) => name.Length > 1 && (name[0] == '[' || name[0] == '"') ? name[1..^1] : name;

    static bool Depth0(string text, int index)
    {
        var depth = 0;
        foreach (var (c, _) in Scan(text[..index])) depth += c == '(' ? 1 : c == ')' ? -1 : 0;
        return depth == 0;
    }

    // Text up to the first FROM at the top level (outside parentheses, quotes, brackets and braces).
    static string SelectList(string text)
    {
        var depth = 0;
        foreach (var (c, i) in Scan(text))
        {
            depth += c == '(' ? 1 : c == ')' ? -1 : 0;
            if (depth == 0 && (c == 'F' || c == 'f') && (i == 0 || !IsWord(text[i - 1])) && i + 4 <= text.Length
                && text.AsSpan(i, 4).Equals("FROM", StringComparison.OrdinalIgnoreCase) && (i + 4 == text.Length || !IsWord(text[i + 4])))
                return text[..i];
        }
        return text;
    }

    static IEnumerable<string> SplitTop(string text)
    {
        var depth = 0; var start = 0;
        foreach (var (c, i) in Scan(text))
        {
            depth += c == '(' ? 1 : c == ')' ? -1 : 0;
            if (depth == 0 && c == ',') { yield return text[start..i]; start = i + 1; }
        }
        yield return text[start..];
    }

    // The characters outside quotes, brackets and braces, with their index.
    static IEnumerable<(char, int)> Scan(string text)
    {
        char close = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (close != '\0') { if (c == close) close = '\0'; continue; }
            close = c switch { '\'' => '\'', '"' => '"', '[' => ']', '{' => '}', _ => '\0' };
            if (close == '\0') yield return (c, i);
        }
    }

    static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    static string StripComments(string sql)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < sql.Length; i++)
        {
            if (sql[i] == '\'') { var end = sql.IndexOf('\'', i + 1); end = end < 0 ? sql.Length - 1 : end; sb.Append(sql, i, end - i + 1); i = end; }
            else if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; sb.Append(' '); }
            else if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? sql.Length : end + 1; sb.Append(' '); }
            else sb.Append(sql[i]);
        }
        return sb.ToString();
    }
}
