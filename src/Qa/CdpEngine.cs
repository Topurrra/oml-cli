using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace OmlCli.Qa;

// One Edge or Chrome with its own throwaway profile, driven over CDP; or, with --attach, one new tab in the user's own
// Chrome that runs with remote debugging (see Attach). Clicks and typing are real
// input events at the element's position (hit-tested first), so they go through the same path as a person's click,
// without moving the user's mouse. "Ready" means: the document loaded, no request in flight for a quiet moment.
sealed class CdpEngine : IQaEngine
{
    const int QuietMs = 350;      // no network activity for this long after the last request ended
    const int StuckMs = 8_000;    // a request open longer than this (long polling, analytics) no longer holds "ready"

    readonly Process? process;     // null when attached to the user's own browser (--attach)
    readonly string? profile;
    public Cdp Cdp { get; }
    // "127.0.0.1:<port>" when attached to the user's own Chrome: oml drives one new tab there and closes only that tab.
    public string? Attached { get; }
    readonly string? ownTarget;
    // A person can see the window: --headed, or an attached Chrome that is not headless (a handoff needs one).
    public bool Visible { get; }
    public QaNetwork Network { get; } = new();
    readonly List<string> exceptions = [];
    readonly List<string> console = [];
    readonly object gate = new();

    CdpEngine(Process? process, string? profile, Cdp cdp, bool visible, string? attached = null, string? ownTarget = null)
    {
        (this.process, this.profile, Cdp, Visible, Attached, this.ownTarget) = (process, profile, cdp, visible, attached, ownTarget);
    }

    public static async Task<CdpEngine> Start(bool headed, int width, int height)
    {
        var exe = Locations.BrowserPath() ?? throw new QaUsage("No Edge or Chrome found in the usual Program Files folders. Set OML_BROWSER to a browser .exe.");
        var profile = Path.Combine(Path.GetTempPath(), "oml-qa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = !headed, RedirectStandardError = true, RedirectStandardOutput = true };
        string[] args = ["--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1", "--user-data-dir=" + profile, "--no-first-run",
            "--no-default-browser-check", "--disable-background-networking", "--disable-sync", "--disable-extensions",
            "--disable-features=Translate,OptimizationHints,MediaRouter", $"--window-size={width},{height}"];
        foreach (var a in args) info.ArgumentList.Add(a);
        if (!headed) info.ArgumentList.Add("--headless=new");
        info.ArgumentList.Add("about:blank");
        var process = Process.Start(info) ?? throw new QaUsage("Could not start " + exe);
        process.ErrorDataReceived += (_, _) => { };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        try
        {
            var port = await DevToolsPort(profile, process);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list")).RootElement;
            var page = targets.EnumerateArray().FirstOrDefault(t => t.GetProperty("type").GetString() == "page");
            if (page.ValueKind != JsonValueKind.Object) throw new QaUsage("The browser started but has no page to drive.");
            var cdp = await Cdp.Connect(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), 10_000);
            return await Ready(new CdpEngine(process, profile, cdp, headed), width, height);
        }
        catch
        {
            Kill(process, profile);
            throw;
        }
    }

    static async Task<CdpEngine> Ready(CdpEngine browser, int width, int height)
    {
        browser.Cdp.Event += browser.OnEvent;
        foreach (var domain in new[] { "Page.enable", "Network.enable", "Runtime.enable", "Log.enable" }) await browser.Cdp.Send(domain);
        await browser.Cdp.Send("Emulation.setDeviceMetricsOverride", new { width, height, deviceScaleFactor = 1, mobile = width < 768 });
        return browser;
    }

    // --attach: the user's own Chrome, started with --remote-debugging-port, where they logged in (SSO, MFA). Only a
    // browser on this computer: the port, a ws:// or an http:// address on 127.0.0.1 or localhost. oml opens a new tab,
    // drives only that tab and closes only that tab; the user's tabs and the browser stay as they are.
    public static async Task<CdpEngine> Attach(string? endpoint, int width, int height)
    {
        var port = AttachPort(endpoint);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string product;
        try
        {
            var version = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version")).RootElement;
            product = version.TryGetProperty("Browser", out var b) ? b.GetString() ?? "" : "";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new QaUsage($"No browser with remote debugging on 127.0.0.1:{port}. Start Chrome with: chrome.exe --remote-debugging-port={port} --user-data-dir=<a separate profile folder>, log in there once, then attach again.");
        }
        // Chrome 111 and later want PUT for a new tab.
        using var created = await http.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/json/new?about:blank"));
        if (!created.IsSuccessStatusCode) throw new QaUsage($"The browser on 127.0.0.1:{port} did not open a new tab (HTTP {(int)created.StatusCode}).");
        var target = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var id = target.GetProperty("id").GetString()!;
        try
        {
            var socket = new Uri(target.GetProperty("webSocketDebuggerUrl").GetString()!);
            if (!Loopback(socket.Host)) throw new QaUsage($"The browser gave a DevTools address on {socket.Host}, not on this computer; oml does not connect to it.");
            var cdp = await Cdp.Connect(socket, 10_000);
            var visible = !product.Contains("Headless", StringComparison.OrdinalIgnoreCase);
            return await Ready(new CdpEngine(null, null, cdp, visible, $"127.0.0.1:{port}", id), width, height);
        }
        catch
        {
            try { using var _ = await http.GetAsync($"http://127.0.0.1:{port}/json/close/{id}"); }
            catch (Exception x) when (x is HttpRequestException or TaskCanceledException) { }
            throw;
        }
    }

    // The DevTools port of --attach: none given is 9222. A host that is not this computer is refused before any connection.
    public static int AttachPort(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return 9222;
        if (int.TryParse(endpoint, out var n)) return n is > 0 and < 65536 ? n : throw new QaUsage($"--attach: {endpoint} is not a port.");
        if (!Uri.TryCreate(endpoint.Contains("://") ? endpoint : "http://" + endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "http"))
            throw new QaUsage("--attach takes a port (9222), or a ws:// or http:// address on 127.0.0.1.");
        if (!Loopback(uri.Host)) throw new QaUsage($"--attach: {uri.Host} is not this computer. oml attaches only to a browser on 127.0.0.1 (localhost).");
        if (uri.IsDefaultPort) throw new QaUsage("--attach: give the port, such as ws://127.0.0.1:9222.");
        return uri.Port;
    }

    static bool Loopback(string host) => host is "127.0.0.1" or "localhost" or "[::1]" or "::1";

    // browser raw and the raw step: methods that act on the whole browser are refused. Attached, the browser is the
    // user's own: also no Target call on a tab that is not oml's, and no clearing of storage, cookies or cache.
    public static string? RawRefusal(string method, JsonElement parameters, bool attached, string? ownTarget)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(method, @"^[A-Z][A-Za-z]*\.[a-z][A-Za-z]*$"))
            return $"{method} is not a DevTools method name such as Page.reload or Runtime.evaluate.";
        if (method is "Browser.close" or "Browser.crash" or "Browser.crashGpuProcess")
            return $"{method} would close the whole browser. Use oml browser stop.";
        if (!attached) return null;
        if (method.StartsWith("Target.", StringComparison.Ordinal) && parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("targetId", out var t) && t.ToString() != ownTarget)
            return $"{method}: that tab is not oml's. Attached to your browser, oml acts only on its own tab.";
        if (method.StartsWith("Storage.clear", StringComparison.Ordinal) || method is "Network.clearBrowserCookies" or "Network.clearBrowserCache")
            return $"{method} would clear data of your own browser. Refused while attached.";
        return null;
    }

    // Any DevTools method on oml's page, after RawRefusal.
    public async Task<JsonElement> Raw(string method, JsonElement parameters)
    {
        if (RawRefusal(method, parameters, Attached is not null, ownTarget) is { } why) throw new QaUsage(why);
        return await Cdp.Send(method, parameters.ValueKind == JsonValueKind.Object ? parameters : null, 30_000);
    }

    // Chrome writes the port it picked to DevToolsActivePort in the profile; --remote-debugging-port=0 avoids clashes.
    static async Task<int> DevToolsPort(string profile, Process process)
    {
        var file = Path.Combine(profile, "DevToolsActivePort");
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 20_000)
        {
            if (process.HasExited) throw new QaUsage($"The browser exited at start (code {process.ExitCode}).");
            try
            {
                if (File.Exists(file) && File.ReadAllLines(file) is [var first, ..] && int.TryParse(first, out var port)) return port;
            }
            catch (IOException) { } // still being written
            await Task.Delay(50);
        }
        throw new QaUsage("The browser did not open its DevTools port in 20 seconds.");
    }

    void OnEvent(string method, JsonElement p)
    {
        Network.OnEvent(method, p);
        if (method == "Runtime.bindingCalled" && p.GetProperty("name").GetString() == RecordBinding)
            recorded.Add(p.GetProperty("payload").GetString()!);
        lock (gate)
        {
            if (method == "Runtime.exceptionThrown")
            {
                var d = p.GetProperty("exceptionDetails");
                var text = d.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var de) ? de.GetString() : d.GetProperty("text").GetString();
                exceptions.Add(First(text));
            }
            else if (method == "Runtime.consoleAPICalled" && p.GetProperty("type").GetString() == "error")
                console.Add(First(string.Join(" ", p.GetProperty("args").EnumerateArray().Select(a => a.TryGetProperty("value", out var v) ? v.ToString() : a.TryGetProperty("description", out var d) ? d.GetString() : ""))));
            else if (method == "Log.entryAdded" && p.GetProperty("entry").GetProperty("level").GetString() == "error")
                console.Add(First(p.GetProperty("entry").GetProperty("text").GetString()));
        }
    }

    static string First(string? text)
    {
        var line = (text ?? "").Split('\n')[0].Trim();
        return line.Length > 300 ? line[..300] : line;
    }

    // Exceptions and console errors since the given counts (the runner keeps the counts per step).
    public (List<string> Exceptions, List<string> Console) ErrorsSince(int exceptionCount, int consoleCount)
    {
        lock (gate) return (exceptions.Skip(exceptionCount).ToList(), console.Skip(consoleCount).ToList());
    }
    public (int Exceptions, int Console) ErrorCounts() { lock (gate) return (exceptions.Count, console.Count); }

    public async Task<JsonElement> Eval(string expression, int timeoutMs = 10_000)
    {
        var r = await Cdp.Send("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, timeoutMs);
        if (r.TryGetProperty("exceptionDetails", out var ex))
            throw new QaFailure("Page script failed: " + (ex.TryGetProperty("exception", out var e) && e.TryGetProperty("description", out var d) ? First(d.GetString()) : ex.GetProperty("text").GetString()));
        return r.GetProperty("result").TryGetProperty("value", out var v) ? v : default;
    }

    public Task<JsonElement> Helper(string method, params object?[] args) => Eval(QaScript.Call(method, args));

    const string Settled = """
        (() => {
          if (document.readyState !== 'complete') return false;
          const tc = document.getElementById('transitionContainer');
          if (!tc) return true;
          const s = [...tc.children].filter(c => c.classList.contains('screen-container'));
          return s.length === 0 || s.length === 1 && s[0].classList.contains('active-screen') && ![...s[0].classList].some(c => /-(enter|leave|exit)(-active)?$/.test(c));
        })()
        """;

    public async Task Navigate(string url)
    {
        var r = await Cdp.Send("Page.navigate", new { url }, 30_000);
        if (r.TryGetProperty("errorText", out var error)) throw new QaFailure($"Could not open {url}: {error.GetString()}");
    }

    // Ready: the document is complete and the network has been quiet for QuietMs (requests open longer than StuckMs
    // do not count). Returns how long it took; throws when the timeout passes first.
    public async Task<long> WaitReady(int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        await Task.Delay(60); // let a click's navigation or fetch start
        while (true)
        {
            var (inFlight, quietFor) = Network.State(StuckMs);
            if (inFlight == 0 && quietFor >= QuietMs)
            {
                // Complete, and no OutSystems screen transition running (see UiaPage.ScreenSettled).
                var state = await Eval(Settled);
                if (state.ValueKind == JsonValueKind.True) return clock.ElapsedMilliseconds;
            }
            if (clock.ElapsedMilliseconds > timeoutMs)
                throw new QaFailure($"The page was not ready in {timeoutMs} ms: {Network.Pending(StuckMs)}.");
            await Task.Delay(40);
        }
    }

    public string Mode => "cdp";
    public bool Alive => Cdp.Closed is null && process?.HasExited != true;

    // A real mouse click at the element's centre, after locate scrolled it into view and hit-tested it.
    public async Task Activate(JsonElement at)
    {
        var (x, y) = (at.GetProperty("x").GetDouble(), at.GetProperty("y").GetDouble());
        await Cdp.Send("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y });
        await Cdp.Send("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 });
        await Cdp.Send("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 });
    }

    // Focus and select the field's text, then type over it (an empty text deletes it), as a person would.
    public async Task Type(string text)
    {
        await Helper("focusLast");
        if (text.Length == 0) await Press("Backspace");
        else await Cdp.Send("Input.insertText", new { text });
    }

    public Task<QaNetwork.CallResult> ReadCall(QaNetwork.Request request) => QaNetwork.Read(Cdp, request);

    // Recording: a pointerdown listener in every document (also after navigations) asks the page helper what was
    // clicked, before the click acts, and sends it to oml through a DevTools binding.
    const string RecordBinding = "__omlqaRecord";
    readonly System.Collections.Concurrent.BlockingCollection<string> recorded = [];
    bool recording;

    public async Task<JsonElement?> NextClick(CancellationToken cancel)
    {
        if (!recording)
        {
            var listener = $$"""
                (() => {
                  const h = window.__omlqa || (window.__omlqa = {{QaScript.Helper}});
                  if (window.__omlqaRecording) return;
                  window.__omlqaRecording = true;
                  document.addEventListener('pointerdown', ev => { const s = h.recordAt(ev.target); if (s) window.{{RecordBinding}}(JSON.stringify(s)); }, true);
                })()
                """;
            await Cdp.Send("Runtime.addBinding", new { name = RecordBinding });
            await Cdp.Send("Page.addScriptToEvaluateOnNewDocument", new { source = listener });
            await Eval(listener);
            recording = true;
        }
        while (!cancel.IsCancellationRequested && Alive)
            if (recorded.TryTake(out var payload, 150)) return JsonDocument.Parse(payload).RootElement.Clone();
        return null;
    }

    static readonly Dictionary<string, (string Code, int Vk)> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = ("Enter", 13), ["Tab"] = ("Tab", 9), ["Escape"] = ("Escape", 27), ["Backspace"] = ("Backspace", 8),
        ["Delete"] = ("Delete", 46), ["ArrowDown"] = ("ArrowDown", 40), ["ArrowUp"] = ("ArrowUp", 38),
        ["ArrowLeft"] = ("ArrowLeft", 37), ["ArrowRight"] = ("ArrowRight", 39), ["Space"] = ("Space", 32),
    };

    public async Task Press(string key)
    {
        if (!Keys.TryGetValue(key, out var k)) throw new QaUsage($"press: unknown key {key}. Known: {string.Join(", ", Keys.Keys)}.");
        var name = key.Equals("Space", StringComparison.OrdinalIgnoreCase) ? " " : k.Code;
        var text = k.Vk == 13 ? "\r" : k.Vk == 32 ? " " : null;
        await Cdp.Send("Input.dispatchKeyEvent", new { type = text is null ? "rawKeyDown" : "keyDown", key = name, code = k.Code, windowsVirtualKeyCode = k.Vk, text });
        await Cdp.Send("Input.dispatchKeyEvent", new { type = "keyUp", key = name, code = k.Code, windowsVirtualKeyCode = k.Vk });
    }

    public Task<QaShot.Shot> Screenshot(string file, QaShot.Limit limit) => Capture(file, limit, null);

    // The window, or an area of the page in CSS pixels (look's element), shrunk to the limit after capture. (A clip scale would
    // skip the shrink, but its result depends on the device pixel ratio and on the zoom a phone emulation applies.)
    public async Task<QaShot.Shot> Capture(string file, QaShot.Limit limit, (double X, double Y, double Width, double Height)? area)
    {
        object request = area is { } a
            ? new { format = "png", captureBeyondViewport = true, clip = new { x = a.X, y = a.Y, width = a.Width, height = a.Height, scale = 1 } }
            : new { format = "png" };
        var r = await Cdp.Send("Page.captureScreenshot", request, 20_000);
        return await QaShot.Write(file, Convert.FromBase64String(r.GetProperty("data").GetString()!), limit);
    }

    public async Task<string> Url() => (await Eval("location.href")).GetString() ?? "";

    public void Dispose()
    {
        if (process is null)
        {
            // Attached: close oml's own tab only; the user's browser and tabs stay open.
            try { Cdp.Send("Page.close", null, 2_000).Wait(2_500); } catch (AggregateException) { }
            Cdp.Dispose();
            return;
        }
        try { Cdp.Send("Browser.close", null, 2_000).Wait(2_500); } catch (AggregateException) { }
        Cdp.Dispose();
        Kill(process, profile!);
    }

    static void Kill(Process process, string profile)
    {
        try { if (!process.WaitForExit(3_000)) process.Kill(true); } catch (InvalidOperationException) { }
        process.Dispose();
        // The profile is locked for a moment after the browser exits.
        for (var i = 0; i < 10 && Directory.Exists(profile); i++)
        {
            try { Directory.Delete(profile, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }
}

sealed class QaUsage(string message) : Exception(message);
sealed class QaFailure(string message) : Exception(message);
