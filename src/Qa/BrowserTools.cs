using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli.Qa;

// The oml browser commands that look closer than a page summary. All need DevTools (the cdp engine):
//   look <url> <target>    one element: its place in the module (screen > blocks > widget), box, computed values, the CSS rule that
//                          wins each property and the ones it beats, what hides or covers it; --state hover|focus|active|focus-visible
//                          forces a state, --theme dark emulates a dark color scheme, --screenshot <file.png> captures only the element
//   sweep <url>            the page at each screen width the project supports (ui.targets; --widths 390,768,1366): overflow and the
//                          elements that cause it, layout findings, widgets shown at one width and not at the widest
//   page <url> --snapshot <file.json>, then diff <before.json> <after.json | url>   what changed between two moments (before and
//                          after a publish) as text: widgets added, removed, moved, resized, restyled or with new text
//   page also reports accessibility from the browser's own tree (a11y) and server calls that took a second or more (slowCalls).
static class BrowserTools
{
    static CdpEngine Cdp(IQaEngine browser, string what) =>
        browser as CdpEngine ?? throw new QaUsage($"{what} needs DevTools; this browser runs under UI Automation (a policy blocks DevTools).");

    // ---------- look ----------

    // The properties whose winning rule is explained; the others are listed as computed values only.
    static readonly string[] Explained = ["display", "position", "width", "height", "min-width", "max-width", "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left", "color", "background", "background-color", "font-size", "font-weight", "font-family",
        "line-height", "border", "border-radius", "border-color", "visibility", "opacity", "flex-direction", "justify-content", "align-items", "gap", "text-align", "z-index", "box-shadow"];

    // Inherited from an ancestor when the element sets nothing itself.
    static readonly HashSet<string> Inheriting = ["color", "font-size", "font-weight", "font-family", "line-height", "text-align", "visibility"];

    public static async Task<JsonObject> Look(IQaEngine browser, string url, string target, QaCommand.Settings o)
    {
        var engine = Cdp(browser, "oml browser look");
        if (o.Get("--theme") is { } theme)
        {
            if (theme is not ("dark" or "light")) throw new QaUsage("--theme takes dark or light.");
            await engine.Cdp.Send("Emulation.setEmulatedMedia", new { features = new[] { new { name = "prefers-color-scheme", value = theme } } });
        }
        await browser.Navigate(url);
        await browser.WaitReady(Math.Max(o.Int("--timeout", 15_000), 30_000));
        var at = await browser.Helper("locate", target, null, false);
        if (at.GetProperty("found").GetInt32() == 0) throw new QaFailure($"No element matches '{target}' (a widget Name, text:<visible text> or css:<selector>).");
        if (at.TryGetProperty("ambiguous", out var many))
            return new JsonObject { ["error"] = $"'{target}' matches {at.GetProperty("found").GetInt32()} elements: add #<n> (from 0) to pick one.", ["matches"] = JsonNode.Parse(many.GetRawText()) };
        if (at.TryGetProperty("error", out var bad)) throw new QaFailure(bad.GetString()!);

        var cdp = engine.Cdp;
        var sheets = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        void On(string method, JsonElement p)
        {
            if (method == "CSS.styleSheetAdded" && p.TryGetProperty("header", out var h))
                sheets[h.GetProperty("styleSheetId").GetString()!] = h.TryGetProperty("sourceURL", out var u) ? u.GetString() ?? "" : "";
        }
        cdp.Event += On;
        try
        {
            await cdp.Send("DOM.enable");
            await cdp.Send("CSS.enable");
            await cdp.Send("DOM.getDocument", new { depth = 0 });
            var objectId = (await cdp.Send("Runtime.evaluate", new { expression = "window.__omlqaLast" })).GetProperty("result").GetProperty("objectId").GetString();
            var nodeId = (await cdp.Send("DOM.requestNode", new { objectId })).GetProperty("nodeId").GetInt32();
            var state = o.Get("--state");
            if (state is not null)
            {
                if (state is not ("hover" or "focus" or "active" or "focus-visible" or "focus-within" or "visited")) throw new QaUsage("--state takes hover, focus, active, focus-visible, focus-within or visited.");
                await cdp.Send("CSS.forcePseudoState", new { nodeId, forcedPseudoClasses = new[] { state } });
            }
            var look = JsonNode.Parse((await browser.Helper("lookLast")).GetRawText())!.AsObject();
            var matched = await cdp.Send("CSS.getMatchedStylesForNode", new { nodeId });
            look["rules"] = Rules(matched, sheets);
            if (o.Get("--screenshot") is { } file && look["clip"] is JsonObject clip)
            {
                var shot = await cdp.Send("Page.captureScreenshot", new
                {
                    format = "png", captureBeyondViewport = true,
                    clip = new { x = clip["x"]!.GetValue<double>(), y = clip["y"]!.GetValue<double>(), width = clip["width"]!.GetValue<double>(), height = clip["height"]!.GetValue<double>(), scale = 1 },
                }, 20_000);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                await File.WriteAllBytesAsync(file, Convert.FromBase64String(shot.GetProperty("data").GetString()!));
                look["screenshot"] = Path.GetFullPath(file);
            }
            look.Remove("clip");
            if (state is not null) look["state"] = state;
            if (o.Get("--theme") is { } t) look["theme"] = t + " (prefers-color-scheme; a theme that switches with a class needs that class on the page)";
            return QaCommand.Prune(look);
        }
        finally
        {
            cdp.Event -= On;
            try { await cdp.Send("CSS.disable"); await cdp.Send("DOM.disable"); } catch (CdpException) { }
        }
    }

    static string SheetName(string url) => url.Length == 0 ? "inline <style>" : Path.GetFileName(Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url);

    // Per explained property: the declaration that wins (the inline style, then the matched rules in cascade order, later and
    // !important first; for an inherited property with no declaration of its own, the nearest ancestor's), and the ones it beats.
    static JsonArray Rules(JsonElement matched, ConcurrentDictionary<string, string> sheets)
    {
        var decls = new Dictionary<string, List<(string Where, string Value, bool Important)>>(StringComparer.Ordinal);
        void Add(JsonElement style, string where)
        {
            if (!style.TryGetProperty("cssProperties", out var props)) return;
            foreach (var p in props.EnumerateArray())
            {
                if (p.TryGetProperty("disabled", out var off) && off.GetBoolean() || p.TryGetProperty("parsedOk", out var ok) && !ok.GetBoolean() || !p.TryGetProperty("value", out var v)) continue;
                if (p.TryGetProperty("implicit", out var imp) && imp.GetBoolean()) continue;
                var name = p.GetProperty("name").GetString()!;
                if (!Explained.Contains(name)) continue;
                var important = p.TryGetProperty("important", out var i) && i.GetBoolean();
                if (!decls.TryGetValue(name, out var list)) decls[name] = list = [];
                // A rule can list a property twice (as written and as the browser normalized it): keep its last one.
                if (list.Count > 0 && list[^1].Where == where) list.RemoveAt(list.Count - 1);
                list.Add((where, v.GetString() ?? "", important));
            }
        }
        string Where(JsonElement rule)
        {
            var url = rule.TryGetProperty("styleSheetId", out var id) && sheets.TryGetValue(id.GetString()!, out var u) ? u : "";
            var origin = rule.TryGetProperty("origin", out var or) ? or.GetString() : "regular";
            var sheet = origin == "user-agent" ? "browser default" : SheetName(url);
            return $"{rule.GetProperty("selectorList").GetProperty("text").GetString()} ({sheet})";
        }
        if (matched.TryGetProperty("matchedCSSRules", out var rules))
            foreach (var m in rules.EnumerateArray()) Add(m.GetProperty("rule").GetProperty("style"), Where(m.GetProperty("rule")));
        if (matched.TryGetProperty("inlineStyle", out var inline)) Add(inline, "style attribute");
        var result = new JsonArray();
        var winners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, list) in decls)
        {
            // Later beats earlier; !important beats normal (CDP lists the matched rules from lowest to highest precedence).
            var winner = list.LastOrDefault(d => d.Important) is { Where: not null } imp ? imp : list[^1];
            if (winner.Where.EndsWith("(browser default)", StringComparison.Ordinal)) continue;   // nothing of the app sets it
            // A longhand set by the same rule as its shorthand (padding-top with padding) says nothing new.
            if (name.IndexOf('-') is var dash and > 0 && winners.TryGetValue(name[..dash], out var shorthand) && shorthand == winner.Where) continue;
            winners[name] = winner.Where;
            var beaten = list.Where(d => d != winner && !d.Where.EndsWith("(browser default)", StringComparison.Ordinal)).Reverse().Take(3)
                .Select(d => (JsonNode)$"{d.Where}: {d.Value}{(d.Important ? " !important" : "")}").ToArray();
            result.Add(new JsonObject { ["property"] = name, ["value"] = winner.Value + (winner.Important ? " !important" : ""), ["from"] = winner.Where, ["beats"] = beaten.Length > 0 ? new JsonArray(beaten) : null });
        }
        // Inherited values: the nearest ancestor that declares an inheriting property the element does not declare.
        if (matched.TryGetProperty("inherited", out var inherited))
        {
            var depth = 0;
            foreach (var level in inherited.EnumerateArray())
            {
                depth++;
                if (!level.TryGetProperty("matchedCSSRules", out var up)) continue;
                foreach (var m in up.EnumerateArray().Reverse())
                {
                    if (!m.GetProperty("rule").GetProperty("style").TryGetProperty("cssProperties", out var props)) continue;
                    foreach (var p in props.EnumerateArray())
                    {
                        var name = p.GetProperty("name").GetString()!;
                        if (!Inheriting.Contains(name) || winners.ContainsKey(name) || !p.TryGetProperty("value", out var v) || p.TryGetProperty("disabled", out var off) && off.GetBoolean()) continue;
                        winners[name] = "inherited";
                        result.Add(new JsonObject { ["property"] = name, ["value"] = v.GetString(), ["from"] = Where(m.GetProperty("rule")), ["inherited"] = depth == 1 ? "from the parent" : $"from an ancestor {depth} levels up" });
                    }
                }
            }
        }
        return result;
    }

    // ---------- try ----------

    // Golden rule 1: a preview of planned UI in the running page before anything is built. The CSS goes into a <style> of its own,
    // HTML replaces (--html) or is added to (--append) the content of a located element, and a script runs once. Nothing is saved: a
    // reload removes it. In a session started with --headed the user sees the preview in that window.
    public static async Task<JsonObject> Try(IQaEngine browser, string url, QaCommand.Settings o)
    {
        var engine = Cdp(browser, "oml browser try (use your own browser's dev tools instead)");
        string Read(string option, string value)
        {
            if (File.Exists(value)) return File.ReadAllText(value);
            if (option == "--css" && value.Contains('{')) return value;   // a short rule inline
            throw new QaUsage($"{option}: no file {value}.");
        }
        var css = o.Get("--css") is { } c ? Read("--css", c) : null;
        var html = new List<(string Target, string Html, bool Append)>();
        foreach (var (option, append) in new[] { ("--html", false), ("--append", true) })
            if (o.Get(option) is { } spec)
            {
                var eq = spec.IndexOf('=');
                if (eq <= 0) throw new QaUsage($"{option} takes <target>=<file.html>, such as OrdersTable=plan.html.");
                html.Add((spec[..eq], Read(option, spec[(eq + 1)..]), append));
            }
        var js = o.Get("--js") is { } j ? Read("--js", j) : null;
        if (css is null && html.Count == 0 && js is null) throw new QaUsage("try needs at least one of --css <file.css>, --html <target>=<file.html>, --append <target>=<file.html>, --js <file.js>.");

        var (exceptionCount, _) = browser.ErrorCounts();
        await browser.Navigate(url);
        await browser.WaitReady(Math.Max(o.Int("--timeout", 15_000), 30_000));
        var done = new JsonArray();
        if (css is not null)
        {
            await engine.Eval($"(() => {{ let s = document.getElementById('oml-try'); if (!s) {{ s = document.createElement('style'); s.id = 'oml-try'; document.head.appendChild(s); }} s.textContent += {JsonSerializer.Serialize(css)}; }})()");
            done.Add($"css: {css.Split('}').Length - 1} rules");
        }
        foreach (var (target, markup, append) in html)
        {
            var at = await browser.Helper("locate", target, null, false);
            if (at.GetProperty("found").GetInt32() == 0) throw new QaFailure($"No element matches '{target}' (a widget Name, text:<visible text> or css:<selector>).");
            if (at.TryGetProperty("ambiguous", out _)) throw new QaFailure($"'{target}' matches {at.GetProperty("found").GetInt32()} elements: add #<n> (from 0) to pick one.");
            await engine.Eval(append ? $"window.__omlqaLast.insertAdjacentHTML('beforeend', {JsonSerializer.Serialize(markup)})" : $"window.__omlqaLast.innerHTML = {JsonSerializer.Serialize(markup)}");
            done.Add($"{(append ? "append" : "html")}: {target}");
        }
        if (js is not null)
        {
            await engine.Eval($"(() => {{ {js}\n}})()");
            done.Add("js");
        }
        await Task.Delay(300);   // let fonts, images and transitions settle
        var boxes = await browser.Helper("boxes");
        var result = new JsonObject
        {
            ["url"] = await browser.Url(), ["injected"] = done,
            ["layout"] = new JsonArray([.. QaLayout.Findings(boxes).Select(f => (JsonNode)f)]),
            ["errors"] = new JsonArray([.. browser.ErrorsSince(exceptionCount, 0).Exceptions.Select(e => (JsonNode)("JavaScript error: " + e))]),
            ["a11y"] = await A11y(browser),
            ["note"] = "A preview only: nothing is saved and a reload removes it. Show it to the user; build with oml apply after a yes, matching this preview.",
        };
        if (o.Get("--screenshot") is { } shot) result["screenshot"] = await browser.Screenshot(shot);
        return QaCommand.Prune(result);
    }

    // ---------- sweep ----------

    static readonly Dictionary<string, int> TargetWidth = new() { ["phone"] = 390, ["tablet"] = 768, ["desktop"] = 1366 };

    public static async Task<JsonObject> Sweep(IQaEngine browser, string url, QaCommand.Settings o)
    {
        var engine = Cdp(browser, "oml browser sweep");
        var widths = o.Get("--widths") is { } list
            ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(w => int.TryParse(w, out var n) && n >= 200 && n <= 4000 ? n : throw new QaUsage($"--widths: {w} is not a width from 200 to 4000.")).Distinct().ToList()
            : ProjectConfig.Load(null).List("ui.targets").Select(t => TargetWidth[t]).ToList();
        widths.Sort();
        var height = o.Int("--height", 900);
        var outDir = o.Get("--out");
        var rows = new JsonArray();
        var shownAt = new Dictionary<int, HashSet<string>>();
        foreach (var width in widths)
        {
            await engine.Cdp.Send("Emulation.setDeviceMetricsOverride", new { width, height, deviceScaleFactor = 1, mobile = width < 768 });
            await browser.Navigate(url);
            await browser.WaitReady(Math.Max(o.Int("--timeout", 15_000), 30_000));
            var boxes = await browser.Helper("boxes");
            var snap = await browser.Helper("snapshot");
            shownAt[width] = snap.GetProperty("widgets").EnumerateObject().Select(w => w.Name).ToHashSet(StringComparer.Ordinal);
            var row = new JsonObject
            {
                ["width"] = width, ["body"] = snap.GetProperty("body").GetString(),
                ["layout"] = new JsonArray([.. QaLayout.Findings(boxes).Select(f => (JsonNode)f)]),
            };
            // A phone emulation widens the window to fit wide content, so compare with the width asked for.
            var pageWidth = boxes.GetProperty("scrollWidth").GetDouble();
            if (pageWidth > width + 1)
            {
                if (!row["layout"]!.AsArray().Any(l => l!.GetValue<string>().StartsWith("overflow:")))
                    row["layout"]!.AsArray().Insert(0, $"overflow: the page is {pageWidth:0} px wide at {width} px (horizontal scroll)");
                row["overflowBy"] = JsonNode.Parse((await browser.Helper("overflowing", width)).GetRawText());
            }
            if (outDir is not null) row["screenshot"] = await browser.Screenshot(Path.Combine(outDir, $"sweep-{width}.png"));
            rows.Add(row);
        }
        // Widgets that show at a narrower width and not at the widest, and the other way round: often a responsive mistake, sometimes the design.
        if (widths.Count > 1)
        {
            var widest = shownAt[widths[^1]];
            for (var i = 0; i < widths.Count - 1; i++)
            {
                var row = rows[i]!.AsObject();
                var only = shownAt[widths[i]].Except(widest).Take(15).ToList();
                var missing = widest.Except(shownAt[widths[i]]).Take(15).ToList();
                if (only.Count > 0) row["onlyHere"] = new JsonArray([.. only.Select(x => (JsonNode)x)]);
                if (missing.Count > 0) row["hiddenHere"] = new JsonArray([.. missing.Select(x => (JsonNode)x)]);
            }
        }
        await engine.Cdp.Send("Emulation.setDeviceMetricsOverride", new { width = o.Int("--width", 1366), height, deviceScaleFactor = 1, mobile = false });
        return QaCommand.Prune(new JsonObject { ["url"] = url, ["widths"] = rows, ["note"] = o.Get("--widths") is null ? "Widths from ui.targets in .oml/oml-config.json (phone 390, tablet 768, desktop 1366)." : null });
    }

    // ---------- snapshot and diff ----------

    public static async Task<string> Snapshot(IQaEngine browser, string file)
    {
        var snap = await browser.Helper("snapshot");
        var node = JsonNode.Parse(snap.GetRawText())!.AsObject();
        node["taken"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        await File.WriteAllTextAsync(file, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return Path.GetFullPath(file);
    }

    public static JsonObject ReadSnapshot(string file)
    {
        if (!File.Exists(file)) throw new QaUsage($"No snapshot at {file}. Take one with oml browser page <url> --snapshot {file}.");
        try { return JsonNode.Parse(File.ReadAllText(file))!.AsObject() is var o && o["widgets"] is JsonObject ? o : throw new QaUsage($"{file} is not an oml browser snapshot."); }
        catch (JsonException e) { throw new QaUsage($"{file} is not valid JSON: {e.Message}"); }
    }

    const int Moved = 4;   // px: smaller shifts are rounding and font loading

    // What changed between two snapshots, as lines an agent can read without pictures.
    public static JsonObject Diff(JsonObject before, JsonObject after)
    {
        var a = before["widgets"]!.AsObject();
        var b = after["widgets"]!.AsObject();
        var lines = new List<string>();
        foreach (var (k, _) in b) if (!a.ContainsKey(k)) lines.Add($"added: {k}{Text(b[k])}");
        foreach (var (k, _) in a) if (!b.ContainsKey(k)) lines.Add($"removed: {k}{Text(a[k])}");
        foreach (var (k, va) in a)
        {
            if (va is null || b[k] is not JsonObject vb) continue;
            var ba = va["box"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
            var bb = vb["box"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
            if (Math.Abs(ba[0] - bb[0]) >= Moved || Math.Abs(ba[1] - bb[1]) >= Moved) lines.Add($"moved: {k} from {ba[0]},{ba[1]} to {bb[0]},{bb[1]}");
            if (Math.Abs(ba[2] - bb[2]) >= Moved || Math.Abs(ba[3] - bb[3]) >= Moved) lines.Add($"resized: {k} from {ba[2]} x {ba[3]} to {bb[2]} x {bb[3]}");
            var ta = va["text"]?.GetValue<string>() ?? "";
            var tb = vb["text"]?.GetValue<string>() ?? "";
            if (ta != tb) lines.Add($"text: {k} \"{ta}\" -> \"{tb}\"");
            foreach (var (prop, value) in vb["style"]!.AsObject())
                if (va["style"]?[prop]?.GetValue<string>() is { } old && old != value?.GetValue<string>()) lines.Add($"style: {k} {prop} {old} -> {value}");
        }
        // A moved parent moves its children: keep the first lines of each kind short and say how many there were.
        string[] order = ["added", "removed", "text", "style", "resized", "moved"];
        var shown = lines.GroupBy(l => l[..l.IndexOf(':')]).OrderBy(g => Array.IndexOf(order, g.Key)).SelectMany(g => g.Take(25)).ToList();
        return QaCommand.Prune(new JsonObject
        {
            ["before"] = $"{before["url"]} ({before["taken"]}, {before["viewport"]?["w"]} px)", ["after"] = $"{after["url"]} ({after["taken"] ?? "now"}, {after["viewport"]?["w"]} px)",
            ["same"] = lines.Count == 0 ? true : null, ["changes"] = new JsonArray([.. shown.Select(l => (JsonNode)l)]),
            ["counts"] = lines.Count == 0 ? null : new JsonObject(lines.GroupBy(l => l[..l.IndexOf(':')]).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))),
            ["note"] = before["viewport"]?["w"]?.GetValue<int>() != after["viewport"]?["w"]?.GetValue<int>() ? "The two snapshots have different widths: boxes are not comparable." : null,
        });

        static string Text(JsonNode? w) => w?["text"]?.GetValue<string>() is { Length: > 0 } t ? $" \"{t}\"" : "";
    }

    // ---------- accessibility ----------

    static readonly HashSet<string> Controls = ["button", "link", "textbox", "combobox", "checkbox", "radio", "switch", "tab", "menuitem", "searchbox", "spinbutton", "slider", "listbox"];

    // From the browser's accessibility tree, the problems a screen reader user meets first: controls and images with no name,
    // headings that skip a level, and no main heading.
    public static async Task<JsonArray?> A11y(IQaEngine browser)
    {
        if (browser is not CdpEngine engine) return null;
        var cdp = engine.Cdp;
        try
        {
            await cdp.Send("Accessibility.enable");
            var tree = await cdp.Send("Accessibility.getFullAXTree", null, 20_000);
            var found = new List<string>();
            var levels = new List<int>();
            foreach (var n in tree.GetProperty("nodes").EnumerateArray())
            {
                if (n.TryGetProperty("ignored", out var ig) && ig.GetBoolean()) continue;
                var role = n.TryGetProperty("role", out var r) && r.TryGetProperty("value", out var rv) ? rv.GetString() ?? "" : "";
                var name = n.TryGetProperty("name", out var nm) && nm.TryGetProperty("value", out var nv) ? nv.GetString() ?? "" : "";
                if (role == "heading" && n.TryGetProperty("properties", out var props))
                    foreach (var p in props.EnumerateArray())
                        if (p.GetProperty("name").GetString() == "level") levels.Add(p.GetProperty("value").GetProperty("value").GetInt32());
                if ((Controls.Contains(role) || role is "image" or "img") && name.Trim().Length == 0 && found.Count < 15)
                {
                    var where = n.TryGetProperty("backendDOMNodeId", out var b) ? await Where(cdp, b.GetInt32()) : "";
                    found.Add(role is "image" or "img" ? $"image without a text alternative{where}" : $"{role} without a name{where}: screen readers announce only \"{role}\"");
                }
            }
            if (levels.Count > 0 && !levels.Contains(1)) found.Add("no level-1 heading: give the screen title a Heading 1");
            for (var i = 1; i < levels.Count; i++)
                if (levels[i] > levels[i - 1] + 1) { found.Add($"heading levels skip from {levels[i - 1]} to {levels[i]}"); break; }
            return new JsonArray([.. found.Select(f => (JsonNode)f)]);
        }
        catch (CdpException) { return null; }
        finally { try { await cdp.Send("Accessibility.disable"); } catch (CdpException) { } }
    }

    static async Task<string> Where(Cdp cdp, int backendNodeId)
    {
        try
        {
            var node = (await cdp.Send("DOM.describeNode", new { backendNodeId })).GetProperty("node");
            var attributes = node.TryGetProperty("attributes", out var a) ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
            var i = attributes.IndexOf("id");
            var id = i >= 0 && i + 1 < attributes.Count ? attributes[i + 1] : "";
            var widget = id.Length > 0 && !id.StartsWith('$') ? id.Split('-')[^1] : null;
            var c = attributes.IndexOf("class");
            var cls = c >= 0 && c + 1 < attributes.Count ? attributes[c + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
            return widget is not null ? $" ({widget})" : cls is not null ? $" ({node.GetProperty("localName").GetString()}.{cls})" : "";
        }
        catch (CdpException) { return ""; }
    }
}
