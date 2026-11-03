using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// Opt-in local usage log. Off unless OML_LOG is set: "1" writes %LOCALAPPDATA%\oml-cli\usage.jsonl, any other
// value (except "0") is the file to append to. One JSON line per command, and per step inside `oml run`:
// UTC time, command name, exit code, duration, output size, the module file NAME (no folder) and, for a failure,
// the first problem message cut to 200 characters with folders and secrets removed. For `oml evolve` and memory
// usefulness it also keeps: a hashed session id (OML_SESSION or the agent's session variable), the option NAMES
// (--module, never their values), the record ids a memory recall returned, the spec key of a failed spec, the id of
// `oml warn <id>`, and the words of a doc or spec search that found nothing good (cut to 100 characters, secrets
// masked). It never logs other arguments, spec contents, tokens, environment values or paths, and it sends nothing anywhere.
// Linked into OmlCli.Core as well (see the Core csproj): the launcher logs whole commands, Core logs run steps.
static class UsageLog
{
    const int MaxMessage = 200, MaxQuery = 100;
    const int KeepText = 64 * 1024;
    static readonly string[] ModuleExtensions = [".oml", ".oap", ".osp", ".xif"];
    // Commands whose second word is a verb worth telling apart in the summary (`lt status` vs `lt deploy`).
    static readonly Dictionary<string, string[]> Verbs = new()
    {
        ["lt"] = ["environments", "apps", "teams", "team-apps", "download", "tag", "deploy", "status", "abort", "continue"],
        ["app"] = ["list", "extract", "pack"], ["extension"] = ["inspect", "new"], ["font"] = ["woff2", "batch"],
        ["image"] = ["batch"], ["spec"] = ["check", "schema"], ["translations"] = ["export"], ["test"] = ["scaffold"], ["memory"] = ["init", "add", "recall", "list", "show", "history", "retract", "doctor", "handoff", "traps"],
        ["evolve"] = ["report", "propose", "show", "accept", "reject", "history", "undo"], ["skills"] = ["list", "add", "remove"],
    };
    // The agent's own session variables, read in this order; the value is hashed before it is written.
    static readonly string[] SessionVariables = ["OML_SESSION", "CLAUDE_SESSION_ID", "CLAUDE_CODE_SESSION_ID", "CODEX_SESSION_ID"];
    static readonly Regex OptionName = new(@"^--[a-z][a-z0-9-]{0,30}$", RegexOptions.CultureInvariant);
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
            if (Session() is { } session) line["session"] = session;
            var options = args.Skip(1).Where(a => OptionName.IsMatch(a)).Distinct(StringComparer.Ordinal).ToArray();
            if (options.Length > 0) line["opts"] = new JsonArray(options.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray());
            foreach (var (name, value) in Extras(args, exitCode, stdout)) line[name] = value;
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

    // The session: the first agent session variable that is set, hashed (8 bytes of SHA-256), so the log can group the
    // commands of one session without holding the agent's id. Null without one: `oml evolve` then splits by time gaps.
    static string? Session()
    {
        foreach (var name in SessionVariables)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 8)).ToLowerInvariant();
        return null;
    }

    // The learning fields, read from oml's own output (never from the arguments, except the id of `oml warn`).
    static IEnumerable<(string Name, JsonNode Value)> Extras(IReadOnlyList<string> args, int exitCode, string stdout)
    {
        if (args[0] == "warn" && args.Count > 1 && Regex.IsMatch(args[1], "^[A-Za-z][A-Za-z0-9_]{1,60}$")) yield return ("topic", args[1]);
        var memoryRecall = args[0] == "memory" && args.Count > 1 && args[1] == "recall";
        var search = args[0] is "doc" or "spec" && args.Contains("--search");
        if (!memoryRecall && !search && exitCode == 0) yield break;
        JsonNode? json = null;
        try { json = stdout.Trim().Length == 0 ? null : JsonNode.Parse(stdout); }
        catch (JsonException) { }
        if (json is not JsonObject o) yield break;
        if (memoryRecall && exitCode == 0)
        {
            var ids = new[] { "hits", "traps" }.SelectMany(k => o[k] as JsonArray ?? []).OfType<JsonObject>()
                .Select(h => h["id"] is JsonValue v && v.TryGetValue<int>(out var id) ? id : -1).Where(id => id >= 0).Distinct().ToArray();
            if (ids.Length > 0) yield return ("recalled", new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id)).ToArray()));
        }
        if (search && exitCode == 0 && o["query"] is JsonValue q && q.TryGetValue<string>(out var query)
            && (o["hits"] is not JsonArray hits || hits.Count == 0 || o["weak"] is JsonValue w && w.TryGetValue<bool>(out var weak) && weak))
        {
            var text = Clean(query);
            yield return ("query", text.Length <= MaxQuery ? text : text[..MaxQuery]);
            yield return ("found", false);
        }
        if (exitCode != 0 && SpecKey(o) is { } key) yield return ("specKey", key);
    }

    // The spec key of the first problem with a path (entities[0].attributes[1].type -> entities.attributes.type), at
    // most three parts of letters, so no value or element name reaches the log.
    static string? SpecKey(JsonObject o)
    {
        var path = (o["problems"] as JsonArray)?.OfType<JsonObject>().Select(p => p["path"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(s => s is not null);
        if (path is null) return null;
        var parts = Regex.Replace(path, @"\[[^\]]*\]", "").Split('.', StringSplitOptions.RemoveEmptyEntries).Where(p => Regex.IsMatch(p, "^[A-Za-z]{1,40}$")).Take(3).ToArray();
        return parts.Length == 0 ? null : string.Join('.', parts);
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
        message = MemorySecrets.Mask(message);
        return message.Length <= MaxMessage ? message : message[..MaxMessage];
    }

    // ---- reading the log (log summary, memory usefulness, oml evolve) ----

    internal sealed record Row(int Line, DateTime Ts, string Cmd, int Exit, long Ms, string? Module, string? Error, bool InRun,
        string? Session, string[] Opts, int[] Recalled, string? Query, string? SpecKey, string? Topic)
    {
        // The session for grouping: the logged one, else filled in by Sessions() from time gaps.
        public string Group { get; set; } = "";
    }

    // The lines of a usage log; a damaged line is skipped. Only the last maxBytes of a large file are read.
    internal static List<Row> Read(string file, long maxBytes = long.MaxValue)
    {
        var rows = new List<Row>();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var skipFirst = false;
        if (stream.Length > maxBytes) { stream.Seek(-maxBytes, SeekOrigin.End); skipFirst = true; }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var n = 0;
        while (reader.ReadLine() is { } text)
        {
            n++;
            if (skipFirst) { skipFirst = false; continue; } // the first line may be cut
            try
            {
                if (JsonNode.Parse(text) is not JsonObject o || o["cmd"]?.GetValue<string>() is not { } cmd) continue;
                DateTime.TryParse(o["ts"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var ts);
                string? S(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                rows.Add(new Row(n, ts, cmd, o["exit"]?.GetValue<int>() ?? 0, o["ms"]?.GetValue<long>() ?? 0, S("module"), S("error"), o["run"]?.GetValue<bool>() ?? false,
                    S("session"), (o["opts"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToArray() ?? [],
                    (o["recalled"] as JsonArray)?.Select(x => x?.GetValue<int>() ?? -1).Where(x => x >= 0).ToArray() ?? [], S("query"), S("specKey"), S("topic")));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { } // a damaged line is skipped
        }
        return rows;
    }

    // A gap this long without a command starts a new session, for lines without a logged session.
    internal static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(30);

    // Sets Group on every row: the logged session, else "t<n>" for runs of lines with no gap of 30 minutes. Rows in order.
    internal static List<Row> Sessions(List<Row> rows)
    {
        var n = 0;
        DateTime? last = null;
        foreach (var r in rows.Where(r => r.Session is null))
        {
            if (last is null || r.Ts - last > SessionGap) n++;
            last = r.Ts;
            r.Group = "t" + n;
        }
        foreach (var r in rows.Where(r => r.Session is not null)) r.Group = "s" + r.Session;
        return rows;
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

        var rows = Read(file).Select(r => (cmd: r.Cmd, exit: r.Exit, ms: r.Ms, error: r.Error)).ToList();
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
        TerminalOutput.PrintObject(new { file, lines = rows.Count, commands });
        return 0;
    }

    static int Fail(string message)
    {
        TerminalOutput.PrintObject(new { error = message });
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
