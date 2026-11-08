using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli.Qa;

// `oml qa page <url> --style-conflicts <class prefix>`: for elements whose class starts with the prefix (the module's
// own classes, such as "oui-"), the CSS properties the module's style sheets set where a rule of OutSystems UI or the
// platform wins in the cascade. The offline preview cannot show these: it has no OutSystems UI CSS.
static class QaStyle
{
    const int MaxElements = 300, MaxConflicts = 40;

    // Platform style sheets, by the URL the app serves them from.
    static bool Platform(string url) =>
        url.Contains("/OutSystemsUI.", StringComparison.OrdinalIgnoreCase) || url.Contains("OutSystemsReactWidgets", StringComparison.OrdinalIgnoreCase)
        || url.Contains("/_Basic.css", StringComparison.OrdinalIgnoreCase);

    static string Sheet(string url) => url.Length == 0 ? "(inline <style>)" : Path.GetFileName(new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(url).AbsolutePath : url);

    public static async Task<JsonObject> Conflicts(Cdp cdp, string prefix)
    {
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
            await cdp.Send("CSS.enable"); // Chrome sends styleSheetAdded for every sheet already in the page
            var root = (await cdp.Send("DOM.getDocument", new { depth = 0 })).GetProperty("root").GetProperty("nodeId").GetInt32();
            var selector = $"[class^=\"{prefix}\"], [class*=\" {prefix}\"]";
            var nodes = (await cdp.Send("DOM.querySelectorAll", new { nodeId = root, selector })).GetProperty("nodeIds").EnumerateArray().Select(n => n.GetInt32()).ToList();
            var found = new Dictionary<(string mine, string property, string winner), (string element, string sheet, string value, string winnerValue, int count)>();
            foreach (var node in nodes.Take(MaxElements))
            {
                JsonElement matched;
                try { matched = await cdp.Send("CSS.getMatchedStylesForNode", new { nodeId = node }); }
                catch (CdpException) { continue; } // the node left the page
                if (!matched.TryGetProperty("matchedCSSRules", out var rules)) continue;
                // The cascade order: later rules win, an !important declaration wins over a normal one.
                var winners = new Dictionary<string, (bool important, bool platform, string selector, string sheet, string value)>(StringComparer.Ordinal);
                var mine = new Dictionary<string, (string selector, string value)>(StringComparer.Ordinal);
                foreach (var match in rules.EnumerateArray())
                {
                    var rule = match.GetProperty("rule");
                    if (rule.TryGetProperty("origin", out var origin) && origin.GetString() != "regular") continue;
                    var url = rule.TryGetProperty("styleSheetId", out var id) && sheets.TryGetValue(id.GetString()!, out var u) ? u : "";
                    var platform = Platform(url);
                    var text = rule.GetProperty("selectorList").GetProperty("text").GetString() ?? "";
                    foreach (var prop in rule.GetProperty("style").GetProperty("cssProperties").EnumerateArray())
                    {
                        if (prop.TryGetProperty("disabled", out var off) && off.GetBoolean() || prop.TryGetProperty("parsedOk", out var ok) && !ok.GetBoolean()) continue;
                        if (!prop.TryGetProperty("value", out var v)) continue;
                        var name = prop.GetProperty("name").GetString()!;
                        if (name.StartsWith("--", StringComparison.Ordinal)) continue; // variables do not conflict, their use does
                        var important = prop.TryGetProperty("important", out var imp) && imp.GetBoolean();
                        if (!winners.TryGetValue(name, out var w) || important || !w.important)
                            winners[name] = (important, platform, text, Sheet(url), v.GetString() ?? "");
                        if (!platform) mine[name] = (text, v.GetString() ?? "");
                    }
                }
                if (mine.Count == 0) continue;
                var described = (await cdp.Send("DOM.describeNode", new { nodeId = node })).GetProperty("node");
                var attributes = described.TryGetProperty("attributes", out var a) ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
                var classIndex = attributes.IndexOf("class");
                var classes = classIndex >= 0 && classIndex + 1 < attributes.Count ? attributes[classIndex + 1] : "";
                var element = described.GetProperty("localName").GetString() + "." + (classes.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(c => c.StartsWith(prefix, StringComparison.Ordinal)) ?? prefix);
                foreach (var (property, (mySelector, myValue)) in mine)
                    if (winners.TryGetValue(property, out var w) && w.platform)
                    {
                        var key = (mySelector, property, w.selector);
                        found[key] = found.TryGetValue(key, out var seen) ? seen with { count = seen.count + 1 } : (element, w.sheet, myValue, w.value, 1);
                    }
            }
            var list = found.OrderByDescending(f => f.Value.count).Take(MaxConflicts).Select(f => (JsonNode)new JsonObject
            {
                ["element"] = f.Value.element, ["property"] = f.Key.property,
                ["yours"] = $"{f.Key.mine} {{ {f.Key.property}: {f.Value.value} }}",
                ["wins"] = $"{f.Key.winner} {{ {f.Key.property}: {f.Value.winnerValue} }}", ["winsFrom"] = f.Value.sheet,
                ["elements"] = f.Value.count,
            }).ToArray();
            return new JsonObject
            {
                ["checkedElements"] = Math.Min(nodes.Count, MaxElements), ["conflicts"] = new JsonArray(list), ["total"] = found.Count,
                ["hint"] = found.Count == 0 ? null : "OutSystems UI wins with a more specific selector (often a data- attribute such as input[data-switch]). Restyle with that selector plus your class, for example input[data-switch].my-switch, rather than !important.",
            };
        }
        finally
        {
            cdp.Event -= On;
            try { await cdp.Send("CSS.disable"); await cdp.Send("DOM.disable"); } catch (CdpException) { }
        }
    }
}
