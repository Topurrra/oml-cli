using System.Text;
using System.Text.Json.Nodes;

namespace OmlCli;

// Renders a command's JSON as readable terminal text. It only runs when the output goes to a person's console (or
// OML_OUTPUT=human): machine output keeps its exact JSON bytes. Text stays readable without color: color marks, never
// carries meaning. Lines fit Console.WindowWidth (fallback 100); long values wrap or truncate. Nested lists are
// indented; arrays of objects become tables; error/hint/warning/attention fields come first and are highlighted.
internal static class TerminalRender
{
    const int Indent = 2;
    const int KeyCap = 24;
    const int MaxDepth = 6;
    const int MaxRows = 300;

    static int Width => SafeWidth();
    static int SafeWidth()
    {
        try { return Console.WindowWidth > 40 ? Console.WindowWidth : 100; }
        catch { return 100; }
    }
    static bool Unicode { get; } = DetectUnicode();
    static bool DetectUnicode()
    {
        try { return !Console.OutputEncoding.IsSingleByte; }
        catch { return true; }
    }
    static string Bar => Unicode ? "─" : "-";
    static string Bullet => Unicode ? "•" : "-";
    static string Ellipsis => Unicode ? "…" : "...";

    static readonly (string field, string color)[] Specials =
    [
        ("error", TerminalColors.Red),
        ("warning", TerminalColors.Yellow),
        ("attention", TerminalColors.Magenta),
        ("hint", TerminalColors.Cyan),
    ];

    // ------------------------------------------------------------------ entry points

    public static string Human(string json)
    {
        var node = JsonNode.Parse(json);
        var sb = new StringBuilder();
        RenderRoot(sb, node);
        return sb.ToString().TrimEnd();
    }

    static void RenderRoot(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (TerminalOutput.Command is { } cmd && Tailored.TryGetValue(cmd, out var view)) view(sb, obj);
                else RenderObject(sb, obj, 0, section: true);
                break;
            case JsonArray array:
                RenderArray(sb, "Results", array, 0);
                break;
            default:
                sb.AppendLine(Scalar(node));
                break;
        }
    }

    // ------------------------------------------------------------- generic object/section

    // section=true: every key becomes its own titled block. section=false: a record-like key/value block.
    static void RenderObject(StringBuilder sb, JsonObject obj, int indent, bool section)
    {
        for (var s = 0; s < Specials.Length; s++)
            if (obj.TryGetPropertyValue(Specials[s].field, out var special) && special is not null && !IsEmpty(special))
                RenderSpecial(sb, Specials[s].field, Specials[s].color, special, indent);

        foreach (var (key, value) in obj)
        {
            if (IsSpecial(key) || value is null || IsEmpty(value)) continue;
            if (section) RenderField(sb, key, value, indent);
            else RenderBlockLine(sb, key, value, indent);
        }
    }

    static bool IsSpecial(string key) => Specials.Any(s => s.field == key);
    static bool IsEmpty(JsonNode node) => node is JsonArray { Count: 0 } || (node is JsonObject o && o.Count == 0);

    // A titled block for one key.
    static void RenderField(StringBuilder sb, string key, JsonNode value, int indent)
    {
        if (value is JsonObject obj)
        {
            if (RecordLike(obj, indent))
            {
                Title(sb, key, indent);
                RenderObject(sb, obj, indent + Indent, section: false);
            }
            else
            {
                Title(sb, key, indent);
                RenderObject(sb, obj, indent + Indent, section: true);
            }
            return;
        }
        if (value is JsonArray array)
        {
            RenderArray(sb, key, array, indent);
            return;
        }
        Line(sb, indent, Bold(key) + ": " + Scalar(value), Width - indent, indent + KeyCap + 2);
    }

    static void RenderBlockLine(StringBuilder sb, string key, JsonNode value, int indent)
    {
        if (value is JsonObject obj && !RecordLike(obj, indent))
        {
            RenderField(sb, key, obj, indent);
            return;
        }
        if (value is JsonArray { Count: > 0 } array && array.Any(a => a is JsonObject))
        {
            RenderArray(sb, key, array, indent);
            return;
        }
        var label = Bold(key) + ": ";
        var text = value is JsonArray a ? string.Join("; ", a.Select(Scalar)) : Scalar(value);
        Line(sb, indent, label + text, Width - indent, indent + KeyCap + 2);
    }

    // A small flat object (summary, a record, a settings block) renders as aligned key/value lines.
    static bool RecordLike(JsonObject obj, int indent)
    {
        if (indent >= MaxDepth * Indent) return true;
        if (obj.Count == 0 || obj.Count > 12) return false;
        return obj.All(p => p.Value is null || p.Value is JsonValue || p.Value is JsonArray { Count: 0 });
    }

    static void RenderSpecial(StringBuilder sb, string field, string color, JsonNode value, int indent)
    {
        var list = value is JsonArray a ? a.Select(Scalar) : new[] { Scalar(value) };
        foreach (var text in list)
        {
            var colored = TerminalColors.Paint(field + ": " + text, color + (TerminalColors.Enabled ? TerminalColors.Bold : ""));
            Line(sb, indent, colored, Width - indent, indent + field.Length + 2);
        }
        if (list.Any()) sb.AppendLine();
    }

    static void Title(StringBuilder sb, string title, int indent)
    {
        var t = Words(title);
        var line = TerminalColors.Paint(t, TerminalColors.Bold + (TerminalColors.Enabled ? TerminalColors.Blue : ""));
        sb.Append(' ', indent).AppendLine(line);
        var width = Width - indent;
        sb.Append(' ', indent).AppendLine(Repeat(Bar, Math.Max(3, width)));
    }

    // ---------------------------------------------------------------- arrays and tables

    static void RenderArray(StringBuilder sb, string key, JsonArray array, int indent)
    {
        if (array.Count == 0) return;
        if (array.All(a => a is JsonObject))
        {
            var rows = array.OfType<JsonObject>().Take(MaxRows).ToArray();
            if (rows.Length > 0 && !TableFits(rows, indent + Indent))
            {
                Title(sb, key, indent);
                foreach (var row in rows) RenderObject(sb, row, indent + Indent, section: true);
                if (array.Count > MaxRows) sb.Append(' ', indent + Indent).AppendLine(More(array.Count - MaxRows));
                return;
            }
            if (rows.Length > 0)
            {
                Title(sb, key, indent);
                RenderTable(sb, rows, indent + Indent);
                if (array.Count > MaxRows) sb.Append(' ', indent + Indent).AppendLine(More(array.Count - MaxRows));
            }
            return;
        }
        Title(sb, key, indent);
        foreach (var item in array.Take(MaxRows))
        {
            if (item is JsonObject o) RenderObject(sb, o, indent + Indent, section: true);
            else Line(sb, indent + Indent, Bullet + " " + Scalar(item), Width - indent - Indent, indent + Indent + 2);
        }
        if (array.Count > MaxRows) sb.Append(' ', indent + Indent).AppendLine(More(array.Count - MaxRows));
    }

    static string More(int hidden) => $"... {hidden} more (add --json to see all)";

    // A table only when every cell fits its column in full and no cell holds a nested list or object (warning samples).
    static bool TableFits(JsonObject[] rows, int indent)
    {
        if (rows.Any(r => r.Any(p => p.Value is JsonArray { Count: > 0 } or JsonObject { Count: > 0 }))) return false;
        var columns = new List<string>();
        foreach (var row in rows) foreach (var (k, _) in row) if (!columns.Contains(k)) columns.Add(k);
        columns.RemoveAll(c => rows.All(r => r[c] is null || IsEmpty(r[c]!)));
        if (columns.Count == 0) return true;
        var cells = rows.Select(r => columns.Select(c => Cell(r[c])).ToArray()).ToArray();
        var widths = ColumnWidths(columns, cells, indent);
        return cells.All(row => row.Select((cell, c) => cell.Length <= widths[c]).All(ok => ok));
    }

    // Columns come from the first row's keys, then any key later rows add. Columns whose value is null/empty in every
    // row are hidden. Cells are truncated to the column width; nothing is wrapped inside a cell.
    static void RenderTable(StringBuilder sb, JsonObject[] rows, int indent)
    {
        var columns = new List<string>();
        foreach (var row in rows)
            foreach (var (key, _) in row)
                if (!columns.Contains(key)) columns.Add(key);
        columns.RemoveAll(c => rows.All(r => r[c] is null || IsEmpty(r[c]!)));
        if (columns.Count == 0) return;

        var cells = rows.Select(r => columns.Select(c => Cell(r[c])).ToArray()).ToArray();
        var widths = ColumnWidths(columns, cells, indent);

        var header = new StringBuilder();
        header.Append(' ', indent);
        for (var c = 0; c < columns.Count; c++)
        {
            header.Append(TerminalColors.Paint(Pad(Words(columns[c]), widths[c]), TerminalColors.Bold));
            if (c < columns.Count - 1) header.Append("  ");
        }
        sb.AppendLine(header.ToString());

        var rule = new StringBuilder();
        rule.Append(' ', indent);
        for (var c = 0; c < columns.Count; c++)
        {
            rule.Append(Repeat(Bar, widths[c]));
            if (c < columns.Count - 1) rule.Append("  ");
        }
        sb.AppendLine(rule.ToString());

        foreach (var row in cells)
        {
            var line = new StringBuilder();
            line.Append(' ', indent);
            for (var c = 0; c < columns.Count; c++)
            {
                line.Append(Pad(Clip(row[c], widths[c]), widths[c]));
                if (c < columns.Count - 1) line.Append("  ");
            }
            sb.AppendLine(line.ToString().TrimEnd());
        }
    }

    static int[] ColumnWidths(List<string> columns, string[][] cells, int indent)
    {
        var n = columns.Count;
        var avail = Width - indent - (n - 1) * 2;
        var maxW = Math.Max(12, avail / Math.Max(1, n)) + 20;
        var lens = new int[n];
        for (var c = 0; c < n; c++)
        {
            lens[c] = columns[c].Length;
            foreach (var row in cells) lens[c] = Math.Max(lens[c], row[c].Length);
        }
        var widths = new int[n];
        for (var c = 0; c < n; c++) widths[c] = Math.Clamp(lens[c], 5, maxW);
        var total = widths.Sum();
        if (total > avail)
        {
            var overshoot = total - avail;
            while (overshoot > 0)
            {
                var widest = 0;
                for (var c = 1; c < n; c++) if (widths[c] > widths[widest]) widest = c;
                if (widths[widest] <= 5) break;
                widths[widest]--;
                overshoot--;
            }
        }
        else if (avail - total > 0)
        {
            var left = avail - total;
            var order = Enumerable.Range(0, n).OrderByDescending(c => lens[c]).ToArray();
            foreach (var c in order)
            {
                if (left <= 0) break;
                var grow = Math.Min(left, maxW - widths[c]);
                widths[c] += grow;
                left -= grow;
            }
        }
        return widths;
    }

    static string Cell(JsonNode? node) => node switch
    {
        null => "-",
        JsonValue v => Scalar(v),
        JsonArray a => $"{a.Count} item{(a.Count == 1 ? "" : "s")}",
        JsonObject o => "{…}",
        _ => "-",
    };

    // ---------------------------------------------------------------- scalar formatting

    static string Scalar(JsonNode? node)
    {
        if (node is null) return "-";
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s.Length == 0 ? "-" : Flat(s);
            if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
            if (v.TryGetValue<long>(out var l)) return l.ToString();
            if (v.TryGetValue<double>(out var d)) return d.ToString("0.############");
            return Flat(v.ToString());
        }
        return "-";
    }

    // A scalar as one line: runs of whitespace become one space.
    static string Flat(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ---------------------------------------------------------------- text helpers

    // Wrap a line at `width`, with continuation indent `next`. The text may contain ANSI codes: the visible length is
    // used, and padding must not count escape bytes.
    static void Line(StringBuilder sb, int indent, string text, int width, int next)
    {
        var plain = StripAnsi(text);
        if (plain.Length <= width)
        {
            sb.Append(' ', indent).AppendLine(text);
            return;
        }
        sb.Append(' ', indent);
        var written = 0;
        foreach (var word in plain.Split(' '))
        {
            var w = word.Length;
            if (written > 0 && written + 1 + w > width)
            {
                sb.AppendLine();
                sb.Append(' ', next);
                written = 0;
            }
            else if (written > 0)
            {
                sb.Append(' ');
                written++;
            }
            if (w > width)
            {
                sb.Append(word); // a path or command stays whole, so it can be copied; the console wraps it
                written = width;
            }
            else
            {
                sb.Append(word);
                written += w;
            }
        }
        sb.AppendLine();
    }

    static string StripAnsi(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\x1b') { while (i + 1 < text.Length && text[i + 1] != 'm') i++; i++; }
            else sb.Append(text[i]);
        }
        return sb.ToString();
    }

    static string Pad(string text, int width) => text.Length >= width ? text : text + new string(' ', width - text.Length);
    static string Clip(string text, int width) => text.Length <= width ? text : text[..Math.Max(0, width - 1)] + Ellipsis;
    static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, Math.Max(0, n)));
    static string Bold(string text) => TerminalColors.Paint(text, TerminalColors.Bold);

    // camelCase / snake_case to words, first letter capital: "serverActions" -> "Server actions".
    static string Words(string name)
    {
        if (name.Length == 0) return name;
        var sb = new StringBuilder();
        var prevLower = false;
        var prevDigit = false;
        foreach (var ch in name)
        {
            if (ch is '_' or '-') { sb.Append(' '); prevLower = false; prevDigit = false; continue; }
            var upper = char.IsUpper(ch);
            if (sb.Length > 0 && (upper && (prevLower || (prevDigit && char.IsLetter(ch)))))
                sb.Append(' ');
            sb.Append(ch);
            prevLower = char.IsLower(ch);
            prevDigit = char.IsDigit(ch);
        }
        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length > 1 && w.All(char.IsUpper)) continue; // acronym stays
            words[i] = char.ToUpperInvariant(w[0]) + w[1..];
        }
        return string.Join(" ", words);
    }

    // ------------------------------------------------------------------ help

    static readonly (string area, string[] commands)[] HelpAreas =
    [
        ("Read", ["inspect", "describe", "find", "census", "meta", "expr", "tree", "openapi", "validate"]),
        ("Change", ["new", "apply", "install", "test"]),
        ("Preview", ["preview", "architecture", "check", "review"]),
        ("Service Studio", ["studio", "pull", "publish", "references"]),
        ("LifeTime", ["lt"]),
        ("Knowledge", ["spec", "doc", "warn", "memory", "library", "skills"]),
        ("Assets", ["app", "extension", "templates", "translations", "font", "image"]),
        ("Maintenance", ["version", "api", "widgets", "selftest", "doctor", "dashboard", "setup", "log", "run"]),
    ];

    public static string Help(string usage, string? command)
    {
        var lines = usage.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var sb = new StringBuilder();
        var byArea = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var notes = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("oml ", StringComparison.Ordinal))
            {
                var cmd = line.Length > 4 ? line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "" : "";
                var area = HelpAreas.FirstOrDefault(a => a.commands.Contains(cmd)).area ?? "Other";
                if (!byArea.TryGetValue(area, out var areaLines)) { areaLines = new List<string>(); byArea[area] = areaLines; }
                areaLines.Add(line);
            }
            else if (line.Trim().Length > 0) notes.Add(line);
        }

        foreach (var (area, _) in HelpAreas.Concat([("Other", [])]))
        {
            if (!byArea.TryGetValue(area, out var areaLines) || areaLines.Count == 0) continue;
            sb.AppendLine(TerminalColors.Paint(area, TerminalColors.Bold + (TerminalColors.Enabled ? TerminalColors.Blue : "")));
            foreach (var line in areaLines)
            {
                // "oml <cmd> <rest>": the command name stands out.
                var space = line.IndexOf(' ', 4);
                var head = space > 0 ? line[..space] : line;
                var rest = space > 0 ? line[space..] : "";
                Line(sb, Indent, Bold(head) + rest, Width - Indent, Indent + 4);
            }
            sb.AppendLine();
        }
        foreach (var note in notes) Line(sb, 0, note, Width, 0);
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ tailored views

    static readonly Dictionary<string, Action<StringBuilder, JsonObject>> Tailored = new(StringComparer.Ordinal)
    {
        ["validate"] = ValidateView,
        ["apply"] = ApplyView,
        ["check"] = CheckView,
        ["review"] = ReviewView,
        ["doctor"] = DoctorView,
        ["meta"] = MetaView,
        ["expr"] = ExprView,
        ["find"] = FindView,
        ["doc"] = DocView,
        ["census"] = CensusView,
        ["version"] = VersionView,
        ["library"] = LibraryView,
        ["skills"] = SkillsView,
    };

    static string Text(JsonObject o, string key) => o.TryGetPropertyValue(key, out var v) ? Scalar(v) : "";

    // A shallow copy of an object without the keys a tailored view already rendered, so the generic pass shows the rest.
    static JsonObject Without(JsonObject obj, params string[] keys)
    {
        var copy = new JsonObject();
        foreach (var (key, value) in obj)
            if (!keys.Contains(key)) copy[key] = value?.DeepClone();
        return copy;
    }

    static void ValidateView(StringBuilder sb, JsonObject root)
    {
        if (root["summary"] is JsonObject summary)
            sb.AppendLine(TerminalColors.Paint(
                $"Module {Words(Text(summary, "module"))}: {Text(summary, "errors")} errors, {Text(summary, "warnings")} warnings",
                TerminalColors.Bold));
        RenderObject(sb, Without(root, "summary"), 0, section: true);
    }

    static void ApplyView(StringBuilder sb, JsonObject root)
    {
        var saved = root["summary"] is JsonObject s && s["saved"]?.GetValue<bool>() == true;
        var staged = root["staged"]?.GetValue<string>();
        sb.AppendLine(TerminalColors.Paint(saved ? "Saved " + (staged ?? "") : "Not saved", TerminalColors.Bold));
        var handled = new List<string> { "summary", "staged" };
        if (root["summary"] is JsonObject summary)
        {
            var parts = new List<string>();
            foreach (var key in new[] { "newErrors", "reopenNewErrors", "newWarnings", "problems" })
            {
                var value = summary[key]?.GetValue<int>();
                if (value is > 0) parts.Add($"{Words(key)} {value}");
            }
            if (parts.Count > 0) sb.AppendLine(string.Join(", ", parts.Take(4)));
        }
        RenderObject(sb, Without(root, handled.ToArray()), 0, section: true);
    }

    static void CheckView(StringBuilder sb, JsonObject root)
    {
        var handled = new List<string> { "summary" };
        if (root["summary"] is JsonObject summary)
        {
            var head = $"Findings {Text(summary, "findings")}";
            foreach (var sev in new[] { "high", "medium", "low" })
                if (summary[sev] is { } n && n.GetValue<int>() > 0) head += $", {sev} {n}";
            if (summary["new"] is { } nw) head += $"; {nw} new, {Text(summary, "existing")} existing";
            if (summary["ignored"] is { } ig && ig.GetValue<int>() > 0) head += $"; {ig} ignored";
            sb.AppendLine(TerminalColors.Paint(head, TerminalColors.Bold));
        }
        if (root["findings"] is JsonArray findings)
        {
            var rows = findings.OfType<JsonObject>().Take(MaxRows).ToArray();
            if (rows.Length > 0)
            {
                Title(sb, "Findings", 0);
                var cols = new[] { "severity", "status", "rule", "module", "element", "title" };
                BuildTable(sb, rows, cols, Indent);
            }
            handled.Add("findings");
            foreach (var key in new[] { "warnings", "skipped" })
                if (root[key] is JsonArray { Count: > 0 } arr) { RenderArray(sb, key, arr, 0); handled.Add(key); }
        }
        RenderObject(sb, Without(root, handled.ToArray()), 0, section: true);
    }

    static void ReviewView(StringBuilder sb, JsonObject root)
    {
        var handled = new List<string> { "summary" };
        if (root["summary"] is JsonObject summary)
        {
            var head = $"Review of {Words(Text(root, "module"))}";
            foreach (var key in new[] { "added", "removed", "recreated", "renamed", "changed", "risks", "info", "newFindings", "resolvedFindings" })
                if (summary[key] is { } v && v.GetValue<int>() > 0) head += $", {key} {v}";
            sb.AppendLine(TerminalColors.Paint(head, TerminalColors.Bold));
        }
        if (root["risks"] is JsonArray risks && risks.Count > 0)
        {
            Title(sb, "Risks", 0);
            BuildTable(sb, risks.OfType<JsonObject>().ToArray(), new[] { "rule", "element", "text" }, Indent);
            handled.Add("risks");
        }
        if (root["changes"] is JsonArray changes && changes.Count > 0)
        {
            Title(sb, "Changes", 0);
            foreach (var change in changes.OfType<JsonObject>())
            {
                var what = Text(change, "change");
                var kind = Text(change, "kind");
                var name = Text(change, "name");
                Line(sb, Indent, $"{what} {kind} {name}", Width - Indent, Indent + 4);
                if (change["details"] is JsonArray details)
                    foreach (var d in details.Take(6))
                        Line(sb, Indent * 2, Bullet + " " + Scalar(d), Width - Indent * 2, Indent * 2 + 2);
            }
            handled.Add("changes");
        }
        if (root["newFindings"] is JsonArray fresh && fresh.Count > 0)
        {
            Title(sb, "New findings", 0);
            BuildTable(sb, fresh.OfType<JsonObject>().ToArray(), new[] { "severity", "rule", "element", "title" }, Indent);
            handled.Add("newFindings");
        }
        RenderObject(sb, Without(root, handled.ToArray()), 0, section: true);
    }

    static void DoctorView(StringBuilder sb, JsonObject root)
    {
        if (root["checks"] is JsonArray checks)
        {
            var rows = checks.OfType<JsonObject>().ToArray();
            var fails = rows.Count(r => Text(r, "status") == "fail");
            sb.AppendLine(TerminalColors.Paint($"{rows.Length} checks, {fails} failed", TerminalColors.Bold));
            BuildTable(sb, rows, new[] { "name", "status", "detail", "fix" }, 0);
            RenderObject(sb, Without(root, "checks"), 0, section: true);
            return;
        }
        RenderObject(sb, root, 0, section: true);
    }

    static void MetaView(StringBuilder sb, JsonObject root)
    {
        if (root["same"]?.GetValue<bool>() == true)
        {
            sb.AppendLine(TerminalColors.Paint("Same module: no header field differs", TerminalColors.Bold));
            return;
        }
        if (root["differing"] is JsonArray differing && differing.Count > 0)
        {
            sb.AppendLine(TerminalColors.Paint($"{differing.Count} header field(s) differ", TerminalColors.Bold));
            BuildTable(sb, differing.OfType<JsonObject>().ToArray(), new[] { "field", "left", "right" }, 0);
            return;
        }
        RenderObject(sb, root, 0, section: true);
    }

    static void ExprView(StringBuilder sb, JsonObject root)
    {
        if (root["ok"]?.GetValue<bool>() == true)
            sb.AppendLine(TerminalColors.Paint("OK: " + Text(root, "canonical"), TerminalColors.Green));
        else
            sb.AppendLine(TerminalColors.Paint("error: " + Text(root, "error"), TerminalColors.Red));
    }

    static void FindView(StringBuilder sb, JsonObject root)
    {
        var handled = new List<string>();
        if (root["matches"] is JsonArray matches)
        {
            Title(sb, "Matches for " + Text(root, "name"), 0);
            BuildTable(sb, matches.OfType<JsonObject>().ToArray(), new[] { "module", "name", "qualified", "kind", "source", "file" }, Indent);
            handled.Add("matches");
        }
        if (root["problems"] is JsonArray { Count: > 0 } problems) { RenderArray(sb, "Problems", problems, 0); handled.Add("problems"); }
        RenderObject(sb, Without(root, handled.ToArray()), 0, section: true);
    }

    static void DocView(StringBuilder sb, JsonObject root)
    {
        var handled = new List<string>();
        if (root["hits"] is JsonArray hits)
        {
            Title(sb, "Hits for " + Text(root, "query"), 0);
            BuildTable(sb, hits.OfType<JsonObject>().ToArray(), new[] { "kind", "title", "open", "score", "snippet" }, Indent);
            handled.Add("hits");
        }
        RenderObject(sb, Without(root, handled.ToArray()), 0, section: true);
    }

    static void CensusView(StringBuilder sb, JsonObject root)
    {
        if (root["byType"] is JsonObject byType)
        {
            var rows = byType.Select(p => new JsonObject { ["type"] = p.Key, ["count"] = p.Value?.DeepClone() }).OfType<JsonObject>().ToArray();
            sb.AppendLine(TerminalColors.Paint($"Total: {Text(root, "total")} objects", TerminalColors.Bold));
            BuildTable(sb, rows, new[] { "type", "count" }, 0);
            return;
        }
        RenderObject(sb, root, 0, section: true);
    }

    static void VersionView(StringBuilder sb, JsonObject root)
    {
        var rows = root.Select(p => new JsonObject { ["key"] = p.Key, ["value"] = p.Value?.DeepClone() }).OfType<JsonObject>().ToArray();
        BuildTable(sb, rows, new[] { "key", "value" }, 0);
    }

    static void LibraryView(StringBuilder sb, JsonObject root)
    {
        var handled = new List<string>();
        if (root["sources"] is JsonArray sources)
        {
            Title(sb, "Library sources", 0);
            BuildTable(sb, sources.OfType<JsonObject>().ToArray(), new[] { "source", "repo", "commit", "files", "kb", "seconds", "error" }, Indent);
            handled.Add("sources");
        }
        RenderObject(sb, Without(root, handled.ToArray()), 0, section: true);
    }

    static void SkillsView(StringBuilder sb, JsonObject root)
    {
        var handled = new List<string>();
        if (root["skills"] is JsonArray skills)
        {
            Title(sb, "Company skills", 0);
            BuildTable(sb, skills.OfType<JsonObject>().ToArray(), new[] { "name", "description", "tokens", "open" }, Indent);
            handled.Add("skills");
        }
        RenderObject(sb, Without(root, handled.ToArray()), 0, section: true);
    }

    // A table built from a fixed column list (tailored views), reusing the shared width logic.
    static void BuildTable(StringBuilder sb, JsonObject[] rows, string[] columns, int indent)
    {
        var present = columns.Where(c => rows.Any(r => r[c] is not null && !IsEmpty(r[c]!))).ToArray();
        if (present.Length == 0) return;
        var cells = rows.Select(r => present.Select(c => Cell(r[c])).ToArray()).ToArray();
        var widths = ColumnWidths(present.ToList(), cells, indent);
        var header = new StringBuilder();
        header.Append(' ', indent);
        for (var c = 0; c < present.Length; c++)
        {
            header.Append(TerminalColors.Paint(Pad(Words(present[c]), widths[c]), TerminalColors.Bold));
            if (c < present.Length - 1) header.Append("  ");
        }
        sb.AppendLine(header.ToString());
        var rule = new StringBuilder();
        rule.Append(' ', indent);
        for (var c = 0; c < present.Length; c++)
        {
            rule.Append(Repeat(Bar, widths[c]));
            if (c < present.Length - 1) rule.Append("  ");
        }
        sb.AppendLine(rule.ToString());
        foreach (var row in cells)
        {
            var line = new StringBuilder();
            line.Append(' ', indent);
            for (var c = 0; c < present.Length; c++)
            {
                line.Append(Pad(Clip(row[c], widths[c]), widths[c]));
                if (c < present.Length - 1) line.Append("  ");
            }
            sb.AppendLine(line.ToString().TrimEnd());
        }
    }

    // ------------------------------------------------------------------ self-tests

    // Unit-style self-tests of the renderer, run by `oml terminal-test`: they need no console. They feed JSON and
    // compare the rendered text, including the color on/off paths (NO_COLOR gives no escape sequences).
    public static string SelfTest()
    {
        var failures = new List<string>();
        void Check(string name, Func<bool> ok)
        {
            try { if (!ok()) failures.Add(name); }
            catch (Exception e) { failures.Add($"{name}: {e.Message}"); }
        }

        var objectJson = """{"name":"Reg","kind":"CrossDevice","hidden":null,"attrs":[{"name":"Id","type":"Long Integer"},{"name":"Label","type":"Text"}]}""";
        Check("object lines", () =>
        {
            var text = Human(objectJson);
            return text.Contains("name: Reg") && text.Contains("kind: CrossDevice");
        });
        Check("empty values dropped", () => !Human(objectJson).Contains("hidden"));
        Check("table header and rows", () =>
        {
            var text = Human(objectJson);
            return text.Contains("Attrs") && text.Contains("Id") && text.Contains("Long Integer");
        });
        Check("special fields first", () =>
        {
            var text = Human("""{"name":"x","error":"boom","hint":"fix it"}""");
            var error = text.IndexOf("error:", StringComparison.Ordinal);
            var hint = text.IndexOf("hint:", StringComparison.Ordinal);
            var name = text.IndexOf("name:", StringComparison.Ordinal);
            return error >= 0 && hint > error && name > hint;
        });
        Check("agent-loop commands stay JSON in a console", () =>
            !TerminalOutput.ForDevelopers("inspect") && !TerminalOutput.ForDevelopers("apply") && !TerminalOutput.ForDevelopers("run")
            && TerminalOutput.ForDevelopers("doctor") && TerminalOutput.ForDevelopers(null));
        Check("a table that would cut a cell becomes stacked blocks", () =>
            Human("""{"items":[{"name":"a","path":"C:\\a\\very\\long\\path\\that\\does\\not\\fit\\in\\any\\column\\of\\a\\console\\table\\at\\all\\because\\it\\is\\long\\really\\long.oml"}]}""")
                .Contains("C:\\a\\very\\long\\path\\that\\does\\not\\fit\\in\\any\\column\\of\\a\\console\\table\\at\\all\\because\\it\\is\\long\\really\\long.oml"));
        Check("nested samples are shown, not counted", () =>
            Human("""{"warnings":[{"id":"X","count":1,"samples":[{"owner":"Order_Save","message":"Unused"}]}]}""").Contains("Order_Save"));        Check("no color gives no escapes", () =>
        {
            var previous = TerminalColors.Enabled;
            try
            {
                TerminalColors.Enabled = false;
                var text = Human(objectJson);
                return !text.Contains('\x1b');
            }
            finally { TerminalColors.Enabled = previous; }
        });
        Check("color highlights error", () =>
        {
            var previous = TerminalColors.Enabled;
            try
            {
                TerminalColors.Enabled = true;
                var text = Human("""{"error":"boom"}""");
                return text.Contains('\x1b');
            }
            finally { TerminalColors.Enabled = previous; }
        });
        Check("bars render", () => Bar.Length > 0 && Bullet.Length > 0);
        Check("long value wraps", () =>
        {
            var text = Human("""{"message":"a very long line that certainly exceeds the terminal width and must wrap onto the next line with an indent"}""");
            return text.Length > 40 && text.Contains('\n');
        });
        Check("help groups areas", () =>
        {
            var help = Help(CliUsage.Text, null);
            return help.Contains("Read") && help.Contains("Change") && help.Contains("Knowledge") && help.Contains("oml inspect");
        });

        return failures.Count == 0 ? "ok" : "FAIL\n" + string.Join("\n", failures);
    }
}
