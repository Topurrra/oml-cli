using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli.Qa;

// `oml browser` (formerly `oml browser`, still accepted): the agent's browser and computer use for a running OutSystems web app, in a
// browser of its own (see IQaEngine).
//   browser page <url>         what is on the screen, compactly: actions, inputs, tables, messages, layout, calls, errors, a11y;
//                              --snapshot <file.json> also saves a record of it for browser diff
//   browser run <script>       run a browser script: open, click, fill, select, press, wait, screenshot, expect
//   browser record <url>       open a window, record what a person does in it, write a browser script
//   browser look <url> <el>    one element: widget path, box, computed values, the winning CSS rules (BrowserTools)
//   browser sweep <url>        the page at each supported width: overflow and its cause, layout, widgets per width
//   browser try <url> --css ..  a preview of planned UI injected into the running page (golden rule 1); nothing is saved
//   browser diff <a> <b|url>   what changed between two snapshots, as text
//   browser start | stop       keep one browser open between runs (QaSession): no cold start, logins stay
static class QaCommand
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new QaUsage(CliUsage.For("browser"));
            switch (args[0])
            {
                case "start": return QaSession.Start(Options(args[1..]));
                case "stop": return QaSession.Stop();
                case "serve" when args.Length >= 2: return QaSession.Serve(args[1], Options(args[2..]));
                case "record" when args.Length >= 2: return QaRecorder.Run(args[1], Options(args[2..])).GetAwaiter().GetResult();
                // Two snapshot files compare at once; a URL as the second side is the page now, taken by a browser.
                case "diff" when args.Length >= 3 && !(Uri.TryCreate(args[2], UriKind.Absolute, out var live) && live.Scheme.StartsWith("http")):
                    if (args.Length > 3) throw new QaUsage("diff takes two snapshot files, or a snapshot file and a URL.");
                    TerminalOutput.PrintNode(BrowserTools.Diff(BrowserTools.ReadSnapshot(args[1]), BrowserTools.ReadSnapshot(args[2])));
                    return 0;
                case "look" or "diff" when args.Length >= 3:
                case "run" or "page" or "sweep" or "try" when args.Length >= 2:
                    var options = Options(args[0] is "look" or "diff" ? args[3..] : args[2..]);
                    if (!options.Has("--no-session") && QaSession.Forward(args) is { } forwarded)
                    {
                        TerminalOutput.PrintNode(forwarded.Output);
                        return forwarded.Code;
                    }
                    var (code, output) = Execute(args, null).GetAwaiter().GetResult();
                    TerminalOutput.PrintNode(output);
                    return code;
                default: throw new QaUsage(CliUsage.For("browser"));
            }
        }
        catch (QaUsage e) { TerminalOutput.PrintObject(new { error = e.Message }); return 2; }
        catch (Exception e) when (e is CdpException or QaFailure or HttpRequestException or IOException or System.Net.WebSockets.WebSocketException or TaskCanceledException)
        {
            TerminalOutput.PrintObject(new { error = e.Message });
            return 2;
        }
    }

    // A run or page request, on its own browser or on the session's (shared, which stays open).
    public static async Task<(int Code, JsonObject Output)> Execute(string[] args, (IQaEngine Engine, string? Note)? shared)
    {
        var options = Options(args[0] is "look" or "diff" ? args[3..] : args[2..]);
        return args[0] switch
        {
            "run" => await RunFile(args[1], options, shared),
            "look" => await Closer(options, shared, b => BrowserTools.Look(b, args[1], args[2], options)),
            "sweep" => await Closer(options, shared, b => BrowserTools.Sweep(b, args[1], options)),
            "try" => await Closer(options, shared, b => BrowserTools.Try(b, args[1], options)),
            "diff" => await Closer(options, shared, async b =>
            {
                await b.Navigate(args[2]);
                await b.WaitReady(Math.Max(options.Int("--timeout", 15_000), 30_000));
                var after = JsonNode.Parse((await b.Helper("snapshot")).GetRawText())!.AsObject();
                return BrowserTools.Diff(BrowserTools.ReadSnapshot(args[1]), after);
            }),
            _ => await Page(args[1], options, shared),
        };
    }

    // look, sweep and diff: on the session's browser, or on one of their own that closes after.
    static async Task<(int, JsonObject)> Closer(Settings o, (IQaEngine Engine, string? Note)? shared, Func<IQaEngine, Task<JsonObject>> work)
    {
        var (browser, note) = shared ?? await Engine(o, o.Int("--width", 1366), o.Int("--height", 900));
        try
        {
            var result = await work(browser);
            if (note is not null) result["engineNote"] = note;
            if (shared is not null) result["session"] = true;
            return (result["error"] is null ? 0 : 2, result);
        }
        finally
        {
            if (shared is null) browser.Dispose();
            else if (browser is CdpEngine cdp && o.Has("--theme"))   // the session's browser goes back to the normal color scheme
                try { await cdp.Cdp.Send("Emulation.setEmulatedMedia", new { features = Array.Empty<object>() }); } catch (CdpException) { }
        }
    }

    public sealed record Settings(Dictionary<string, string?> Values)
    {
        public bool Has(string name) => Values.ContainsKey(name);
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public int Int(string name, int fallback) => Get(name) is { } v ? int.TryParse(v, out var n) && n > 0 ? n : throw new QaUsage($"{name} takes a positive number.") : fallback;
    }

    public static readonly string[] Valued = ["--base-url", "--width", "--height", "--timeout", "--out", "--screenshot", "--engine", "--save", "--style-conflicts",
        "--state", "--theme", "--widths", "--snapshot", "--css", "--html", "--append", "--js"];
    static readonly string[] Flags = ["--headed", "--continue", "--no-session"];

    public static Settings Options(string[] args)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            if (Flags.Contains(args[i])) values[args[i]] = null;
            else if (Valued.Contains(args[i]) && i + 1 < args.Length) values[args[i]] = args[++i];
            else throw new QaUsage($"Unknown or incomplete option {args[i]}. {CliUsage.For("browser")}");
        }
        return new Settings(values);
    }

    // auto: DevTools when the browser allows it, else UI Automation (a RemoteDebuggingAllowed=0 policy, or DevTools
    // that did not start). The note says why the fallback was taken and what it cannot check.
    public static async Task<(IQaEngine Engine, string? Note)> Engine(Settings o, int width, int height, bool? headed = null)
    {
        var mode = o.Get("--engine") ?? "auto";
        var show = headed ?? o.Has("--headed");
        if (mode is not ("auto" or "cdp" or "uia")) throw new QaUsage("--engine takes auto, cdp or uia.");
        const string Limits = "UI Automation sees what the screen shows; it has no network checks (call), JavaScript errors, key presses or css: targets.";
        if (mode == "uia") return (new UiaEngine(show, width, height), Limits);
        if (mode == "auto" && QaPolicy.BlocksDevTools() is { } policy)
            return (new UiaEngine(show, width, height), $"{policy}: using UI Automation. {Limits}");
        try { return (await CdpEngine.Start(show, width, height), null); }
        catch (QaUsage e) when (mode == "auto" && e.Message.Contains("DevTools", StringComparison.Ordinal))
        {
            return (new UiaEngine(show, width, height), $"{e.Message} Using UI Automation. {Limits}");
        }
    }

    static JsonElement ReadQaFile(string file)
    {
        if (!File.Exists(file)) throw new QaUsage($"No browser script at {file}.");
        try { return JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }).RootElement; }
        catch (JsonException e) { throw new QaUsage($"{file} is not valid JSON: {e.Message}"); }
    }

    static async Task<(int, JsonObject)> RunFile(string file, Settings o, (IQaEngine Engine, string? Note)? shared)
    {
        var root = ReadQaFile(file);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            throw new QaUsage($"{file}: give {{\"baseUrl\": \"https://<env>/<App>/\", \"steps\": [...]}}.");
        var baseText = o.Get("--base-url") ?? (root.TryGetProperty("baseUrl", out var b) ? b.GetString() : null);
        Uri? baseUrl = null;
        if (baseText is not null && !Uri.TryCreate(baseText.EndsWith('/') ? baseText : baseText + "/", UriKind.Absolute, out baseUrl))
            throw new QaUsage($"baseUrl {baseText} is not an absolute URL.");
        var viewport = root.TryGetProperty("viewport", out var vp) ? vp : default;
        var width = o.Int("--width", viewport.ValueKind == JsonValueKind.Object && vp.TryGetProperty("width", out var w) ? w.GetInt32() : 1366);
        var height = o.Int("--height", viewport.ValueKind == JsonValueKind.Object && vp.TryGetProperty("height", out var h) ? h.GetInt32() : 900);
        var timeout = o.Int("--timeout", root.TryGetProperty("timeout", out var t) ? t.GetInt32() : 15_000);
        var outDir = o.Get("--out") ?? Path.Combine(Path.GetTempPath(), "oml-browser", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var keepGoing = o.Has("--continue");

        var clock = Stopwatch.StartNew();
        var (browser, engineNote) = shared ?? await Engine(o, width, height);
        try
        {
            var save = o.Get("--save") is { } s ? Path.GetFullPath(s) : null;
            if (save is not null && (File.Exists(save) || string.Equals(save, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)))
                throw new QaUsage($"--save: {save} exists. Save to a new file; the file you ran stays as it is.");
            var runner = new QaRunner(browser, baseUrl, timeout, outDir, capture: save is not null);
            var results = new JsonArray();
            int passed = 0, failed = 0, n = 0;
            foreach (var step in steps.EnumerateArray())
            {
                n++;
                if (failed > 0 && !keepGoing) { results.Add(new JsonObject { ["n"] = n, ["skipped"] = true }); continue; }
                var r = await runner.Step(n, step);
                results.Add(r);
                if (r["ok"]!.GetValue<bool>()) passed++; else failed++;
            }
            var report = new JsonObject
            {
                ["ok"] = failed == 0, ["passed"] = passed, ["failed"] = failed, ["skipped"] = n - passed - failed,
                ["ms"] = clock.ElapsedMilliseconds, ["baseUrl"] = baseUrl?.AbsoluteUri, ["engine"] = browser.Mode, ["engineNote"] = engineNote,
                ["session"] = shared is null ? null : true, ["steps"] = results,
            };
            if (save is not null)
            {
                if (failed > 0) report["saved"] = "Nothing saved: a step failed. Fix it and run again with --save.";
                else
                {
                    await File.WriteAllTextAsync(save, Saved(root, steps, results, browser.Mode, baseUrl));
                    report["saved"] = save;
                    report["replay"] = $"oml browser run {save}";
                }
            }
            if (o.Has("--out"))
            {
                Directory.CreateDirectory(outDir);
                var path = Path.Combine(outDir, "browser-report.json");
                await File.WriteAllTextAsync(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                report["report"] = Path.GetFullPath(path);
            }
            return (failed == 0 ? 0 : 1, Prune(report));
        }
        finally { if (shared is null) browser.Dispose(); }
    }

    static async Task<(int, JsonObject)> Page(string url, Settings o, (IQaEngine Engine, string? Note)? shared)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Scheme.StartsWith("http")) throw new QaUsage($"{url} is not an http(s) URL.");
        var clock = Stopwatch.StartNew();
        var (browser, engineNote) = shared ?? await Engine(o, o.Int("--width", 1366), o.Int("--height", 900));
        try
        {
            // A session's browser keeps what earlier requests saw: count only what this page does.
            var seq = browser.Network.Seq;
            var (exceptionCount, consoleCount) = browser.ErrorCounts();
            await browser.Navigate(uri.AbsoluteUri);
            var ready = await browser.WaitReady(Math.Max(o.Int("--timeout", 15_000), 30_000));
            var summary = JsonNode.Parse((await browser.Helper("summary")).GetRawText())!.AsObject();
            var now = await browser.Url();
            var path = new Uri(now).AbsolutePath.TrimEnd('/');
            var page = new JsonObject { ["url"] = now, ["screen"] = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]), ["engine"] = browser.Mode, ["engineNote"] = engineNote };
            foreach (var (key, value) in summary.ToList()) { summary.Remove(key); page[key] = value; }
            var calls = new JsonArray();
            var slow = new JsonArray();
            foreach (var r in browser.Network.CallsSince(seq).Where(r => r.Done))
            {
                var read = await browser.ReadCall(r);
                calls.Add(new JsonObject { ["name"] = r.Call, ["status"] = r.Status, ["rows"] = read.Rows, ["exception"] = read.Exception, ["ms"] = r.Ms > 0 ? r.Ms : null });
                if (r.Ms >= SlowMs) slow.Add($"{r.Call} took {r.Ms} ms{(read.Rows is { } rows ? $" for {rows} rows" : "")}");
            }
            page["calls"] = calls;
            page["slowCalls"] = slow;
            page["a11y"] = await BrowserTools.A11y(browser);
            if (o.Get("--snapshot") is { } snapshot) page["snapshot"] = await BrowserTools.Snapshot(browser, snapshot);
            var boxes = await browser.Helper("boxes");
            page["layout"] = new JsonArray([.. QaLayout.Findings(boxes).Select(f => (JsonNode)f)]);
            // A browser window has a minimum width (about 500 px for Edge), so UI Automation cannot show a phone width.
            var viewport = boxes.GetProperty("viewport");
            page["viewport"] = new JsonObject { ["width"] = Math.Round(viewport.GetProperty("w").GetDouble()), ["height"] = Math.Round(viewport.GetProperty("h").GetDouble()) };
            var (exceptions, console) = browser.ErrorsSince(exceptionCount, consoleCount);
            page["errors"] = new JsonArray([.. exceptions.Select(e => (JsonNode)("JavaScript error: " + e)),
                .. browser.Network.FailedSince(seq).Select(r => (JsonNode)$"request failed: {r.Call ?? QaNetwork.Short(r.Url)} {r.Failed ?? "HTTP " + r.Status}")]);
            if (console.Count > 0) page["console"] = new JsonArray([.. console.Take(10).Select(c => (JsonNode)c)]);
            if (o.Get("--screenshot") is { } shot) page["screenshot"] = await browser.Screenshot(shot);
            if (o.Get("--style-conflicts") is { } prefix)
                page["styleConflicts"] = browser is CdpEngine cdp ? await QaStyle.Conflicts(cdp.Cdp, prefix)
                    : new JsonObject { ["error"] = "--style-conflicts needs DevTools; this browser runs under UI Automation (a policy blocks DevTools)." };
            page["session"] = shared is null ? null : true;
            page["readyMs"] = ready;
            page["ms"] = clock.ElapsedMilliseconds;
            return (0, Prune(page));
        }
        finally { if (shared is null) browser.Dispose(); }
    }

    // A passing run as a browser script to replay without an agent: the same steps, each action with a time limit from what it took
    // (three times as long, at least 2 seconds; never a fixed wait), and, where a step checked nothing, the screen it ended
    // on and the calls it made (DevTools only). Secrets stay "$env:NAME" references, as the file had them.
    static string Saved(JsonElement root, JsonElement steps, JsonArray results, string mode, Uri? baseUrl)
    {
        var file = JsonNode.Parse(root.GetRawText())!.AsObject();
        if (baseUrl is not null) file["baseUrl"] = baseUrl.AbsoluteUri;
        var saved = new JsonArray();
        var i = 0;
        foreach (var step in steps.EnumerateArray())
        {
            var s = JsonNode.Parse(step.GetRawText())!.AsObject();
            var r = results[i++]!.AsObject();
            var action = s.Select(p => p.Key).FirstOrDefault(k => k is "open" or "click" or "fill" or "select" or "press");
            if (action is not null)
            {
                var ms = r["ms"]?.GetValue<long>() ?? 0;
                s["maxMs"] ??= (long)Math.Ceiling(Math.Max(ms * 3, 2000) / 500.0) * 500;
                if (s["expect"] is null && action is not "fill")
                {
                    var expect = new JsonObject();
                    if (r["screen"]?.GetValue<string>() is { Length: > 0 } screen) expect["screen"] = screen;
                    if (mode == "cdp" && r["calls"] is JsonArray calls && calls.OfType<JsonObject>().Select(c => c["name"]?.GetValue<string>()).FirstOrDefault(n => n is { Length: > 0 }) is { } call)
                        expect["call"] = call;
                    if (expect.Count > 0) s["expect"] = expect;
                }
            }
            saved.Add(s);
        }
        file["steps"] = saved;
        file["saved"] = $"oml browser run --save, {DateTime.Now:yyyy-MM-dd HH:mm}: a passing run. Replay it with oml browser run; no agent needed.";
        return file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    const long SlowMs = 1000;   // a server call this slow is worth a look (an aggregate fetching too much, a missing index)

    // Drops empty arrays and nulls, so an agent reads only what is there.
    public static JsonObject Prune(JsonObject o)
    {
        foreach (var (key, value) in o.ToList())
            if (value is null || value is JsonArray { Count: 0 }) o.Remove(key);
            else if (value is JsonObject child) Prune(child);
            else if (value is JsonArray list) foreach (var item in list) if (item is JsonObject x) Prune(x);
        return o;
    }
}
