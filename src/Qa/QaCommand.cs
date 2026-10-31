using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli.Qa;

// `oml qa`: test a running OutSystems web app like a manual tester, in a browser of its own (see IQaEngine).
//   qa page <url>      what is on the screen, compactly: actions, inputs, tables, messages, the calls it made
//   qa run <steps>     run a QA file: open, click, fill, select, press, wait, screenshot, expect
static class QaCommand
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length < 2 || args[0] is not ("run" or "page")) throw new QaUsage(CliUsage.For("qa"));
            var options = Options(args[2..]);
            return args[0] == "run" ? RunFile(args[1], options).GetAwaiter().GetResult() : Page(args[1], options).GetAwaiter().GetResult();
        }
        catch (QaUsage e) { TerminalOutput.PrintObject(new { error = e.Message }); return 2; }
        catch (Exception e) when (e is CdpException or QaFailure or HttpRequestException or IOException or System.Net.WebSockets.WebSocketException or TaskCanceledException)
        {
            TerminalOutput.PrintObject(new { error = e.Message });
            return 2;
        }
    }

    sealed record Settings(Dictionary<string, string?> Values)
    {
        public bool Has(string name) => Values.ContainsKey(name);
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public int Int(string name, int fallback) => Get(name) is { } v ? int.TryParse(v, out var n) && n > 0 ? n : throw new QaUsage($"{name} takes a positive number.") : fallback;
    }

    static readonly string[] Valued = ["--base-url", "--width", "--height", "--timeout", "--out", "--screenshot", "--engine"];

    // auto: DevTools when the browser allows it, else UI Automation (a RemoteDebuggingAllowed=0 policy, or DevTools
    // that did not start). The note says why the fallback was taken and what it cannot check.
    static async Task<(IQaEngine Engine, string? Note)> Engine(Settings o, int width, int height)
    {
        var mode = o.Get("--engine") ?? "auto";
        if (mode is not ("auto" or "cdp" or "uia")) throw new QaUsage("--engine takes auto, cdp or uia.");
        const string Limits = "UI Automation sees what the screen shows; it has no network checks (call), JavaScript errors, key presses or css: targets.";
        if (mode == "uia") return (new UiaEngine(o.Has("--headed"), width, height), Limits);
        if (mode == "auto" && QaPolicy.BlocksDevTools() is { } policy)
            return (new UiaEngine(o.Has("--headed"), width, height), $"{policy}: using UI Automation. {Limits}");
        try { return (await CdpEngine.Start(o.Has("--headed"), width, height), null); }
        catch (QaUsage e) when (mode == "auto" && e.Message.Contains("DevTools", StringComparison.Ordinal))
        {
            return (new UiaEngine(o.Has("--headed"), width, height), $"{e.Message} Using UI Automation. {Limits}");
        }
    }
    static readonly string[] Flags = ["--headed", "--continue"];

    static Settings Options(string[] args)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            if (Flags.Contains(args[i])) values[args[i]] = null;
            else if (Valued.Contains(args[i]) && i + 1 < args.Length) values[args[i]] = args[++i];
            else throw new QaUsage($"Unknown or incomplete option {args[i]}. {CliUsage.For("qa")}");
        }
        return new Settings(values);
    }

    static async Task<int> RunFile(string file, Settings o)
    {
        if (!File.Exists(file)) throw new QaUsage($"No QA file at {file}.");
        JsonElement root;
        try { root = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }).RootElement; }
        catch (JsonException e) { throw new QaUsage($"{file} is not valid JSON: {e.Message}"); }
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
        var outDir = o.Get("--out") ?? Path.Combine(Path.GetTempPath(), "oml-qa", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var keepGoing = o.Has("--continue");

        var clock = Stopwatch.StartNew();
        var (engine, engineNote) = await Engine(o, width, height);
        using var browser = engine;
        var runner = new QaRunner(browser, baseUrl, timeout, outDir);
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
            ["ms"] = clock.ElapsedMilliseconds, ["baseUrl"] = baseUrl?.AbsoluteUri, ["engine"] = browser.Mode, ["engineNote"] = engineNote, ["steps"] = results,
        };
        if (o.Has("--out"))
        {
            Directory.CreateDirectory(outDir);
            var path = Path.Combine(outDir, "qa-report.json");
            await File.WriteAllTextAsync(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            report["report"] = Path.GetFullPath(path);
        }
        TerminalOutput.PrintNode(report);
        return failed == 0 ? 0 : 1;
    }

    static async Task<int> Page(string url, Settings o)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Scheme.StartsWith("http")) throw new QaUsage($"{url} is not an http(s) URL.");
        var clock = Stopwatch.StartNew();
        var (engine, engineNote) = await Engine(o, o.Int("--width", 1366), o.Int("--height", 900));
        using var browser = engine;
        await browser.Navigate(uri.AbsoluteUri);
        var ready = await browser.WaitReady(o.Int("--timeout", 15_000));
        var summary = JsonNode.Parse((await browser.Helper("summary")).GetRawText())!.AsObject();
        var now = await browser.Url();
        var path = new Uri(now).AbsolutePath.TrimEnd('/');
        var page = new JsonObject { ["url"] = now, ["screen"] = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]), ["engine"] = browser.Mode, ["engineNote"] = engineNote };
        foreach (var (key, value) in summary.ToList()) { summary.Remove(key); page[key] = value; }
        var calls = new JsonArray();
        foreach (var r in browser.Network.CallsSince(0).Where(r => r.Done))
        {
            var read = await browser.ReadCall(r);
            calls.Add(new JsonObject { ["name"] = r.Call, ["status"] = r.Status, ["rows"] = read.Rows, ["exception"] = read.Exception });
        }
        page["calls"] = calls;
        var (exceptions, console) = browser.ErrorsSince(0, 0);
        page["errors"] = new JsonArray([.. exceptions.Select(e => (JsonNode)("JavaScript error: " + e)),
            .. browser.Network.FailedSince(0).Select(r => (JsonNode)$"request failed: {r.Call ?? QaNetwork.Short(r.Url)} {r.Failed ?? "HTTP " + r.Status}")]);
        if (console.Count > 0) page["console"] = new JsonArray([.. console.Take(10).Select(c => (JsonNode)c)]);
        if (o.Get("--screenshot") is { } shot) page["screenshot"] = await browser.Screenshot(shot);
        page["readyMs"] = ready;
        page["ms"] = clock.ElapsedMilliseconds;
        TerminalOutput.PrintNode(Prune(page));
        return 0;
    }

    // Drops empty arrays and nulls, so an agent reads only what is there.
    static JsonObject Prune(JsonObject o)
    {
        foreach (var (key, value) in o.ToList())
            if (value is null || value is JsonArray { Count: 0 }) o.Remove(key);
            else if (value is JsonObject child) Prune(child);
            else if (value is JsonArray list) foreach (var item in list) if (item is JsonObject x) Prune(x);
        return o;
    }
}
