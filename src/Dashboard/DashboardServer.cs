using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// The Platform server page, the Home overview and the code-mode programs on the Agent activity page.
// Same rules as DashboardApi.cs: arguments are built from checked input. `oml server` only reads the server: the
// check downloads into its own cache, a pull saves into a new file in the work folder, never over a file.
// The login comes from OML_SERVER_HOST, OML_SERVER_USER and OML_SERVER_PASSWORD (this process, else the Windows user
// variables); the page never gets the password, and Json() removes it from every answer as a second guard.
static partial class Dashboard
{
    static readonly string[] ServerVariables = ["OML_SERVER_HOST", "OML_SERVER_USER", "OML_SERVER_PASSWORD"];

    // A variable of this process, else the person's Windows user variable (HKCU\Environment): a dashboard started
    // before the variables were set still finds them, as `oml mcp` does.
    static string? ServerSetting(string name)
    {
        if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } set) return set;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment");
            return key?.GetValue(name) is string value && value.Length > 0 ? value : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    // Before a server command: the missing variables from the Windows user variables, in this process only.
    static void FillServerVariables()
    {
        foreach (var name in ServerVariables)
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)) && ServerSetting(name) is { } value)
                Environment.SetEnvironmentVariable(name, value);
    }

    static JsonObject ServerState()
    {
        var missing = ServerVariables.Where(n => ServerSetting(n) is null).ToArray();
        return new JsonObject
        {
            ["configured"] = missing.Length == 0,
            ["host"] = ServerSetting("OML_SERVER_HOST"),
            ["missing"] = new JsonArray(missing.Select(m => (JsonNode?)m).ToArray()),
        };
    }

    // ---- history: read from the cache that `oml server check` keeps, no login ----

    const int HistoryRuns = 90;

    // Every host folder in the cache that has a history, newest first; the configured host first when it has one.
    static List<(string host, string folder, DateTime changed)> HistoryHosts()
    {
        var root = ServerApi.CacheRoot();
        if (!Directory.Exists(root)) return [];
        var list = new List<(string, string, DateTime)>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var file = Path.Combine(dir, "history.jsonl");
                if (File.Exists(file)) list.Add((Path.GetFileName(dir), dir, File.GetLastWriteTimeUtc(file)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var configured = ServerSetting("OML_SERVER_HOST") is { } h ? Path.GetFileName(ServerApi.HostCacheFolder(h)) : null;
        return list.OrderByDescending(x => string.Equals(x.Item1, configured, StringComparison.OrdinalIgnoreCase)).ThenByDescending(x => x.Item3).ToList();
    }

    static List<JsonObject> HistoryRunsOf(string folder)
    {
        var file = Path.Combine(folder, "history.jsonl");
        var runs = new List<JsonObject>();
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                try { if (JsonNode.Parse(line) is JsonObject run && run["total"] is JsonObject) runs.Add(run); }
                catch (JsonException) { } // a damaged line is skipped
                if (runs.Count > HistoryRuns * 2) runs.RemoveRange(0, HistoryRuns);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return runs.TakeLast(HistoryRuns).ToList();
    }

    // What the page draws: per run the time, the applications, the totals and the module rows (no paths).
    static Reply ServerHistory(string? wanted)
    {
        var hosts = HistoryHosts();
        var body = new JsonObject
        {
            ["server"] = ServerState(),
            ["cache"] = ServerApi.CacheRoot(),
            ["hosts"] = new JsonArray(hosts.Select(x => (JsonNode?)x.host).ToArray()),
        };
        var chosen = wanted is null ? hosts.FirstOrDefault() : hosts.FirstOrDefault(x => string.Equals(x.host, wanted, StringComparison.OrdinalIgnoreCase));
        if (chosen.folder is null) { body["runs"] = new JsonArray(); return new Reply(200, body); }
        body["host"] = chosen.host;
        body["history"] = Path.Combine(chosen.folder, "history.jsonl");
        body["runs"] = new JsonArray(HistoryRunsOf(chosen.folder).Select((run, i) => (JsonNode?)new JsonObject
        {
            ["index"] = i,
            ["time"] = run["time"]?.DeepClone(),
            ["apps"] = run["apps"]?.DeepClone() ?? new JsonArray(),
            ["total"] = run["total"]!.DeepClone(),
            ["report"] = run["html"] is JsonValue v && v.TryGetValue<string>(out var html) && File.Exists(html),
            ["modules"] = new JsonArray((run["modules"] as JsonArray ?? []).OfType<JsonObject>().Select(m => (JsonNode?)new JsonObject
            {
                ["name"] = m["name"]?.DeepClone(), ["key"] = m["key"]?.DeepClone(), ["application"] = m["application"]?.DeepClone(),
                ["version"] = m["version"]?.DeepClone(), ["score"] = m["score"]?.DeepClone(),
                ["high"] = m["high"]?.DeepClone(), ["medium"] = m["medium"]?.DeepClone(), ["low"] = m["low"]?.DeepClone(),
            }).ToArray()),
        }).ToArray());
        return new Reply(200, body);
    }

    // The check report of one run: only a file that history.jsonl names inside the cache folder.
    static Reply ServerReport(JsonObject body)
    {
        var host = Str(body, "host") ?? throw new BadInput("Choose a server.");
        var index = Number(body, "index", 0, HistoryRuns - 1, -1);
        var chosen = HistoryHosts().FirstOrDefault(x => string.Equals(x.host, host, StringComparison.OrdinalIgnoreCase));
        if (chosen.folder is null) throw new BadInput($"No check history for {host}.");
        var runs = HistoryRunsOf(chosen.folder);
        if (index < 0 || index >= runs.Count) throw new BadInput("That run is not in the history.");
        var root = Path.GetFullPath(ServerApi.CacheRoot()) + Path.DirectorySeparatorChar;
        string? Inside(string key) => runs[index][key] is JsonValue v && v.TryGetValue<string>(out var p) && Path.GetFullPath(p).StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(p) ? p : null;
        var html = Inside("html") ?? throw new BadInput("The report of that run is gone from the cache. Run the check again.");
        return new Reply(200, Report(html, Inside("json")));
    }

    // ---- commands that log in to the server ----

    static async Task<Reply> ServerCheckRun(JsonObject body)
    {
        var args = new List<string> { "server", "check" };
        foreach (var app in body["apps"] as JsonArray ?? [])
            if (app is JsonValue v && v.TryGetValue<string>(out var name) && name.Trim().Length > 0) args.AddRange(["--app", LifeTimeName(name.Trim(), "an application")]);
        return Wrap(await Oml([.. args]));
    }

    static async Task<Reply> ServerModules(string? app) =>
        Wrap(app is null ? await Oml("server", "modules") : await Oml("server", "modules", "--app", LifeTimeName(app, "an application")));

    static async Task<Reply> ServerVersions(string? module) =>
        Wrap(await Oml("server", "versions", ElementName(module, "a module name")));

    // One version into a new file of the work folder: never over a file.
    static async Task<Reply> ServerPull(JsonObject body)
    {
        var module = ElementName(Str(body, "module"), "a module name");
        var version = body["version"] is null ? (int?)null : Number(body, "version", 1, 1_000_000, 0);
        var file = Path.Combine(NewWorkFolder("server"), module + (version is { } n ? $"-v{n}" : "") + ".oml");
        var args = new List<string> { "server", "pull", module };
        if (version is { } v) args.AddRange(["--version", v.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        args.AddRange(["--out", file]);
        var r = await Oml([.. args]);
        return Wrap(r, new JsonObject { ["path"] = File.Exists(file) ? file : null });
    }

    static readonly Regex VersionWord = new("^([1-9][0-9]{0,6}|published|latest)$");

    // `oml review --server <Module> <v1> <v2>`: the same report as the Review page, for two versions on the server.
    static async Task<Reply> ServerReview(JsonObject body)
    {
        var module = ElementName(Str(body, "module"), "a module name");
        string Version(string name) => Str(body, name) is { } v && VersionWord.IsMatch(v) ? v
            : throw new BadInput("A version is a number, published or latest.");
        var (from, to) = (Version("from"), Version("to"));
        if (from == to) throw new BadInput("Choose two different versions.");
        var folder = NewWorkFolder("review");
        var (md, html, notes) = (Path.Combine(folder, "review.md"), Path.Combine(folder, "review.html"), Path.Combine(folder, "release-notes.md"));
        var r = await Oml("review", "--server", module, from, to, "--md", md, "--out", html, "--notes", notes);
        return Wrap(r, await ReviewFiles(folder, md, html, notes));
    }

    // ---- Home: one cheap look at everything, no login and no Service Studio ----

    static Reply Overview()
    {
        var body = new JsonObject { ["server"] = ServerState(), ["lifeTime"] = new JsonObject { ["configured"] = LifeTimeConfigured() } };

        var log = new JsonObject { ["enabled"] = UsageLog.Enabled };
        if (UsageLog.FilePath is { } file && File.Exists(file))
        {
            var today = DateTime.Today;
            List<UsageLog.Row> rows;
            try { rows = UsageLog.Read(file, 4L * 1024 * 1024).Where(r => !r.InRun && r.Ts.ToLocalTime().Date == today).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { rows = []; }
            log["today"] = rows.Count;
            log["failed"] = rows.Count(r => r.Exit >= 2);
            log["last"] = rows.Count > 0 ? rows[^1].Ts.ToString("o") : null;
        }
        body["log"] = log;

        if (HistoryHosts() is [var first, ..])
        {
            var runs = HistoryRunsOf(first.folder);
            if (runs.Count > 0)
            {
                var last = runs[^1];
                int? Score(JsonObject run) => run["total"]?["score"] is JsonValue s && s.TryGetValue<int>(out var n) ? n : null;
                var key = (JsonObject run) => string.Join("|", (run["apps"] as JsonArray ?? []).Select(a => a?.ToString() ?? "")).ToLowerInvariant();
                var before = runs.Take(runs.Count - 1).LastOrDefault(r => key(r) == key(last));
                body["score"] = new JsonObject
                {
                    ["host"] = first.host, ["time"] = last["time"]?.DeepClone(), ["score"] = Score(last),
                    ["change"] = before is not null && Score(before) is { } b && Score(last) is { } a ? a - b : null,
                    ["modules"] = last["total"]?["modules"]?.DeepClone(), ["runs"] = runs.Count,
                };
            }
        }

        var project = CodeMode.ProjectRoot(Environment.CurrentDirectory);
        var runsList = CodeRuns(project, 20);
        body["code"] = new JsonObject
        {
            ["folder"] = project,
            ["recent"] = runsList.Count,
            ["waiting"] = runsList.Count(r => r["status"]?.GetValue<string>() == "paused"),
        };
        return new Reply(200, body);
    }

    // ---- code mode: the programs in <project>\.oml\codemode\executions, read only ----

    static Reply CodePrograms(string? typed)
    {
        var dir = typed is null ? Environment.CurrentDirectory : UserPath(typed, "a project folder");
        if (!Directory.Exists(dir)) throw new BadInput($"Folder not found: {dir}");
        var project = CodeMode.ProjectRoot(dir);
        return new Reply(200, new JsonObject
        {
            ["folder"] = project,
            ["executions"] = Path.Combine(project, ".oml", "codemode", "executions"),
            ["programs"] = new JsonArray(CodeRuns(project, 25).Select(r => (JsonNode?)r).ToArray()),
        });
    }

    const long CodeLogLimit = 8L * 1024 * 1024;

    // The newest programs, newest first: status, calls per tool, failed calls, and the calls that wait for a person.
    static List<JsonObject> CodeRuns(string project, int count)
    {
        var folder = Path.Combine(project, ".oml", "codemode", "executions");
        if (!Directory.Exists(folder)) return [];
        string[] files;
        try { files = Directory.GetFiles(folder, "ex_*.jsonl").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Take(count).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        var list = new List<JsonObject>();
        foreach (var file in files)
        {
            var row = new JsonObject { ["id"] = Path.GetFileNameWithoutExtension(file) };
            try
            {
                if (new FileInfo(file).Length > CodeLogLimit) { row["status"] = "unknown"; row["note"] = "The log is too large to read here. Use oml code get."; list.Add(row); continue; }
                JsonObject? start = null, end = null;
                string? lastPass = null;
                var calls = new SortedDictionary<int, JsonObject>();
                var approved = new HashSet<int>();
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    while (reader.ReadLine() is { } line)
                    {
                        JsonObject? entry;
                        try { entry = JsonNode.Parse(line) as JsonObject; }
                        catch (JsonException) { continue; } // the last line may be half written
                        switch (entry?["type"]?.ToString())
                        {
                            case "start": start = entry; break;
                            case "end": end = entry; break;
                            case "pass_start": lastPass = null; break;
                            case "pass_end": lastPass = entry["status"]?.ToString(); break;
                            case "call" when entry["seq"] is JsonValue s && s.TryGetValue<int>(out var seq): calls[seq] = entry; break;
                            case "approval":
                                foreach (var x in entry["seqs"] as JsonArray ?? []) if (x is JsonValue a && a.TryGetValue<int>(out var n)) approved.Add(n);
                                break;
                        }
                    }
                var status = end is not null ? end["status"]?.ToString() ?? "error" : lastPass == "paused" ? "paused" : "running";
                var tools = calls.Values.Where(c => c["kind"]?.ToString() == "tool").ToList();
                var code = start?["code"]?.ToString() ?? "";
                row["status"] = status;
                row["agent"] = start?["agent"]?.DeepClone();
                row["started"] = start?["ts"]?.DeepClone();
                row["ended"] = end?["ts"]?.DeepClone();
                row["calls"] = tools.Count;
                row["failed"] = tools.Count(c => c["state"]?.ToString() == "error");
                row["tools"] = new JsonObject(tools.GroupBy(c => c["name"]?.ToString() ?? "?").OrderByDescending(g => g.Count())
                    .Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count())));
                row["pending"] = new JsonArray(tools.Where(c => c["state"]?.ToString() == "pending" && !approved.Contains(c["seq"]!.GetValue<int>()))
                    .Select(c => (JsonNode?)new JsonObject { ["tool"] = c["name"]?.DeepClone(), ["command"] = CallText(c) }).ToArray());
                row["error"] = end?["error"] is JsonObject error ? error["message"]?.DeepClone() ?? error.DeepClone() : end?["error"]?.DeepClone();
                row["code"] = code.Length > 4000 ? code[..4000] + "\n..." : code;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { row["status"] = "unknown"; row["note"] = "Could not read the log."; }
            list.Add(row);
        }
        return list;
    }

    // An oml call as the command line a person would recognise; another tool as its name and arguments.
    static string CallText(JsonObject call)
    {
        var name = call["name"]?.ToString() ?? "?";
        if (name.StartsWith("oml.", StringComparison.Ordinal) && call["args"] is JsonObject callArgs && callArgs["args"] is JsonArray list)
            return CliText(list.Select(a => a?.ToString() ?? ""));
        var args = call["args"]?.ToJsonString() ?? "";
        return name + " " + (args.Length > 300 ? args[..300] + "..." : args);
    }
}
