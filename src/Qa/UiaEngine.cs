using System.Diagnostics;
using System.Text.Json;

namespace OmlCli.Qa;

// The fallback engine: a normal Edge or Chrome window (own throwaway profile, accessibility forced on) read and
// driven through Windows UI Automation, for machines where a RemoteDebuggingAllowed=0 policy blocks DevTools.
// The window sits left of every monitor unless --headed, so it never covers the user's work; Windows still gives it
// the keyboard focus when it opens, so the engine hands the foreground back to the window that had it.
// Clicks are UIA patterns (Invoke, Toggle, SelectionItem, ExpandCollapse, then the default action), typing is the
// Value pattern: no mouse, no keyboard. What it cannot do: network checks, JavaScript errors, key presses, css: targets.
sealed class UiaEngine : IQaEngine
{
    const int StableMs = 500;   // without events: the page's text and element count unchanged this long counts as ready
    const int QuietMs = 250;    // with events: no structure, name or value change this long, then one read confirms
    const int EventsTrustMs = 3_000; // a page that never stops changing (a clock, an animation): fall back to the text check
    static readonly int[] Watched = [Uia.Name, Uia.ValueValue];

    readonly Changes changes = new();
    bool listening;

    readonly string exe, profile;
    readonly bool headed;
    readonly int width, height;
    readonly IUIAutomation automation = Uia.Create();
    readonly IUIAutomationCacheRequest request;
    readonly IUIAutomationCondition documents;
    Process? browser;
    IntPtr window;
    UiaPage.Node? last;

    public string Mode => "uia";
    public bool Alive => browser is null || !browser.HasExited;
    public QaNetwork Network { get; } = new();

    public UiaEngine(bool headed, int width, int height)
    {
        exe = Locations.BrowserPath() ?? throw new QaUsage("No Edge or Chrome found in the usual Program Files folders. Set OML_BROWSER to a browser .exe.");
        (this.headed, this.width, this.height) = (headed, width, height);
        profile = Path.Combine(Path.GetTempPath(), "oml-qa-" + Guid.NewGuid().ToString("N"));
        request = automation.CreateCacheRequest();
        foreach (var p in UiaPage.Properties) request.AddProperty(p);
        request.SetTreeScope(Uia.TreeScopeSubtree);
        request.SetTreeFilter(automation.CreateTrueCondition());
        documents = automation.CreatePropertyCondition(Uia.ControlType, Uia.Document);
    }

    // Every open is a new window of the same browser (the old one is closed): UI Automation has no "go to URL".
    public async Task Navigate(string url)
    {
        var foreground = Win32.GetForegroundWindow();
        var before = browser is null ? [] : Win32.BrowserWindows(browser.Id);
        var (x, y) = headed ? (40, 40) : (Win32.GetSystemMetrics(Win32.SmXVirtualScreen) - width - 400, Win32.GetSystemMetrics(Win32.SmYVirtualScreen));
        // Redirected: the browser's own console lines (policy notices, GPU warnings) must not mix into oml's output.
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "--user-data-dir=" + profile, "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-extensions",
            "--force-renderer-accessibility", "--disable-features=CalculateNativeWinOcclusion,Translate", "--disable-backgrounding-occluded-windows",
            "--disable-renderer-backgrounding", $"--window-position={x},{y}", $"--window-size={width},{height}", "--new-window", url })
            info.ArgumentList.Add(a);
        var started = Process.Start(info) ?? throw new QaUsage("Could not start " + exe);
        started.OutputDataReceived += (_, _) => { };
        started.ErrorDataReceived += (_, _) => { };
        started.BeginOutputReadLine();
        started.BeginErrorReadLine();
        if (browser is null) browser = started; else started.Dispose(); // a later start hands the URL to the running browser and exits
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (browser.HasExited) throw new QaUsage($"The browser exited (code {browser.ExitCode}).");
            var fresh = Win32.BrowserWindows(browser.Id).Except(before).FirstOrDefault();
            if (fresh != IntPtr.Zero) { window = fresh; break; }
            if (clock.ElapsedMilliseconds > 20_000) throw new QaFailure($"The browser opened no window for {url} in 20 seconds.");
            await Task.Delay(50);
        }
        foreach (var old in before) Win32.PostMessage(old, Win32.WmClose, IntPtr.Zero, IntPtr.Zero);
        if (!headed && foreground != IntPtr.Zero && Win32.IsWindow(foreground)) Win32.SetForegroundWindow(foreground);
        (last, cached) = (null, null);
        Listen();
    }

    // Change events for the new window: the ready wait reads the page only once they go quiet. When registering
    // fails, the wait polls instead.
    void Listen()
    {
        try
        {
            automation.RemoveAllEventHandlers();
            var root = automation.ElementFromHandle(window);
            automation.AddStructureChangedEventHandler(root, Uia.TreeScopeSubtree, null, changes);
            automation.AddPropertyChangedEventHandlerNativeArray(root, Uia.TreeScopeSubtree, null, changes, Watched, Watched.Length);
            changes.Mark();
            listening = true;
        }
        catch (Exception e) when (Uia.Failed(e) || e is InvalidCastException or NotSupportedException) { listening = false; }
    }

    // Notes when the page last changed. UI Automation calls it on its own threads; it must not call UI Automation back.
    public sealed class Changes : IUIAutomationStructureChangedEventHandler, IUIAutomationPropertyChangedEventHandler
    {
        long last = Stopwatch.GetTimestamp();
        public void HandleStructureChangedEvent(IUIAutomationElement sender, int changeType, int[] runtimeId) => Mark();
        public void HandlePropertyChangedEvent(IUIAutomationElement sender, int propertyId, object newValue) => Mark();
        public void Mark() => Interlocked.Exchange(ref last, Stopwatch.GetTimestamp());
        public long Last => Interlocked.Read(ref last);
        public double QuietMs => Stopwatch.GetElapsedTime(Last).TotalMilliseconds;
    }

    // The tab's web document: the largest Document element in the window (the browser's own UI has none that size).
    // For a moment (a dropdown's list closing, a page replacing another) the window can show no document: retry for a second.
    IUIAutomationElement Document()
    {
        if (window == IntPtr.Zero) throw new QaFailure("No page is open: start with an open step.");
        for (var clock = Stopwatch.StartNew(); ; Thread.Sleep(50))
        {
            var docs = Uia.Items(automation.ElementFromHandle(window).FindAll(Uia.TreeScopeDescendants, documents)).ToList();
            var doc = docs.OrderByDescending(d => Uia.Current(d, Uia.BoundingRectangle) is double[] { Length: 4 } r ? r[2] * r[3] : 0).FirstOrDefault();
            if (doc is not null) return doc;
            if (clock.ElapsedMilliseconds > 1_000) throw new QaFailure("The browser window has no web page.");
        }
    }

    // One read serves every helper call until the page changes: with change events, a read taken after the last
    // event is still the page. Without events (or after an action of oml's own), every call reads again.
    UiaPage? cached;
    long cachedAt;
    UiaPage Read()
    {
        if (listening && cached is not null && changes.Last == cachedAt) return cached;
        var at = changes.Last;
        var page = new UiaPage(Document().BuildUpdatedCache(request));
        (cached, cachedAt) = (page, at);
        return page;
    }

    // Ready: the page has loaded, shows text, and stopped changing. With change events, "stopped" is QuietMs without an
    // event and a read with no event during it; without them (or on a page that never stops), the text and element
    // count unchanged for StableMs.
    public async Task<long> WaitReady(int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        await Task.Delay(120); // let the click's own changes start
        string? signature = null;
        long stableSince = 0;
        string why = "the page did not load";
        while (true)
        {
            try
            {
                var events = listening && clock.ElapsedMilliseconds < EventsTrustMs;
                if (!events || changes.QuietMs >= QuietMs)
                {
                    var before = changes.Last;
                    var page = Read();
                    var text = page.Text();
                    var now = $"{page.Url}|{page.All.Count}|{text.GetHashCode()}";
                    if (page.Busy || page.Url.Length == 0 || page.Url == "about:blank" || text.Length == 0) { why = page.Busy ? "the page is still loading" : "the page is empty"; signature = null; }
                    else if (!page.ScreenSettled) { why = "the OutSystems screen transition did not end"; signature = null; }
                    else if (events && changes.Last == before) return clock.ElapsedMilliseconds;
                    else if (now != signature) { signature = now; stableSince = clock.ElapsedMilliseconds; why = "the page kept changing"; }
                    else if (clock.ElapsedMilliseconds - stableSince >= StableMs) return clock.ElapsedMilliseconds;
                }
                else why = "the page kept changing";
            }
            catch (Exception e) when (e is QaFailure || Uia.Failed(e)) { why = e.Message; signature = null; }
            if (clock.ElapsedMilliseconds > timeoutMs) throw new QaFailure($"The page was not ready in {timeoutMs} ms: {why}.");
            await Task.Delay(40);
        }
    }

    public Task<JsonElement> Helper(string method, params object?[] args)
    {
        if (method == "focusLast") return Task.FromResult(JsonSerializer.SerializeToElement(true));
        if (method == "valueOfLast")
            return Task.FromResult(JsonSerializer.SerializeToElement(last is null ? null : Uia.Current(last.Element, Uia.ValueValue) as string ?? ""));
        if (method == "selectLast") return Choose((string)args[0]!);
        var page = Read();
        var result = method switch
        {
            "locate" => page.Locate((string)args[0]!, (int?)args[1], (bool)args[2]!, out last),
            "feedback" => page.Feedback(),
            "invalid" => page.Invalid(),
            "text" => JsonSerializer.SerializeToElement(page.Text()),
            "table" => page.Table((string)args[0]!, (int?)args[1]),
            "summary" => page.Summary(page.Root.Name),
            "boxes" => page.Boxes(),
            _ => throw new QaUsage("Unknown page helper " + method),
        };
        if (method == "locate" && last is { Offscreen: true, ScrollItem: true })
            try { Pattern<IUIAutomationScrollItemPattern>(last, Uia.ScrollItemPattern)?.ScrollIntoView(); }
            catch (Exception x) when (Uia.Failed(x)) { }
        return Task.FromResult(result);
    }

    static T? Pattern<T>(UiaPage.Node n, int id) where T : class => n.Element.GetCurrentPattern(id) as T;

    // In order: Invoke (buttons, links), Toggle (checkboxes, switches), SelectionItem (tabs, radios), ExpandCollapse,
    // and the element's default action. Each one is a semantic action in the page, not a mouse click.
    public Task Activate(JsonElement at)
    {
        var n = last ?? throw new QaFailure("Nothing located to click.");
        try
        {
            cached = null;
            if (n.Invoke && Pattern<IUIAutomationInvokePattern>(n, Uia.InvokePattern) is { } invoke) invoke.Invoke();
            else if (n.Toggle && Pattern<IUIAutomationTogglePattern>(n, Uia.TogglePattern) is { } toggle) toggle.Toggle();
            else if (n.SelectItem && Pattern<IUIAutomationSelectionItemPattern>(n, Uia.SelectionItemPattern) is { } select) select.Select();
            else if (n.Expand && Pattern<IUIAutomationExpandCollapsePattern>(n, Uia.ExpandCollapsePattern) is { } expand) expand.Expand();
            else if (Pattern<IUIAutomationLegacyIAccessiblePattern>(n, Uia.LegacyIAccessiblePattern) is { } legacy) legacy.DoDefaultAction();
            else throw new QaFailure($"{UiaPage.Txt(n)}: this element offers no click action to UI Automation.");
        }
        catch (Exception e) when (Uia.Failed(e)) { throw new QaFailure($"click failed: {e.Message}"); }
        return Task.CompletedTask;
    }

    // The Value pattern replaces the whole text and fires the input's events, so OutSystems sees the change.
    public Task Type(string text)
    {
        var n = last ?? throw new QaFailure("Nothing located to type into.");
        if (Pattern<IUIAutomationValuePattern>(n, Uia.ValuePattern) is not { } value) throw new QaFailure("This field does not accept text through UI Automation.");
        cached = null;
        try { value.SetValue(text); }
        catch (Exception e) when (Uia.Failed(e)) { throw new QaFailure($"typing failed: {e.Message}"); }
        return Task.CompletedTask;
    }

    // Chromium puts a dropdown's options in the tree only while its list is open: expand it, select the option (which
    // sets the value and fires change), and collapse it again if the selection left it open.
    async Task<JsonElement> Choose(string option)
    {
        var n = last;
        if (n is null || n.Type != Uia.ComboBox) return JsonSerializer.SerializeToElement(new { error = "not a dropdown (select)" });
        var expand = Pattern<IUIAutomationExpandCollapsePattern>(n, Uia.ExpandCollapsePattern);
        List<UiaPage.Node> options = [];
        for (var attempt = 0; attempt < 15 && options.Count == 0; attempt++)
        {
            options = new UiaPage(n.Element.BuildUpdatedCache(request)).All.Where(o => o.Type == Uia.ListItem).ToList();
            if (options.Count > 0) break;
            if (attempt == 0) try { expand?.Expand(); } catch (Exception x) when (Uia.Failed(x)) { }
            await Task.Delay(100);
        }
        cached = null;
        try { return Pick(options, option); }
        finally { try { expand?.Collapse(); } catch (Exception x) when (Uia.Failed(x)) { } }
    }

    static JsonElement Pick(List<UiaPage.Node> options, string option)
    {
        var pick = options.FirstOrDefault(o => o.Name.Trim() == option) ?? options.FirstOrDefault(o => o.Name.Contains(option, StringComparison.OrdinalIgnoreCase));
        if (pick is null) return JsonSerializer.SerializeToElement(new { error = "no option " + JsonSerializer.Serialize(option), options = options.Select(o => o.Name.Trim()).Take(20).ToList() });
        if (Pattern<IUIAutomationSelectionItemPattern>(pick, Uia.SelectionItemPattern) is not { } item) return JsonSerializer.SerializeToElement(new { error = "the option cannot be selected through UI Automation" });
        item.Select();
        return JsonSerializer.SerializeToElement(new { selected = pick.Name.Trim() });
    }

    public Task Press(string key) =>
        throw new QaFailure($"press {key}: key presses need the DevTools engine (UI Automation has no keyboard without taking the focus). Click the button instead.");

    public async Task<string> Screenshot(string file)
    {
        var png = Win32.CapturePng(window) ?? throw new QaFailure("Could not capture the browser window.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        await File.WriteAllBytesAsync(file, png);
        return Path.GetFullPath(file);
    }

    public Task<string> Url() => Task.FromResult(Uia.Current(Document(), Uia.ValueValue) as string ?? "");

    // Recording: keep the latest read of the page (cheap while nothing changes), and answer a click from the read
    // taken before it, so the target is what the person saw, not the screen the click led to.
    MouseHook? hook;
    public async Task<JsonElement?> NextClick(CancellationToken cancel)
    {
        hook ??= new MouseHook();
        UiaPage? before = null;
        while (!cancel.IsCancellationRequested)
        {
            if (!Win32.IsWindow(window)) return null;
            try { before = Read(); } catch (Exception e) when (e is QaFailure || Uia.Failed(e)) { }
            if (!hook.Next(out var point, 120)) { await Task.Yield(); continue; }
            if (MouseHook.WindowOf(point.X, point.Y) != window || before?.RecordAt(point.X, point.Y) is not { } click) continue;
            return click;
        }
        return null;
    }

    public Task<QaNetwork.CallResult> ReadCall(QaNetwork.Request request) => Task.FromResult(new QaNetwork.CallResult(null, null, "no network in UI Automation mode"));
    public (List<string> Exceptions, List<string> Console) ErrorsSince(int exceptionCount, int consoleCount) => ([], []);
    public (int Exceptions, int Console) ErrorCounts() => (0, 0);

    public void Dispose()
    {
        hook?.Dispose();
        if (listening) try { automation.RemoveAllEventHandlers(); } catch (Exception x) when (Uia.Failed(x)) { }
        if (browser is not null)
        {
            foreach (var w in Win32.BrowserWindows(browser.Id)) Win32.PostMessage(w, Win32.WmClose, IntPtr.Zero, IntPtr.Zero);
            try { if (!browser.WaitForExit(5_000)) browser.Kill(true); } catch (InvalidOperationException) { }
            browser.Dispose();
        }
        for (var i = 0; i < 10 && Directory.Exists(profile); i++)
        {
            try { Directory.Delete(profile, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
