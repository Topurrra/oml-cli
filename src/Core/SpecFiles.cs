using System.Text;
using System.Text.Json.Nodes;

namespace OmlCli;

// {"file": "relative/path"} as the value of a code or expression key: the text of a file next to the spec, so code
// that JSON escaping would mangle stays as written. No model types: `oml spec check` in the launcher uses it too.
static class SpecFiles
{
    public static readonly string[] Keys = ["javaScript", "styleSheet", "sql", "expression", "value"];
    const int MaxBytes = 2 * 1024 * 1024;
    static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// Replaces each {"file": ...} value of those keys by the file's text: UTF-8 (or UTF-16 with a BOM), BOM removed,
    /// nothing else changed. The path is relative to the spec's folder and must stay inside it, with no link on the way.
    /// A value that fails becomes "" and a problem.
    public static List<(string path, string message)> Resolve(JsonNode? spec, string specFile)
    {
        var problems = new List<(string, string)>();
        Walk(spec, "$", Folder(specFile), problems);
        return problems;
    }

    static string Folder(string specFile) => Path.GetDirectoryName(Path.GetFullPath(specFile))!;

    // Older path keys (images, scripts, resources "file", a theme's styleSheet path, module icon, copy "from",
    // translations "import") were relative to the working folder. A relative path that names a file in the spec's
    // folder now means that file; any other value stays as it is, so it still works from the working folder.
    // Returns a warning for each path that names a different file in both folders.
    public static List<string> Rebase(JsonNode? spec, string specFile)
    {
        var warnings = new List<string>();
        if (spec is not JsonObject root) return warnings;
        var folder = Folder(specFile);
        void Fix(JsonObject? o, string key)
        {
            if (o?[key] is not JsonValue v || !v.TryGetValue<string>(out var path) || path.Length == 0 || Path.IsPathRooted(path)
                || path.IndexOfAny(['{', '\n', ':', '*', '?', '"', '<', '>', '|']) >= 0 || !File.Exists(Path.Combine(folder, path))) return;
            var mine = Path.GetFullPath(Path.Combine(folder, path));
            if (File.Exists(path) && !string.Equals(Path.GetFullPath(path), mine, StringComparison.OrdinalIgnoreCase)
                && !File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(mine)))
                warnings.Add($"{path} is in both the spec's folder and the working folder; using the spec's ({mine})");
            o[key] = mine;
        }
        foreach (var list in new[] { "images", "scripts", "resources" })
            if (root[list] is JsonArray items) foreach (var item in items) Fix(item as JsonObject, "file");
        if (root["themes"] is JsonArray themes) foreach (var theme in themes) Fix(theme as JsonObject, "styleSheet");
        if (root["copy"] is JsonArray copies) foreach (var copy in copies) Fix(copy as JsonObject, "from");
        Fix(root["module"] as JsonObject, "icon");
        Fix(root["translations"] as JsonObject, "import");
        return warnings;
    }

    static void Walk(JsonNode? node, string path, string root, List<(string, string)> problems)
    {
        if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++) Walk(array[i], $"{path}[{i}]", root, problems);
        if (node is not JsonObject obj) return;
        foreach (var (key, value) in obj.ToList())
        {
            if (key.StartsWith('_') || key.StartsWith('$')) continue; // free note keys
            var at = $"{path}.{key}";
            var code = Keys.Contains(key, StringComparer.OrdinalIgnoreCase);
            if (value is JsonObject o && o.ContainsKey("file") && (code || o.Count == 1))
            {
                if (!code) problems.Add((at, $"a {{\"file\": ...}} value is only for {string.Join(", ", Keys)}"));
                else if (o.Count != 1 || o["file"] is not JsonValue v || !v.TryGetValue<string>(out var file))
                    problems.Add((at, "write a file value as {\"file\": \"relative/path\"}: one key, a string"));
                else { obj[key] = Read(file, root, at, problems); continue; }
                obj[key] = "";
            }
            else Walk(value, at, root, problems);
        }
    }

    static string Read(string file, string root, string at, List<(string, string)> problems)
    {
        string? Fail(string message) { problems.Add((at, message)); return null; }
        var inside = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = file.Length == 0 || Path.IsPathRooted(file) || file.Contains(':') ? null : Path.GetFullPath(Path.Combine(root, file));
        var device = file.Split('/', '\\').Select(s => s.Split('.')[0].TrimEnd(' ')).FirstOrDefault(Devices.Contains); // base name before the first dot: NUL, nul.js
        var text = full is null ? Fail($"file '{file}' must be a path relative to the spec's folder (no drive, no ':')")
            : device is not null ? Fail($"file '{file}': the base name '{device}' is a Windows device name, not a file name; rename the file")
            : !full.StartsWith(inside, StringComparison.OrdinalIgnoreCase) ? Fail($"file '{file}' is outside the spec's folder {root}; keep it in that folder or below")
            : Link(full, inside) is { } link ? Fail($"file '{file}': {link} is a link (symbolic link or junction); use the real file inside the spec's folder")
            : Directory.Exists(full) ? Fail($"file '{file}' is a folder, not a file")
            : !File.Exists(full) ? Fail($"file not found: {full}")
            : Decode(full, Fail);
        return text ?? "";
    }

    // The first link on the way from the spec's folder down to the file (the file itself included).
    static string? Link(string full, string inside)
    {
        for (var p = full; p.Length >= inside.Length; p = Path.GetDirectoryName(p)!)
        {
            FileSystemInfo info = Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p);
            if (info.Exists && info.LinkTarget is not null) return p;
            if (Path.GetDirectoryName(p) is null) break;
        }
        return null;
    }

    static string? Decode(string full, Func<string, string?> fail)
    {
        if (new FileInfo(full).Length > MaxBytes) return fail($"{full} is larger than {MaxBytes / 1024 / 1024} MB, too big for a value in a module");
        byte[] bytes;
        try { bytes = File.ReadAllBytes(full); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return fail($"cannot read {full}: {e.Message}"); }
        var (encoding, bom, name) = bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => ((Encoding)new UTF8Encoding(false, true), 3, "UTF-8"),
            [0xFF, 0xFE, ..] => (new UnicodeEncoding(false, false, true), 2, "UTF-16"),
            [0xFE, 0xFF, ..] => (new UnicodeEncoding(true, false, true), 2, "UTF-16"),
            _ => (new UTF8Encoding(false, true), 0, "UTF-8"),
        };
        string text;
        try { text = encoding.GetString(bytes, bom, bytes.Length - bom); }
        catch (DecoderFallbackException) { return fail($"{full} is not valid {name}; save it as UTF-8"); }
        return text.Contains('\0') ? fail($"{full} contains a NUL character (U+0000): it is binary, or UTF-16 or UTF-32 without a BOM; save it as UTF-8") : text;
    }
}
