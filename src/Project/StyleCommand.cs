using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml style replace`: the fast lane for a small style change, one command instead of a read, a spec and an apply.
//   oml style replace <module.oml> --from <text> --to <text> [--class <name>]... [--in <Element>]... [--out <staged.oml>] [--dry-run] [--loose]
//   oml style replace <file.css>   --from <text> --to <text> [--class <name>]... --out <new.css> [--dry-run] [--loose]
// It reads every style sheet of the module (oml describe --css --all), replaces the value, writes a spec with one
// edits.set per changed sheet (the CSS from a file, so comments stay) and applies it to a NEW staged file. The output
// lists each changed line, before and after, then the apply report. Nothing is installed or published.
//   --class  only inside rules whose selector names that class (.card, or card); several classes may be given
//   --in     only these screens, blocks, emails or themes (a name, or the path describe prints)
//   --loose  also replace inside a longer value: by default 45px is not replaced in 145px, 0.45px or -45px
// The source module is never changed. With a .css file it works on plain CSS (a theme kept in a file).
static class StyleCommand
{
    const string Usage = "Usage: oml style replace <module.oml|file.css> --from <text> --to <text> [--class <name>]... [--in <Element>]... [--out <staged.oml|new.css>] [--dry-run] [--loose]";
    const int MaxLines = 12;

    internal sealed record Change(int Line, string Before, string After);

    static int Fail(string message, string? hint = null, int code = 2)
    {
        TerminalOutput.PrintObject(new { error = message, hint });
        return code;
    }

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h") { Console.WriteLine(CliUsage.For("style")); return args.Length == 0 ? 2 : 0; }
        if (args[0] != "replace" || args.Length < 2) return Fail($"Unknown or incomplete style command. {Usage}");
        var source = Path.GetFullPath(args[1]);
        string? from = null, to = null, output = null;
        var classes = new List<string>();
        var only = new List<string>();
        bool dry = false, loose = false;
        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from" when i + 1 < args.Length: from = args[++i]; break;
                case "--to" when i + 1 < args.Length: to = args[++i]; break;
                case "--class" when i + 1 < args.Length: classes.Add(args[++i].TrimStart('.')); break;
                case "--in" when i + 1 < args.Length: only.Add(args[++i]); break;
                case "--out" when i + 1 < args.Length: output = Path.GetFullPath(args[++i]); break;
                case "--dry-run": dry = true; break;
                case "--loose": loose = true; break;
                default: return Fail($"Unknown or incomplete option {args[i]}. {Usage}");
            }
        }
        if (string.IsNullOrEmpty(from) || to is null) return Fail("--from and --to are required.", Usage);
        if (from == to) return Fail("--from and --to are the same text.");
        if (classes.Any(c => c.Length == 0 || c.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_')))) return Fail("--class takes a class name: letters, digits, - and _ (.card or card).");
        if (!File.Exists(source)) return Fail($"File not found: {source}");
        if (output is not null && File.Exists(output)) return Fail($"{output} exists.", "oml never overwrites a file: give a new --out path.");
        var ext = Path.GetExtension(source).ToLowerInvariant();
        if (ext == ".css") return CssFile(source, from, to, classes, output, dry, loose);
        if (ext != ".oml") return Fail("style replace reads an .oml module or a .css file.");
        if (output is not null && !output.EndsWith(".oml", StringComparison.OrdinalIgnoreCase)) return Fail("--out must be an .oml path for a module.");

        // 1. Every style sheet of the module.
        var (code, text) = Oml(["describe", source, "--css", "--all"]);
        JsonNode? described = null;
        try { described = JsonNode.Parse(text); } catch (JsonException) { }
        if (code != 0 || Sheets(described) is not { } sheets)
            return Fail("The style sheets could not be read (oml describe --css --all).", Shorten(described?["error"]?.ToString() ?? text), code == 0 ? 1 : code);

        // 2. Replace, sheet by sheet.
        var wanted = sheets.Where(s => only.Count == 0 || only.Any(o => Matches(s.Element, o))).ToList();
        if (only.FirstOrDefault(o => !sheets.Any(s => Matches(s.Element, o))) is { } missing)
            return Fail($"--in {missing}: no screen, block, email or theme with a style sheet has that name.", "Elements with a style sheet: " + string.Join(", ", sheets.Select(s => s.Element).Take(40)));
        var changed = new List<(string Element, string Kind, string Css, List<Change> Changes)>();
        var skipped = 0;
        foreach (var sheet in wanted)
        {
            var (css, changes, partOfLonger) = Replace(sheet.Css, from, to, classes, loose);
            skipped += partOfLonger;
            if (changes.Count > 0) changed.Add((sheet.Element, sheet.Kind, css, changes));
        }
        var total = changed.Sum(c => c.Changes.Count);
        object Listed() => changed.Select(c => new
        {
            element = c.Element, kind = c.Kind, lines = c.Changes.Count,
            changes = c.Changes.Take(MaxLines).Select(x => new { line = x.Line, before = x.Before, after = x.After }),
            more = c.Changes.Count > MaxLines ? c.Changes.Count - MaxLines : (int?)null,
        });
        var skippedNote = skipped > 0 ? $"{skipped} place(s) where '{from}' is part of a longer value were left as they are; --loose replaces them too." : null;
        if (total == 0)
        {
            TerminalOutput.PrintObject(new
            {
                error = $"'{from}' was not found" + (classes.Count > 0 ? $" in rules of class {string.Join(", ", classes.Select(c => "." + c))}" : "") + $" in {wanted.Count} style sheet(s).",
                searched = wanted.Select(s => s.Element), note = skippedNote,
                hint = classes.Count > 0 ? "Check the class name, or leave --class out to search every rule." : "Check the text: it must be written as in the style sheet (oml describe <module.oml> <Element> --css).",
            });
            return 1;
        }
        if (dry)
        {
            TerminalOutput.PrintObject(new { dryRun = true, lines = total, changed = Listed(), note = skippedNote, next = "Run it again without --dry-run to write the staged file." });
            return 0;
        }

        // 3. The spec: one edits.set per sheet, the CSS in a file beside it so nothing but the value changes.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var name = Path.GetFileNameWithoutExtension(source);
        var project = ProjectConfig.RootOf(Path.GetDirectoryName(source)) ?? ProjectConfig.RootOf(null);
        var work = project is not null && Directory.Exists(Path.Combine(project, "specs"))
            ? Path.Combine(project, "specs", $"style-{name}-{stamp}")
            : Path.Combine(Path.GetDirectoryName(source)!, $"{name}.style-{stamp}");
        output ??= project is not null && Directory.Exists(Path.Combine(project, "staged"))
            ? Path.Combine(project, "staged", $"{name}.style-{stamp}.oml")
            : Path.Combine(Path.GetDirectoryName(source)!, $"{name}.style-{stamp}.staged.oml");
        if (File.Exists(output)) return Fail($"{output} exists.", "Give a new --out path.");
        Directory.CreateDirectory(work);
        var edits = new JsonArray();
        for (var i = 0; i < changed.Count; i++)
        {
            var file = $"sheet-{i + 1}.css";
            File.WriteAllText(Path.Combine(work, file), changed[i].Css, new UTF8Encoding(false));
            edits.Add(new JsonObject { ["op"] = "set", ["element"] = changed[i].Element, ["properties"] = new JsonObject { ["styleSheet"] = new JsonObject { ["file"] = file } } });
        }
        var spec = Path.Combine(work, "spec.json");
        File.WriteAllText(spec, new JsonObject { ["edits"] = edits }.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n", new UTF8Encoding(false));

        // 4. Apply to a new staged file.
        var (applyCode, applyText) = Oml(["apply", source, spec, output]);
        JsonNode? report = null;
        try { report = JsonNode.Parse(applyText); } catch (JsonException) { }
        var saved = report?["saved"]?.GetValue<bool>() ?? report?["summary"]?["saved"]?.GetValue<bool>() ?? false;
        TerminalOutput.PrintObject(new
        {
            staged = saved ? output : null, lines = total, changed = Listed(), note = skippedNote, spec,
            apply = report ?? (object)new { error = Shorten(applyText) },
            next = saved && applyCode == 0 ? "Show the user the changed lines and the staged file. Ask before oml install." : "The apply did not finish cleanly: read apply, fix, and use a new --out path.",
        });
        return applyCode;
    }

    // A plain CSS file: the same replace, written to a new file.
    static int CssFile(string source, string from, string to, List<string> classes, string? output, bool dry, bool loose)
    {
        var (css, changes, skipped) = Replace(File.ReadAllText(source), from, to, classes, loose);
        var note = skipped > 0 ? $"{skipped} place(s) where '{from}' is part of a longer value were left as they are; --loose replaces them too." : null;
        if (changes.Count == 0) { TerminalOutput.PrintObject(new { error = $"'{from}' was not found in {source}.", note }); return 1; }
        if (!dry && output is null) return Fail("A .css file needs --out <new.css> (or --dry-run).");
        if (!dry) File.WriteAllText(output!, css, new UTF8Encoding(false));
        TerminalOutput.PrintObject(new
        {
            dryRun = dry ? true : (bool?)null, file = dry ? null : output, lines = changes.Count,
            changes = changes.Take(MaxLines).Select(x => new { line = x.Line, before = x.Before, after = x.After }),
            more = changes.Count > MaxLines ? changes.Count - MaxLines : (int?)null, note,
        });
        return 0;
    }

    // The output of describe --css --all: an array of { element, kind, styleSheet } (or one object for one sheet).
    static List<(string Element, string Kind, string Css)>? Sheets(JsonNode? node)
    {
        var items = node switch { JsonArray a => a.ToList(), JsonObject o when o["styleSheet"] is not null => [o], JsonObject o when o["sheets"] is JsonArray a => a.ToList(), _ => null };
        if (items is null) return null;
        var list = new List<(string, string, string)>();
        foreach (var item in items)
            if (item?["element"]?.ToString() is { Length: > 0 } element && item["styleSheet"] is JsonValue css && css.TryGetValue<string>(out var text))
                list.Add((element, item["kind"]?.ToString() ?? "", text));
        return list;
    }

    // --in Orders matches the element Orders and the path MainFlow/Orders.
    static bool Matches(string element, string wanted) =>
        element.Equals(wanted, StringComparison.OrdinalIgnoreCase) || element.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase);

    // Replaces `from` by `to`. With classes: only in the declarations of rules whose selector names one of them (a rule
    // nested in such a rule counts too; an @media block passes its parent's state on). Comments and strings are never
    // searched for braces. Without --loose a match that is part of a longer value is counted, not replaced.
    internal static (string Css, List<Change> Changes, int PartOfLonger) Replace(string css, string from, string to, IReadOnlyList<string> classes, bool loose)
    {
        var active = ActiveMap(css, classes);
        var result = new StringBuilder(css.Length + 64);
        var hits = new List<int>(); // positions in css that were replaced
        var partOfLonger = 0;
        var at = 0;
        while (true)
        {
            var found = css.IndexOf(from, at, StringComparison.Ordinal);
            if (found < 0) break;
            var inScope = active[found];
            var whole = loose || Whole(css, found, from.Length);
            if (inScope && !whole) partOfLonger++;
            if (inScope && whole)
            {
                result.Append(css, at, found - at).Append(to);
                hits.Add(found);
            }
            else result.Append(css, at, found + from.Length - at);
            at = found + from.Length;
        }
        result.Append(css, at, css.Length - at);
        var after = result.ToString();
        // One entry per changed line: the line as it was and as it is.
        var changes = new List<Change>();
        string[] oldLines = css.Split('\n'), newLines = after.Split('\n');
        if (!from.Contains('\n') && !to.Contains('\n') && oldLines.Length == newLines.Length)
        {
            for (var i = 0; i < oldLines.Length; i++)
                if (oldLines[i] != newLines[i]) changes.Add(new Change(i + 1, Cut(oldLines[i]), Cut(newLines[i])));
        }
        else
            foreach (var hit in hits) changes.Add(new Change(css.AsSpan(0, hit).Count('\n') + 1, Cut(from), Cut(to)));
        return (after, changes, partOfLonger);
    }

    static string Cut(string line) => line.Trim() is var t && t.Length > 160 ? t[..157] + "..." : line.Trim();

    // 45px is whole in "width: 45px;" and in "0 45px 0"; it is part of a longer value in 145px, 0.45px, -45px and 45pxx.
    static bool Whole(string css, int at, int length)
    {
        static bool Joins(char c) => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '%';
        var before = at > 0 && Joins(css[at - 1]) && Joins(css[at]);
        var end = at + length;
        var after = end < css.Length && Joins(css[end]) && css[end] != '.' && Joins(css[end - 1]);
        return !before && !after;
    }

    // For each character: may it be replaced? Never in a comment or a string. With classes: only inside the
    // declarations of a rule that names one of them. Without: everywhere else (selectors and @media conditions too).
    static bool[] ActiveMap(string css, IReadOnlyList<string> classes)
    {
        var all = classes.Count == 0;
        var map = new bool[css.Length];
        var stack = new Stack<bool>();
        var prelude = 0; // where the text of the next selector starts
        for (var i = 0; i < css.Length; i++)
        {
            var c = css[i];
            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var close = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = close < 0 ? css.Length : close + 2;
                if (prelude == i) prelude = stop; // a comment before a selector is not part of it
                i = stop - 1;
                continue;
            }
            if (c is '"' or '\'')
            {
                var j = i + 1;
                while (j < css.Length && css[j] != c && css[j] != '\n') j += css[j] == '\\' ? 2 : 1;
                i = Math.Min(j, css.Length - 1); // a string keeps its text
                continue;
            }
            if (c == '{')
            {
                var selector = css[prelude..i];
                var parent = stack.Count > 0 && stack.Peek();
                var on = all || (selector.TrimStart().StartsWith('@') ? parent : parent || classes.Any(name => Names(selector, name)));
                // The selector itself is not a declaration: clear what was marked while reading it.
                if (!all) for (var k = prelude; k < i; k++) map[k] = false;
                stack.Push(on);
                prelude = i + 1;
                continue;
            }
            if (c == '}')
            {
                if (stack.Count > 0) stack.Pop();
                prelude = i + 1;
                continue;
            }
            if (c == ';') prelude = i + 1;
            map[i] = all || (stack.Count > 0 && stack.Peek());
        }
        return map;
    }

    // Does the selector name the class: .card, .card:hover, a.card.wide; not .card-title or .cards.
    static bool Names(string selector, string name)
    {
        var at = 0;
        while ((at = selector.IndexOf("." + name, at, StringComparison.Ordinal)) >= 0)
        {
            var end = at + 1 + name.Length;
            if (end >= selector.Length || !(char.IsLetterOrDigit(selector[end]) || selector[end] is '-' or '_')) return true;
            at = end;
        }
        return false;
    }

    static string Shorten(string text) => text.Trim() is var t && t.Length > 400 ? t[..400] + "..." : text.Trim();

    // One oml command in a process of its own, its JSON collected: a module is opened and closed inside that process,
    // as the model needs (one edit job per process).
    static (int Code, string Output) Oml(string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["OML_OUTPUT"] = "json";
        using var process = Process.Start(info) ?? throw new InvalidOperationException("oml could not start itself.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Trim().Length > 0 ? output : error.Result);
    }
}
