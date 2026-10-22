using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// Opt-in local usage log. Off unless OML_LOG is set: "1" writes %LOCALAPPDATA%\oml-cli\usage.jsonl, any other
// value (except "0") is the file to append to. One JSON line per command, and per step inside `oml run`:
// UTC time, command name, exit code, duration, output size, the module file NAME (no folder) and, for a failure,
// the first problem message cut to 200 characters with folders and secrets removed. It never logs arguments,
// spec contents, tokens, environment values or paths, and it sends nothing anywhere.
// Linked into OmlCli.Core as well (see the Core csproj): the launcher logs whole commands, Core logs run steps.
static class UsageLog
{
    const int MaxMessage = 200;
    const int KeepText = 64 * 1024;
    static readonly string[] ModuleExtensions = [".oml", ".oap", ".osp", ".xif"];
    // Commands whose second word is a verb worth telling apart in the summary (`lt status` vs `lt deploy`).
    static readonly Dictionary<string, string[]> Verbs = new()
    {
        ["lt"] = ["environments", "apps", "teams", "team-apps", "download", "tag", "deploy", "status", "abort", "continue"],
        ["app"] = ["list", "extract", "pack"], ["extension"] = ["inspect", "new"], ["font"] = ["woff2", "batch"],
        ["image"] = ["batch"], ["spec"] = ["check", "schema"], ["translations"] = ["export"], ["test"] = ["scaffold"],
    };
    // Environment values that must never reach the file, even inside an error message.
    static readonly string[] Secrets = ["OML_LIFETIME_TOKEN", "OML_LIFETIME_HOST"];

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oml-cli", "usage.jsonl");

    public static string? FilePath
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("OML_LOG");
            if (string.IsNullOrWhiteSpace(value) || value == "0") return null;
            try { return value == "1" ? DefaultPath : Path.GetFullPath(value); }
            catch (ArgumentException) { return null; } // not a usable path: log nothing
        }
    }

    public static bool Enabled => FilePath is not null;

    // Runs one whole command with stdout and stderr copied (not held back), then logs it.
    public static int Measure(IReadOnlyList<string> args, Func<int> run)
    {
        var (originalOut, originalError) = (Console.Out, Console.Error);
        var (copyOut, copyError) = (new Copy(originalOut), new Copy(originalError));
        Console.SetOut(copyOut);
        Console.SetError(copyError);
        var watch = Stopwatch.StartNew();
        var code = 1;
        string? crash = null;
        try { code = run(); return code; }
        catch (Exception e) { crash = e.Message; throw; }
        finally
        {
            watch.Stop();
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Record(args, code, watch.ElapsedMilliseconds, copyOut.Bytes, copyOut.Text, crash ?? copyError.Text);
        }
    }

    public static void Record(IReadOnlyList<string> args, int exitCode, long ms, long bytes, string stdout, string stderr, bool inRun = false)
    {
        var path = FilePath;
        if (path is null || args.Count == 0) return;
        try
        {
            var line = new JsonObject
            {
                ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                ["cmd"] = CommandName(args), ["exit"] = exitCode, ["ms"] = ms, ["bytes"] = bytes,
            };
            if (ModuleName(args) is { } module) line["module"] = module;
            if (exitCode != 0) line["error"] = Clean(Problem(stdout, stderr) ?? "(no message)");
            if (inRun) line["run"] = true;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Append and share, so two commands that finish together do not lose a line.
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytesOut = new UTF8Encoding(false).GetBytes(line.ToJsonString() + "\n");
            stream.Write(bytesOut, 0, bytesOut.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // shortcut: a log that cannot be written is skipped silently, so logging never breaks a command; upgrade to a one-time stderr note if users ask.
        }
    }

    static string CommandName(IReadOnlyList<string> args) =>
        Verbs.TryGetValue(args[0], out var verbs) && args.Count > 1 && verbs.Contains(args[1]) ? $"{args[0]} {args[1]}" : args[0];

    // The file NAME of the first .oml/.oap/.osp/.xif argument; never its folder.
    static string? ModuleName(IReadOnlyList<string> args)
    {
        foreach (var a in args.Skip(1))
            if (!a.StartsWith("--") && ModuleExtensions.Contains(System.IO.Path.GetExtension(a), StringComparer.OrdinalIgnoreCase))
                return System.IO.Path.GetFileName(a);
        return null;
    }

    // The first problem in a failed command's output: a JSON "error", the first of "problems" / error groups,
    // or the first line of stderr (or of text output) when stdout holds no JSON. Valid JSON without a message
    // gives nothing, so a result never leaks its arguments into the log.
    static string? Problem(string stdout, string stderr)
    {
        if (stdout.Trim().Length > 0)
        {
            try { return Find(JsonNode.Parse(stdout, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })); }
            catch (JsonException) { }
        }
        var text = stderr.Length > 0 ? stderr : stdout;
        // The usage text is what a wrong command or wrong arguments print; one line says so.
        if (text.StartsWith("oml version")) return "Usage shown: unknown command or wrong arguments";
        return text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
    }

    static string? Find(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v: return v.TryGetValue<string>(out var s) ? s : null;
            case JsonArray a: return a.Select(Find).FirstOrDefault(x => x is not null);
            case JsonObject o:
                // `oml run`: the problem of the first step that failed.
                if (o["results"] is JsonArray steps)
                    return steps.OfType<JsonObject>().Where(r => r["exitCode"]?.GetValue<int>() is > 0).Select(r => Find(r["output"])).FirstOrDefault(x => x is not null);
                foreach (var key in new[] { "error", "problems", "newErrors", "reopenNewErrors", "errors", "message", "samples" })
                    if (o.TryGetPropertyValue(key, out var child) && Find(child) is { } found) return found;
                return null;
            default: return null;
        }
    }

    // One short line without folders or secrets.
    internal static string Clean(string message)
    {
        foreach (var name in Secrets)
            if (Environment.GetEnvironmentVariable(name) is { Length: >= 4 } secret)
                message = message.Replace(secret, "[removed]", StringComparison.OrdinalIgnoreCase);
        // A drive or UNC path, however many folders (spaces allowed), shrinks to its last part.
        message = Regex.Replace(message, @"(?:[A-Za-z]:|\\\\[^\\/\s]+)[\\/](?:[^\\/:*?""<>|\r\n]*[\\/])*", "");
        message = Regex.Replace(message, @"\s+", " ").Trim();
        return message.Length <= MaxMessage ? message : message[..MaxMessage];
    }

    // `oml log summary [--file f]`: per command count, failures, median ms and the 5 most common failure messages.
    public static int Summary(string[] args)
    {
        string? file = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--file" && i + 1 < args.Length) file = args[++i];
            else if (i == 0 && args[i] == "summary") continue;
            else return Fail("Usage: oml log summary [--file <usage.jsonl>]");
        }
        if (args.Length == 0 || args[0] != "summary") return Fail("Usage: oml log summary [--file <usage.jsonl>]");
        file = Path.GetFullPath(file ?? FilePath ?? DefaultPath);
        if (!File.Exists(file)) return Fail($"No usage log at {file}. Set OML_LOG=1 (or OML_LOG=<file>) and use oml; each command then adds a line.");

        var rows = new List<(string cmd, int exit, long ms, string? error)>();
        foreach (var text in File.ReadLines(file))
        {
            try
            {
                if (JsonNode.Parse(text) is JsonObject o && o["cmd"]?.GetValue<string>() is { } cmd)
                    rows.Add((cmd, o["exit"]?.GetValue<int>() ?? 0, o["ms"]?.GetValue<long>() ?? 0, o["error"]?.GetValue<string>()));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { } // a damaged line is skipped
        }
        var commands = rows.GroupBy(r => r.cmd).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var times = g.Select(r => r.ms).Order().ToArray();
            var median = times.Length % 2 == 1 ? times[times.Length / 2] : (times[times.Length / 2 - 1] + times[times.Length / 2]) / 2;
            var failed = g.Where(r => r.exit != 0).ToList();
            return new
            {
                command = g.Key, count = g.Count(), failures = failed.Count, medianMs = median,
                topFailures = failed.GroupBy(r => r.error ?? "(no message)").OrderByDescending(f => f.Count()).ThenBy(f => f.Key, StringComparer.Ordinal)
                    .Take(5).Select(f => new { message = f.Key, count = f.Count() }).ToArray(),
            };
        }).ToArray();
        Console.WriteLine(JsonOutput.Serialize(new { file, lines = rows.Count, commands }));
        return 0;
    }

    static int Fail(string message)
    {
        Console.WriteLine(JsonOutput.Serialize(new { error = message }));
        return 2;
    }

    // Passes everything through at once; keeps the first 64 KB of text for the problem message and counts every byte.
    sealed class Copy(TextWriter inner) : TextWriter
    {
        readonly StringBuilder text = new();
        public long Bytes { get; private set; }
        public string Text => text.ToString();
        public override Encoding Encoding => inner.Encoding;

        void Note(string value)
        {
            Bytes += Encoding.UTF8.GetByteCount(value);
            if (text.Length < KeepText) text.Append(value, 0, Math.Min(value.Length, KeepText - text.Length));
        }
        public override void Write(char value) { inner.Write(value); Note(value.ToString()); }
        public override void Write(string? value) { if (value is null) return; inner.Write(value); Note(value); }
        public override void Write(char[] buffer, int index, int count) { inner.Write(buffer, index, count); Note(new string(buffer, index, count)); }
        public override void Flush() => inner.Flush();
    }
}
