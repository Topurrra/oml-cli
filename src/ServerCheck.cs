using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml server check`: the end-of-day environment check. It keeps the published version of each module in a cache
// folder per host (a module is downloaded again only when its published version id changed), runs oml check over the
// cached files, and appends one line per run to history.jsonl in that folder, so each module's score can be compared
// with the last run. Not scheduled: work laptops are off at night, so the agent runs it when the user ends the day.
// `oml review --server <Module> <v1> <v2>`: pulls two versions and runs the normal review between them.
// Both log in like the rest of oml server and never write to the server.
static partial class ServerApi
{
    internal const string ReviewUsage = "oml review --server <Module> <v1> <v2> [--keep <folder>] [--md <file.md>] [--out <file.html>] [--notes <file.md>] [--rules <oml-rules.json>]";
    const int ListedModules = 50;

    sealed record CheckedModule(string Name, string Key, string Application, int? Version, int VersionId, string File);

    // The module score: 100 with no findings, lower with more; a high finding weighs 10, a medium 3, a low 1. It never
    // reaches 0, so a module with many findings still shows whether it got better or worse.
    internal static int Score(int high, int medium, int low) => (int)Math.Round(10000.0 / (100 + 10 * high + 3 * medium + low));

    static int Check(Ctx ctx, List<string> apps, string? rules, string? outArg)
    {
        string? html = null;
        if (outArg is not null)
        {
            html = Path.GetFullPath(outArg);
            if (!html.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) throw new ServerError("--out must be an .html file: the check report.");
            Local(() => CheckInput.NewOutput(html));
        }
        var rulesFile = rules is null ? null : Path.GetFullPath(rules);
        if (rulesFile is not null) Local(() => CheckInput.ReadSettings(rulesFile));

        var client = ctx.Connect("Download", StudioPull);
        var all = client.Modules(StudioPull);
        var chosen = (apps.Count == 0
                ? all.OfType<JsonNode>().OrderBy(m => Text(Embedded(m, "application"), "name"), StringComparer.OrdinalIgnoreCase).ThenBy(m => Text(m, "name"), StringComparer.OrdinalIgnoreCase)
                : apps.SelectMany(a => AppModules(all, a)))
            .DistinctBy(m => Text(m, "key")).ToArray();
        string[] appNames = apps.Count == 0 ? [] : chosen.Select(m => Text(Embedded(m, "application"), "name")).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var cache = CacheFolder(ctx.Host!);
        var manifest = ReadManifest(cache);
        var sameHost = string.Equals(manifest["host"]?.GetValue<string>(), ctx.Host, StringComparison.OrdinalIgnoreCase);
        var runs = new List<CheckedModule>();
        var changed = new JsonArray();
        var skipped = new JsonArray();
        var failed = new JsonArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int downloaded = 0, cached = 0;
        foreach (var m in chosen)
        {
            var (name, key, app) = (Text(m, "name"), Text(m, "key"), Text(Embedded(m, "application"), "name"));
            if (!names.Add(name)) { skipped.Add(new JsonObject { ["name"] = name, ["reason"] = "Another module has this name: oml check reads one module per name." }); continue; }
            var file = Path.Combine(cache, FileName(m, all));
            try
            {
                var (versionId, version) = PublishedVersion(m);
                if (versionId is not > 0) { skipped.Add(new JsonObject { ["name"] = name, ["reason"] = "The server lists no version of this module." }); continue; }
                if (Unchanged(manifest, ctx.Host!, file, key, versionId.Value) is { } same)
                {
                    version ??= Int(same["version"]);
                    cached++;
                }
                else
                {
                    version ??= NumberOf(client, key, versionId.Value);
                    if (sameHost && manifest["files"]?[Path.GetFileName(file)] is JsonObject old && old["key"]?.GetValue<string>() == key && Int(old["versionId"]) != versionId)
                    {
                        var last = Embedded(m, "lastVersion");
                        var change = new JsonObject { ["name"] = name, ["from"] = Int(old["version"]), ["to"] = version };
                        if (Int(Prop(last, "versionId")) == versionId) change["uploadedBy"] = Text(last, "uploaderUsername");
                        changed.Add(change);
                    }
                    TerminalProgress.Message($"  {downloaded + 1}: {name}");
                    var part = file + ".part";
                    try { if (File.Exists(part)) File.Delete(part); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ServerError($"Could not replace {part}. Check that no other program has it open."); }
                    var bytes = client.SourceCode(key, versionId.Value, part);
                    try { File.Move(part, file, overwrite: true); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ServerError($"Could not replace {file} in the cache. Check that no other program has it open."); }
                    Remember(manifest, ctx.Host!, file, m, versionId.Value, version, bytes);
                    WriteManifest(cache, manifest);
                    downloaded++;
                }
                runs.Add(new(name, key, app, version, versionId.Value, file));
            }
            catch (ServerError e) { failed.Add(new JsonObject { ["name"] = name, ["reason"] = e.Message }); }
        }
        if (runs.Count == 0) throw new ServerError("No module could be downloaded, so nothing was checked." + (failed.Count > 0 ? $" First failure: {failed[0]!["reason"]}" : ""));

        // oml check over the cached files, with the last run's report as the baseline (status new = since the last run).
        var reports = Path.Combine(cache, "reports");
        CreateFolder(reports);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var json = Path.Combine(reports, $"check-{stamp}.json");
        for (var n = 2; File.Exists(json) || html is null && File.Exists(Path.ChangeExtension(json, ".html")); n++) json = Path.Combine(reports, $"check-{stamp}-{n}.json");
        html ??= Path.ChangeExtension(json, ".html");
        var historyFile = Path.Combine(cache, "history.jsonl");
        var history = ReadHistory(historyFile);
        // The last run over the same applications: the baseline of the findings and of the environment score.
        var appsKey = string.Join("|", appNames).ToLowerInvariant();
        var lastSame = history.LastOrDefault(h => string.Join("|", (h["apps"] as JsonArray ?? []).Select(a => a?.GetValue<string>() ?? "")).ToLowerInvariant() == appsKey);
        var baseline = lastSame?["json"] is JsonValue b && b.TryGetValue<string>(out var path) && File.Exists(path) ? path : null;
        var args = new List<string> { "check" };
        foreach (var r in runs) args.AddRange(["--in", r.File]);
        args.AddRange(["--json", json, "--out", html]);
        if (rulesFile is not null) args.AddRange(["--rules", rulesFile]);
        if (baseline is not null) args.AddRange(["--baseline", baseline]);
        TerminalProgress.Message($"Checking {runs.Count} module(s)...");
        var (_, text) = CaptureCore(args.ToArray());
        if (!File.Exists(json)) throw new ServerError("oml check did not run: " + CoreError(text));
        var report = JsonNode.Parse(File.ReadAllText(json)) as JsonObject ?? throw new InvalidOperationException();

        // Findings per module (a module graph finding counts for its first module), then the score and its change.
        var checkedNames = (report["modules"] as JsonArray ?? []).Select(x => x?["name"]?.GetValue<string>() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var counts = (report["findings"] as JsonArray ?? []).GroupBy(f => f?["module"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (high: g.Count(f => Severity(f) == "high"), medium: g.Count(f => Severity(f) == "medium"), low: g.Count(f => Severity(f) == "low")), StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = history.Count - 1; i >= 0; i--)
            foreach (var x in history[i]["modules"] as JsonArray ?? [])
                if (x?["key"]?.GetValue<string>() is { } k && Int(x["score"]) is { } s) previous.TryAdd(k, s);
        var rows = new List<(JsonObject row, int rank, int score)>();
        var records = new JsonArray();
        int high = 0, medium = 0, low = 0;
        foreach (var r in runs)
        {
            if (!checkedNames.Contains(r.Name)) { skipped.Add(new JsonObject { ["name"] = r.Name, ["reason"] = SkipReason(report, r.File) }); continue; }
            var (h, me, l) = counts.GetValueOrDefault(r.Name);
            var score = Score(h, me, l);
            (high, medium, low) = (high + h, medium + me, low + l);
            int? delta = previous.TryGetValue(r.Key, out var before) ? score - before : null;
            var status = delta is null ? "new" : delta > 0 ? "better" : delta < 0 ? "worse" : "same";
            rows.Add((new JsonObject { ["name"] = r.Name, ["score"] = score, ["change"] = delta, ["status"] = status, ["high"] = h, ["medium"] = me, ["low"] = l },
                status switch { "worse" => 0, "new" => 1, "better" => 2, _ => 3 }, score));
            records.Add(new JsonObject
            {
                ["name"] = r.Name, ["key"] = r.Key, ["application"] = r.Application, ["version"] = r.Version, ["versionId"] = r.VersionId,
                ["score"] = score, ["high"] = h, ["medium"] = me, ["low"] = l,
            });
        }
        if (rows.Count == 0) throw new ServerError("oml check read none of the downloaded modules. Open the report for the reason: " + html);
        var total = (int)Math.Round(rows.Average(x => x.score));
        int? totalChange = lastSame?["total"]?["score"] is { } t && Int(t) is { } p ? total - p : null;

        var record = new JsonObject
        {
            ["time"] = DateTime.Now.ToString("o"), ["host"] = ctx.Host, ["apps"] = new JsonArray(appNames.Select(a => (JsonNode?)a).ToArray()),
            ["rules"] = rulesFile, ["json"] = json, ["html"] = html,
            ["total"] = new JsonObject { ["score"] = total, ["modules"] = rows.Count, ["high"] = high, ["medium"] = medium, ["low"] = low },
            ["modules"] = records,
        };
        try { File.AppendAllText(historyFile, record.ToJsonString() + "\n"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ServerError($"Could not write {historyFile}. Check that the folder is writable."); }

        var ordered = rows.OrderBy(x => x.rank).ThenBy(x => x.score).Select(x => x.row).ToList();
        var count = (string status) => rows.Count(x => x.row["status"]!.GetValue<string>() == status);
        var summary = new JsonObject
        {
            ["modules"] = rows.Count, ["score"] = total, ["change"] = totalChange,
            ["worse"] = count("worse"), ["better"] = count("better"), ["same"] = count("same"), ["new"] = count("new"),
            ["versionChanged"] = changed.Count, ["downloaded"] = downloaded, ["cached"] = cached,
            ["high"] = high, ["medium"] = medium, ["low"] = low,
        };
        if (baseline is not null && report["summary"]?["new"] is { } newFindings) summary["newFindings"] = newFindings.DeepClone();
        var result = new JsonObject { ["host"] = ctx.Host, ["apps"] = apps.Count == 0 ? "all" : new JsonArray(appNames.Select(a => (JsonNode?)a).ToArray()), ["summary"] = summary };
        if (changed.Count > 0) result["versionChanged"] = changed;
        result["modules"] = new JsonArray(ordered.Take(ListedModules).Select(x => (JsonNode?)x).ToArray());
        if (ordered.Count > ListedModules) result["moreModules"] = ordered.Count - ListedModules;
        if (skipped.Count > 0) result["skipped"] = skipped;
        if (failed.Count > 0) result["failed"] = failed;
        result["report"] = html;
        result["json"] = json;
        result["history"] = historyFile;
        var worse = ordered.Where(x => x["status"]!.GetValue<string>() == "worse").Select(x => x["name"]!.GetValue<string>()).ToArray();
        result["next"] = worse.Length > 0
            ? $"Tell the user which modules got worse ({string.Join(", ", worse.Take(5))}{(worse.Length > 5 ? ", ..." : "")}). Their new findings are in {html} (status new)."
            : $"Tell the user the environment score {total}{(totalChange is { } c ? $" ({(c >= 0 ? "+" : "")}{c} since the last run)" : "")}. Details: {html}";
        ctx.Emit(result);
        return 0;
    }

    static string? Severity(JsonNode? finding) => finding?["severity"]?.GetValue<string>();

    // Why oml check did not read a file: its entry in summary.skipped, else a pointer to the report's warnings.
    static string SkipReason(JsonObject report, string file)
    {
        foreach (var s in report["summary"]?["skipped"] as JsonArray ?? [])
            if (s?["input"]?.GetValue<string>() is { } input && string.Equals(Path.GetFullPath(input), file, StringComparison.OrdinalIgnoreCase))
                return s["reason"]?.GetValue<string>() ?? "oml check could not read it.";
        return "oml check did not read it; see the warnings in the report.";
    }

    // %LOCALAPPDATA%\oml-cli\server-cache\<host> (OML_SERVER_CACHE replaces the server-cache folder).
    static string CacheFolder(string host)
    {
        var root = Environment.GetEnvironmentVariable("OML_SERVER_CACHE") is { Length: > 0 } r ? Path.GetFullPath(r)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oml-cli", "server-cache");
        var folder = Path.Combine(root, string.Concat(host.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_')));
        CreateFolder(folder);
        return folder;
    }

    static List<JsonObject> ReadHistory(string file)
    {
        if (!File.Exists(file)) return [];
        try
        {
            return File.ReadLines(file).Select(line =>
            {
                try { return JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { return null; }
            }).OfType<JsonObject>().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ServerError($"Could not read {file}."); }
    }

    // `oml review --server <Module> <v1> <v2> [--keep <folder>] [review options]`: args without "review".
    public static int Review(string[] args) => Guarded(ctx =>
    {
        string? module = null;
        var versions = new List<string>();
        var options = new Dictionary<string, string>();
        var flags = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--server")
            {
                if (module is not null) throw new ServerError("Repeated option --server. " + ReviewUsage);
                for (var k = 0; k < 3; k++)
                {
                    if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--")) throw new ServerError("--server takes <Module> <v1> <v2>. " + ReviewUsage);
                    if (k == 0) module = args[i]; else versions.Add(args[i]);
                }
            }
            else if (a is "--keep" or "--md" or "--out" or "--notes" or "--rules" or "--prepare" or "--import")
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--")) throw new ServerError($"{a} needs a value. " + ReviewUsage);
                if (!options.TryAdd(a, args[i])) throw new ServerError($"Repeated option {a}. " + ReviewUsage);
            }
            else if (a == "--timings") flags.Add(a);
            else if (!a.StartsWith("--")) throw new ServerError("review --server takes <Module> <v1> <v2> in place of two module files. " + ReviewUsage);
            else throw new ServerError($"Unknown option {a}. " + ReviewUsage);
        }
        if (module is null) throw new ServerError(ReviewUsage);
        foreach (var v in versions)
            if (!(v.Equals("published", StringComparison.OrdinalIgnoreCase) || v.Equals("latest", StringComparison.OrdinalIgnoreCase) || int.TryParse(v, out var n) && n > 0))
                throw new ServerError($"'{v}' is not a version: use a version number from oml server versions {module}, or published or latest.");
        ReviewPreflight(options);
        ctx.Settings();
        var keep = options.TryGetValue("--keep", out var k1) ? Path.GetFullPath(k1) : null;
        if (keep is not null && File.Exists(keep)) throw new ServerError("--keep is a folder: the two versions are saved there.");

        var client = ctx.Connect("Download", StudioPull);
        var found = FindModule(client.Modules(StudioPull), module);
        var (key, name) = (Text(found, "key"), Text(found, "name"));
        var published = Int(Prop(found, "publishedVersionId"));
        var list = client.Versions(key);
        JsonNode Pick(string spec)
        {
            if (spec.Equals("latest", StringComparison.OrdinalIgnoreCase))
                return list.OfType<JsonNode>().MaxBy(x => Int(Prop(x, "versionNumber")) ?? 0) ?? throw new ServerError($"The server lists no version of module '{name}'.");
            if (spec.Equals("published", StringComparison.OrdinalIgnoreCase))
                return list.OfType<JsonNode>().FirstOrDefault(x => published is > 0 && Int(Prop(x, "versionId")) == published)
                    ?? throw new ServerError($"Module '{name}' has no published version on this server. Run oml server versions {name}.");
            return list.OfType<JsonNode>().FirstOrDefault(x => Int(Prop(x, "versionNumber")) == int.Parse(spec))
                ?? throw new ServerError($"Module '{name}' has no version {spec} on this server. Run oml server versions {name}.");
        }
        var (before, after) = (Pick(versions[0]), Pick(versions[1]));
        if (Int(Prop(before, "versionId")) == Int(Prop(after, "versionId")))
            throw new ServerError($"{versions[0]} and {versions[1]} are the same version ({Int(Prop(before, "versionNumber"))}): pick two different versions.");

        var folder = keep ?? Path.Combine(Path.GetTempPath(), "oml-server", "review-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            CreateFolder(folder);
            var manifest = keep is null ? new JsonObject { ["files"] = new JsonObject() } : ReadManifest(folder);
            var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string Fetch(JsonNode version)
            {
                var id = Int(Prop(version, "versionId")) ?? throw new ServerError($"The server lists a version of '{name}' without a version id.");
                var number = Int(Prop(version, "versionNumber"));
                var file = Path.Combine(folder, $"{safe}-v{number?.ToString() ?? "id" + id}.oml");
                if (File.Exists(file))
                {
                    if (Unchanged(manifest, ctx.Host!, file, key, id) is not null) return file;
                    throw new ServerError($"{file} exists and is not that version from this server. Delete it, or use another --keep folder.");
                }
                TerminalProgress.Message($"Downloading {name} version {number}...");
                var bytes = client.SourceCode(key, id, file);
                if (keep is not null)
                {
                    Remember(manifest, ctx.Host!, file, found, id, number, bytes);
                    WriteManifest(folder, manifest);
                }
                return file;
            }
            var (first, second) = (Fetch(before), Fetch(after));
            var core = new List<string> { "review", first, second };
            foreach (var (option, value) in options)
                if (option != "--keep") core.AddRange([option, value]);
            core.AddRange(flags);
            var (code, text) = CaptureCore(core.ToArray());
            JsonObject? output = null;
            try { output = JsonNode.Parse(text) as JsonObject; }
            catch (JsonException) { }
            if (output is null)
            {
                Console.Out.Write(text);
                return code;
            }
            JsonObject Side(JsonNode version, string file)
            {
                var side = VersionRow(version, published);
                if (keep is not null) side["file"] = file;
                return side;
            }
            var server = new JsonObject
            {
                ["host"] = ctx.Host, ["module"] = name, ["key"] = key, ["application"] = Text(Embedded(found, "application"), "name"),
                ["before"] = Side(before, first), ["after"] = Side(after, second),
            };
            // The review's own output, with `server` after `module`.
            var items = output.ToList();
            output.Clear();
            var result = new JsonObject();
            foreach (var (k, v) in items)
            {
                result[k] = v;
                if (k == "module") result["server"] = server;
            }
            if (result["server"] is null) result["server"] = server;
            ctx.Emit(result);
            return code;
        }
        finally
        {
            if (keep is null) RemoveReviewFolder(folder);
        }
    });

    // The review's checks of its own options, before the login and the downloads (Review.cs checks them again).
    static void ReviewPreflight(Dictionary<string, string> options)
    {
        if (options.ContainsKey("--prepare") && options.Keys.Any(k => k is not ("--prepare" or "--keep")))
            throw new ServerError("--prepare writes only the review packet: use it without --md, --out, --notes, --rules and --import.");
        if (options.ContainsKey("--import") && options.Keys.Any(k => k is not ("--import" or "--rules" or "--keep")))
            throw new ServerError("--import takes only --rules.");
        var outputs = new List<string>();
        foreach (var option in new[] { "--md", "--out", "--notes" })
        {
            if (!options.TryGetValue(option, out var value)) continue;
            var full = Path.GetFullPath(value);
            outputs.Add(full);
            if (option == "--md" && IsReviewFile(full)) continue;
            Local(() => CheckInput.NewOutput(full));
        }
        if (outputs.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) throw new ServerError("--md, --out and --notes need different paths.");
        if (options.TryGetValue("--rules", out var rules)) Local(() => CheckInput.ReadSettings(Path.GetFullPath(rules)));
        if (options.TryGetValue("--import", out var import) && !File.Exists(import)) throw new ServerError($"Findings file not found: {Path.GetFullPath(import)}");
    }

    // An earlier review report (first line "# Review: "), which --md may replace, as in oml review.
    static bool IsReviewFile(string path)
    {
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)) return false;
        try { using var reader = new StreamReader(path); return reader.ReadLine()?.StartsWith("# Review: ", StringComparison.Ordinal) == true; }
        catch (IOException) { return false; }
    }

    // Only a folder this command made: %TEMP%\oml-server\review-<random>.
    static void RemoveReviewFolder(string folder)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "oml-server"));
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(folder)), parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(folder).StartsWith("review-", StringComparison.Ordinal)) return;
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static void Local(Action check)
    {
        try { check(); }
        catch (CheckInputError e) { throw new ServerError(e.Message); }
    }

    // A Core command with its stdout collected as machine JSON; stderr (progress, Service Studio problems) passes through.
    static (int Code, string Output) CaptureCore(string[] args)
    {
        var previous = Console.Out;
        var output = new StringWriter();
        var inRun = TerminalOutput.InRun;
        TerminalOutput.InRun = true;
        Console.SetOut(output);
        try { return (Program.RunCore(args), output.ToString()); }
        finally
        {
            Console.SetOut(previous);
            TerminalOutput.InRun = inRun;
        }
    }

    static string CoreError(string text)
    {
        try { if (JsonNode.Parse(text) is JsonObject o && o["error"] is JsonValue e && e.TryGetValue<string>(out var s)) return s; }
        catch (JsonException) { }
        var plain = text.Trim();
        return plain.Length == 0 ? "no output (see the messages above)." : plain.Length > 300 ? plain[..300] + "..." : plain;
    }
}
