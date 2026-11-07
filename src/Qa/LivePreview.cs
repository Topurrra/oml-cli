using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli.Qa;

// `oml preview --live <url> <source.oml> <staged.oml>`: how a UI change looks in the running app, before it is published.
// The page is the real one (real data, theme, OutSystems UI, the login of a running `oml qa start` session), so what
// the screenshots show is what users will see.
//   1. Both modules' style sheets (Core: describe --css --all) and what else changed (Core: review).
//   2. For every theme, screen or block whose style sheet changed: the page loses the rules of the old sheet and gets
//      the new sheet (QaCommand.Restyle), between a before and an after screenshot.
//   3. Any change that is not a style sheet (widgets, data, logic, new screens) cannot be shown honestly in a page that
//      was built from the published module: it is listed under needsPublish, with the publish-and-check step.
static class LivePreview
{
    const string Usage = "Usage: oml preview --live <url> <source.oml> <staged.oml> [--out <folder>] [--width N] [--height N] [--timeout <ms>] [--engine auto|cdp] [--headed] [--no-session]";

    public static int Run(string[] args, Func<string[], (int Code, string Output)> core)
    {
        try
        {
            var (url, source, staged, qaArgs) = Parse(args);
            JsonArray Sheets(string module)
            {
                var (code, output) = core(["describe", module, "--css", "--all"]);
                if (code != 0) throw new QaUsage($"Could not read the style sheets of {Path.GetFileName(module)}: {Short(output)}");
                return JsonNode.Parse(output) as JsonArray ?? throw new QaUsage($"describe --css --all gave no list for {Path.GetFileName(module)}.");
            }
            var before = Sheets(source).OfType<JsonObject>().ToDictionary(s => s["element"]!.GetValue<string>(), s => s["styleSheet"]!.GetValue<string>());
            var after = Sheets(staged).OfType<JsonObject>().ToDictionary(s => s["element"]!.GetValue<string>(), s => s["styleSheet"]!.GetValue<string>());
            var changed = before.Keys.Union(after.Keys).Order(StringComparer.Ordinal)
                .Where(k => before.GetValueOrDefault(k, "") != after.GetValueOrDefault(k, "")).ToList();

            var (reviewCode, reviewOutput) = core(["review", source, staged]);
            if (reviewCode is not (0 or 1)) throw new QaUsage($"Could not compare the modules: {Short(reviewOutput)}");
            var needsPublish = NotStyle(JsonNode.Parse(reviewOutput)?["changes"] as JsonArray ?? []);
            var publishStep = $"Install, publish to DEV, then check with: oml qa page {url} --screenshot <file.png>";

            if (changed.Count == 0)
            {
                TerminalOutput.PrintNode(QaCommand.Prune(new JsonObject
                {
                    ["ok"] = false, ["live"] = false,
                    ["reason"] = "No style sheet changed, so there is nothing to show in the live page.",
                    ["needsPublish"] = new JsonArray([.. needsPublish.Select(n => (JsonNode)n)]),
                    ["next"] = needsPublish.Count > 0 ? publishStep : null,
                }));
                return 1;
            }

            var cssFile = Path.Combine(Path.GetTempPath(), $"oml-live-{Guid.NewGuid():N}.json");
            File.WriteAllText(cssFile, new JsonObject
            {
                ["removed"] = new JsonArray([.. changed.Where(before.ContainsKey).Select(k => (JsonNode)before[k])]),
                ["added"] = new JsonArray([.. changed.Where(after.ContainsKey).Select(k => (JsonNode)after[k])]),
            }.ToJsonString());
            int code;
            JsonObject output;
            try
            {
                string[] request = ["restyle", url, "--css", cssFile, .. qaArgs];
                if (!qaArgs.Contains("--no-session") && QaSession.Forward(request) is { } forwarded) (code, output) = forwarded;
                else (code, output) = QaCommand.Execute(request, null).GetAwaiter().GetResult();
            }
            finally { File.Delete(cssFile); }
            if (code != 0) { TerminalOutput.PrintNode(output); return code; }

            var notes = new List<string>();
            if (output["rules"] is JsonObject rules && rules["oldRules"]?.GetValue<int>() is int old && rules["oldRulesFound"]?.GetValue<int>() is int found && found < old)
                notes.Add($"{old - found} of {old} rules of the old style sheets are not on this page. The page may not show those screens or blocks, or what is published differs from {Path.GetFileName(source)}.");
            if (output["errors"] is JsonArray { Count: > 0 }) notes.Add("The page showed JavaScript errors (errors): check them in the published app too.");
            notes.Add("Only style sheets are applied here. Widget classes, new widgets, data and logic show after a publish (needsPublish).");
            output["live"] = true;
            output["styleSheets"] = new JsonArray([.. changed.Select(c => (JsonNode)c)]);
            output["needsPublish"] = new JsonArray([.. needsPublish.Select(n => (JsonNode)n)]);
            output["notes"] = new JsonArray([.. notes.Select(n => (JsonNode)n)]);
            output["next"] = "Look at before and after. When it looks right, ask the user to install and publish" + (needsPublish.Count > 0 ? $"; then: oml qa page {url} --screenshot <file.png> for the rest." : ".");
            TerminalOutput.PrintNode(QaCommand.Prune(output));
            return 0;
        }
        catch (QaUsage e) { TerminalOutput.PrintObject(new { error = e.Message }); return 2; }
        catch (Exception e) when (e is CdpException or QaFailure or HttpRequestException or IOException or JsonException or System.Net.WebSockets.WebSocketException or TaskCanceledException)
        {
            TerminalOutput.PrintObject(new { error = e.Message });
            return 2;
        }
    }

    static (string Url, string Source, string Staged, string[] QaArgs) Parse(string[] args)
    {
        string? url = null;
        var files = new List<string>();
        var qa = new List<string>();
        string[] valued = ["--out", "--width", "--height", "--timeout", "--engine"];
        string[] flags = ["--headed", "--no-session"];
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--live" && i + 1 < args.Length) url = args[++i];
            else if (valued.Contains(args[i]) && i + 1 < args.Length) { qa.Add(args[i]); qa.Add(args[++i]); }
            else if (flags.Contains(args[i])) qa.Add(args[i]);
            else if (!args[i].StartsWith("--")) files.Add(args[i]);
            else throw new QaUsage($"Unknown or incomplete option {args[i]}. {Usage}");
        }
        if (url is null || files.Count != 2) throw new QaUsage(Usage);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Scheme.StartsWith("http")) throw new QaUsage($"{url} is not an http(s) URL. {Usage}");
        if (qa.IndexOf("--engine") is var e && e >= 0 && qa[e + 1] == "uia") throw new QaUsage("A live preview needs the DevTools engine (--engine auto or cdp): UI Automation cannot change a page.");
        foreach (var f in files)
            if (!File.Exists(f)) throw new QaUsage($"File not found: {f}");
        return (url, Path.GetFullPath(files[0]), Path.GetFullPath(files[1]), [.. qa]);
    }

    // The review's change lines that are not about a style sheet: what the live page cannot show.
    static List<string> NotStyle(JsonArray changes)
    {
        var lines = new List<string>();
        foreach (var c in changes.OfType<JsonObject>())
        {
            var name = $"{c["kind"]?.GetValue<string>()} {c["name"]?.GetValue<string>()}";
            var what = c["change"]?.GetValue<string>();
            var details = (c["details"] as JsonArray ?? []).Select(d => d?.GetValue<string>() ?? "").ToList();
            if (what != "changed") { lines.Add($"{name} {what}"); continue; }
            foreach (var d in details.Where(d => !d.StartsWith("style sheet ", StringComparison.Ordinal)))
                lines.Add($"{name}: {d}");
        }
        return lines;
    }

    static string Short(string text)
    {
        try { if (JsonNode.Parse(text)?["error"]?.GetValue<string>() is { } error) return error; } catch (JsonException) { }
        text = text.Trim();
        return text.Length <= 300 ? text : text[..300] + "...";
    }
}
