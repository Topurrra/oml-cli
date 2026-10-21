using System.Text.RegularExpressions;

namespace OmlCli;

// The message rows of skill/reference/warnings.md: | `Id`, `Id2` | Title | Cause | Fix |. `oml warn` prints them,
// and the apply and validate reports add the Fix cell as `hint` to a message id the file covers. The file is
// embedded in both oml.exe and OmlCli.Core.dll (this source is compiled into both), so the hints and the doc
// can never disagree.
static class WarningTable
{
    internal sealed record Row(string Section, string[] Ids, string Title, string Cause, string Fix, string Line);

    const string ResourceName = "OmlCli.doc.warnings.md";
    static List<Row>? rows;

    internal static List<Row> Rows => rows ??= Parse(Text());

    static string Text()
    {
        using var stream = typeof(WarningTable).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"This build has no embedded resource {ResourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    internal static List<Row> Parse(string text)
    {
        var list = new List<Row>();
        var section = "";
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("## ")) { section = line[3..].Trim(); continue; }
            if (!line.StartsWith("| ") || line.StartsWith("| Id |")) continue;
            var cells = line.Trim().Trim('|').Split(" | ").Select(c => c.Trim()).ToArray();
            if (cells.Length != 4) continue; // tests/doc-check.ps1 fails on a table row that is not 4 cells
            var ids = Regex.Matches(cells[0], "`([A-Za-z0-9_]+)`").Select(m => m.Groups[1].Value).ToArray();
            list.Add(new Row(section, ids, cells[1], cells[2], cells[3], line));
        }
        return list;
    }

    // The fix for one validation message id, or null when warnings.md has no row for it.
    internal static string? Hint(string? id) =>
        string.IsNullOrEmpty(id) ? null : Rows.FirstOrDefault(r => r.Ids.Contains(id, StringComparer.Ordinal))?.Fix;
}
