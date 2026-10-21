using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OmlCli;

// The automatic checks of `oml preview`. They read strings (labels, CSS, JavaScript), so each detector can be tested without a module.
// They report; they never fail the command.
static class PreviewChecks
{
    internal static List<Preview.Check> Run(List<(string owner, string kind, string text)> labels, List<(string owner, string text)> literals,
        List<(string owner, List<string> rootClasses, bool hasRoot)> roots, List<(string owner, string node, string code)> js,
        List<(string owner, string css)> styles)
    {
        var css = styles.Where(s => s.owner != "(theme)").ToList();
        var allCss = string.Join("\n", css.Select(s => s.css));

        var badLabels = labels.Where(l => DefaultLabel(l.text)).Select(l => $"{l.owner}: {l.kind} \"{l.text}\"").Distinct().ToList();
        var canvas = js.Where(j => j.code.Contains("getContext(")).Select(j => j.owner).Distinct()
            .Where(owner => roots.FirstOrDefault(r => r.owner == owner) is not { hasRoot: true } r || !HasHeightRule(allCss, r.rootClasses))
            .Select(o => o + (roots.FirstOrDefault(r => r.owner == o) is { hasRoot: true } ? ": root container has no height, min-height or aspect-ratio rule" : ": no root Container to give a height")).ToList();
        var demo = literals.Where(l => Regex.IsMatch(l.text, @"demo content|lorem ipsum", RegexOptions.IgnoreCase)).Select(l => $"{l.owner}: \"{l.text}\"").Distinct().ToList();
        var colours = css.Sum(s => HardCodedColours(s.css, allCss));
        var listeners = js.SelectMany(j => LeakedListeners(j.code).Select(l => $"{j.owner}/{j.node}: {l}")).ToList();
        var globals = css.SelectMany(s => GlobalSelectors(s.css).Select(g => $"{s.owner}: {g}")).Distinct().ToList();

        return
        [
            new("a", "Link and Button texts are real labels, not default text", badLabels.Count == 0, badLabels.Count == 0 ? null : string.Join("; ", badLabels) + ". Replace the seeded text (\"link\", \"Button\") instead of adding to it."),
            new("b", "Canvas has a sized parent", canvas.Count == 0, canvas.Count == 0 ? null : string.Join("; ", canvas) + ". A canvas with no parent height renders as a thin line."),
            new("c", "No placeholder content (Demo content, Lorem ipsum)", demo.Count == 0, demo.Count == 0 ? null : string.Join("; ", demo)),
            new("d", "Colours come from CSS variables where the CSS uses theme variables", colours == 0, colours == 0 ? null : $"{colours} hard-coded colour value(s) in rules that do not use var(--...). They will not follow the theme."),
            new("e", "Every window/document listener has a matching remove", listeners.Count == 0, listeners.Count == 0 ? null : string.Join("; ", listeners)),
            new("f", "Stylesheet rules are scoped to a class", globals.Count == 0, globals.Count == 0 ? null : string.Join("; ", globals) + ". Global selectors change the whole app."),
        ];
    }

    // "link" or "Button" is what the toolbox seeds. A label that is exactly that, or that has more text glued after it ("linkCopy spec"), is a bug.
    internal static bool DefaultLabel(string text)
    {
        var t = text.Trim();
        return t is "link" or "Button" || Regex.IsMatch(t, @"^(link|Button)(?![a-z\s])");
    }

    internal static string StripComments(string css) => Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);

    // Innermost rule blocks: "selector { declarations }". At-rule preludes (@media ...) never match because they hold a nested block.
    internal static List<(string selector, string body)> Rules(string css) =>
        Regex.Matches(StripComments(css), @"([^{}]+)\{([^{}]*)\}").Select(m => (m.Groups[1].Value.Trim(), m.Groups[2].Value)).ToList();

    internal static bool HasHeightRule(string css, IEnumerable<string> classes)
    {
        foreach (var c in classes)
            foreach (var (selector, body) in Rules(css))
                if (Regex.IsMatch(selector, @"\." + Regex.Escape(c) + @"(?![\w-])")
                    && Regex.IsMatch(body, @"(?<![\w-])(height|min-height|aspect-ratio)\s*:"))
                    return true;
        return false;
    }

    // Counts colour literals in declarations that do not use var(), but only when the stylesheets use theme variables somewhere.
    internal static int HardCodedColours(string css, string allCss)
    {
        if (!allCss.Contains("var(--")) return 0;
        var count = 0;
        foreach (var (_, body) in Rules(css))
            foreach (var declaration in body.Split(';'))
            {
                var colon = declaration.IndexOf(':');
                if (colon < 0 || declaration.TrimStart().StartsWith("--") || declaration.Contains("var(") || declaration.Contains("url(")) continue;
                count += Regex.Matches(declaration[(colon + 1)..], @"#[0-9a-fA-F]{3,8}\b|\b(?:rgb|rgba|hsl|hsla|hwb|lab|lch|oklab|oklch)\(").Count;
            }
        return count;
    }

    // Selectors with no class, id or attribute that name a document-wide element.
    internal static List<string> GlobalSelectors(string css)
    {
        var global = new Regex(@"(^|[\s>+~])(\*|html|body|button|a|input|select|textarea|label|h[1-6]|p|ul|ol|li|img|table|th|td)(?![\w-])", RegexOptions.IgnoreCase);
        var found = new List<string>();
        foreach (var (selector, _) in Rules(css))
            foreach (var part in selector.Split(','))
            {
                var s = part.Trim();
                if (s.Length > 0 && !s.StartsWith("@") && !Regex.IsMatch(s, @"[.#\[]") && global.IsMatch(s)) found.Add(s);
            }
        return found;
    }

    // addEventListener on window/document with no removeEventListener for the same event in the same code.
    internal static List<string> LeakedListeners(string code) =>
        Regex.Matches(code, @"\b(window|document)\s*\.\s*addEventListener\s*\(\s*['""](\w+)['""]").Select(m => (target: m.Groups[1].Value, ev: m.Groups[2].Value))
            .Distinct()
            .Where(l => !Regex.IsMatch(code, @"\b" + l.target + @"\s*\.\s*removeEventListener\s*\(\s*['""]" + l.ev + @"['""]"))
            .Select(l => $"{l.target}.addEventListener('{l.ev}') has no removeEventListener").ToList();
}

// Headless Edge or Chrome, when installed. The page records its report on <html> and in <pre id="oml-preview-report">;
// --dump-dom reads them back.
static class PreviewBrowser
{
    // Page is the JSON the page wrote (errors, skipped, stubbed, console, blocked, navigation, steps, at). Notes say what went
    // wrong with the run itself (no report, a loop, a navigation, not finished).
    internal sealed record Outcome(string? Screenshot, string? Note, System.Text.Json.JsonElement? Page, bool? HorizontalOverflow, List<string> Notes);

    // No network even when a page navigates or opens a window (CSP cannot stop that): every request goes to a proxy that
    // is not there (port 9, loopback included), names do not resolve, and background, ping and WebRTC traffic is off.
    static readonly string[] Offline =
    [
        "--proxy-server=127.0.0.1:9", "--proxy-bypass-list=<-loopback>", "--host-resolver-rules=MAP * ~NOTFOUND",
        "--disable-background-networking", "--no-pings", "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
    ];

    // Real time on top of the virtual time budget before the browser is stopped. A page that loops forever never uses up its
    // virtual time, so this is what ends it. Both runs of a screenshot share TotalMs.
    const int MarginMs = 30_000;
    internal const int TotalMs = 150_000;

    // Without png only the --dump-dom run happens (for the interaction steps and the script report).
    internal static Outcome Capture(string html, string? png, int width, int height, int budgetMs)
    {
        var browser = Locations.BrowserPath();
        if (browser is null)
            return new Outcome(null, "No Edge or Chrome found in the usual Program Files folders (set OML_BROWSER to a browser .exe). Scripts and steps did not run; open the HTML file in a browser.", null, null, []);
        var profile = Path.Combine(Path.GetTempPath(), "oml-preview-" + Guid.NewGuid().ToString("N"));
        var notes = new List<string>();
        var clock = Stopwatch.StartNew();
        var limitMs = Math.Min(budgetMs + MarginMs, TotalMs);
        var loop = $"The browser did not finish in {limitMs / 1000} seconds: a script or JavaScript node may loop forever.";
        try
        {
            var url = new Uri(html).AbsoluteUri;
            // --enable-logging lets the page name a navigation on stderr even when the navigation replaced the page.
            string[] common = ["--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run", "--user-data-dir=" + profile,
                $"--virtual-time-budget={budgetMs}", "--enable-logging=stderr", "--v=0", .. Offline];
            // Headless Edge/Chrome gives the page less room than the window (frame and a minimum width of about 500 px).
            // The first run reads the real viewport from the page, the second sizes the window so the viewport is exactly width x height.
            var dom = Start(browser, [.. common, $"--window-size={width},{height}", "--dump-dom", url], limitMs);
            // A navigation that the page could not cancel is named on stderr; that also explains a run that never ended.
            var navigation = Regex.Match(dom.stderr, @"""__omlnav ([^""]*)""");
            var navigated = navigation.Success ? $"The page navigated to {navigation.Groups[1].Value}; the preview stopped. Nothing was loaded: the preview has no network." : null;
            if (dom.timedOut) return new Outcome(null, png is null ? null : "No screenshot. " + (navigated ?? loop), null, null, [navigated ?? loop]);
            var overflow = Regex.Match(dom.stdout, @"data-oml-overflow=""(true|false)""");
            var viewport = Regex.Match(dom.stdout, @"data-oml-viewport=""(\d+),(\d+)""");
            var report = Regex.Match(dom.stdout, @"<pre id=""oml-preview-report""[^>]*>(.*?)</pre>", RegexOptions.Singleline);
            System.Text.Json.JsonElement? page = null;
            if (report.Success && report.Groups[1].Value.Length > 0)
                try { page = System.Text.Json.JsonDocument.Parse(WebUtility.HtmlDecode(report.Groups[1].Value)).RootElement.Clone(); }
                catch (System.Text.Json.JsonException) { }
            if (page is not { } p)
                notes.Add(navigated ?? "The page wrote no script report: it did not load, or it navigated away. Scripts, errors and steps are unknown.");
            else
            {
                if (p.TryGetProperty("navigation", out var tried) && tried.GetArrayLength() > 0)
                    notes.Add($"The page tried to leave or open a window: {string.Join(", ", tried.EnumerateArray().Select(x => x.GetString()))}. The preview stopped it and kept the page; nothing was loaded.");
                if (!dom.stdout.Contains("data-oml-done=\"true\"", StringComparison.Ordinal))
                    notes.Add($"The page did not finish within its time budget ({budgetMs} ms); still running: {(p.TryGetProperty("at", out var at) ? at.GetString() : "?")}. Later nodes and steps did not run.");
            }
            bool? wide = overflow.Success ? overflow.Groups[1].Value == "true" : null;
            if (png is null) return new Outcome(null, null, page, wide, notes);
            Directory.CreateDirectory(Path.GetDirectoryName(png)!);
            int windowWidth = width, windowHeight = height;
            if (viewport.Success)
            {
                if (int.Parse(viewport.Groups[1].Value) < width) windowWidth = width + (width - int.Parse(viewport.Groups[1].Value));
                if (int.Parse(viewport.Groups[2].Value) < height) windowHeight = height + (height - int.Parse(viewport.Groups[2].Value));
            }
            var left = TotalMs - (int)clock.ElapsedMilliseconds;
            if (left < budgetMs + 5_000)
                return new Outcome(null, $"No screenshot: the first browser run used the time limit ({TotalMs / 1000} seconds for both runs).", page, wide, notes);
            var shot = Start(browser, [.. common, $"--window-size={windowWidth},{windowHeight}", "--screenshot=" + png, url], Math.Min(limitMs, left));
            if (shot.timedOut || !File.Exists(png))
                return new Outcome(null, shot.timedOut ? loop : "The browser ran but wrote no screenshot.", page, wide, notes);
            return new Outcome(png, null, page, wide, notes);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new Outcome(null, "Could not run the browser: " + e.Message, null, null, []);
        }
        finally { try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // The browser writes UTF-8; without the explicit encodings, text in the report would be read in the console code page.
    static (string stdout, string stderr, bool timedOut) Start(string exe, string[] args, int limitMs)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(limitMs))
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            // What the browser wrote before it was stopped (stderr can name a navigation).
            var written = stderr.Wait(5_000) ? stderr.Result : "";
            return ("", written, true);
        }
        process.WaitForExit();
        return (stdout.Result, stderr.Result, false);
    }
}
