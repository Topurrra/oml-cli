using System.Diagnostics;
using System.Net;
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

// Headless Edge or Chrome, when installed. The page records script errors and horizontal overflow on <html>; --dump-dom reads them back.
static class PreviewBrowser
{
    internal sealed record Outcome(string? Screenshot, string? Note, object? JavaScriptErrors, bool? HorizontalOverflow);

    internal static Outcome Capture(string html, string png, int width, int height)
    {
        var browser = Locations.BrowserPath();
        if (browser is null)
            return new Outcome(null, "No Edge or Chrome found in the usual Program Files folders (set OML_BROWSER to a browser .exe). Open the HTML file in a browser.", null, null);
        var profile = Path.Combine(Path.GetTempPath(), "oml-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            var url = new Uri(html).AbsoluteUri;
            string[] common = ["--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run", "--user-data-dir=" + profile, "--virtual-time-budget=1000"];
            Directory.CreateDirectory(Path.GetDirectoryName(png)!);
            // Headless Edge/Chrome gives the page less room than the window (frame and a minimum width of about 500 px).
            // The first run reads the real viewport from the page, the second sizes the window so the viewport is exactly width x height.
            var dom = Start(browser, [.. common, $"--window-size={width},{height}", "--dump-dom", url]);
            var errors = Regex.Match(dom.output, @"data-oml-errors=""(\d+)""");
            var overflow = Regex.Match(dom.output, @"data-oml-overflow=""(true|false)""");
            var viewport = Regex.Match(dom.output, @"data-oml-viewport=""(\d+),(\d+)""");
            var panel = Regex.Match(dom.output, @"<pre id=""oml-preview-errors""[^>]*>(.*?)</pre>", RegexOptions.Singleline);
            int windowWidth = width, windowHeight = height;
            if (viewport.Success)
            {
                if (int.Parse(viewport.Groups[1].Value) < width) windowWidth = width + (width - int.Parse(viewport.Groups[1].Value));
                if (int.Parse(viewport.Groups[2].Value) < height) windowHeight = height + (height - int.Parse(viewport.Groups[2].Value));
            }
            var shot = Start(browser, [.. common, $"--window-size={windowWidth},{windowHeight}", "--screenshot=" + png, url]);
            if (shot.timedOut || !File.Exists(png))
                return new Outcome(null, shot.timedOut ? "The browser did not finish in 60 seconds." : "The browser ran but wrote no screenshot." + Tail(shot.output), null, null);
            var count = errors.Success ? int.Parse(errors.Groups[1].Value) : (int?)null;
            object? javaScript = count is null ? null : new { count, messages = count == 0 ? [] : WebUtility.HtmlDecode(panel.Groups[1].Value).Split("\n\n", StringSplitOptions.RemoveEmptyEntries) };
            return new Outcome(png, null, javaScript, overflow.Success ? overflow.Groups[1].Value == "true" : null);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new Outcome(null, "Could not run the browser: " + e.Message, null, null);
        }
        finally { try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    static string Tail(string text) => text.Trim().Length == 0 ? "" : " " + text.Trim()[^Math.Min(300, text.Trim().Length)..];

    static (string output, bool timedOut) Start(string exe, string[] args)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            return ("", true);
        }
        process.WaitForExit();
        return (stdout.Result + stderr.Result, false);
    }
}
