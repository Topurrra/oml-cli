using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli.Qa;

// Runs the steps of a QA file like a tester would: one action per step, then wait until the screen is ready, then
// the step's expectations (retried until they hold or the timeout passes), then the checks every step gets for free:
// no JavaScript exception, no failed request, no server exception from an OutSystems call, no error feedback message.
// capture: also report the screen each step ended on, for `qa run --save`.
sealed class QaRunner(IQaEngine browser, Uri? baseUrl, int timeoutMs, string shotDir, bool capture = false)
{
    static readonly string[] Actions = ["open", "click", "fill", "select", "press", "wait", "screenshot", "expect"];
    // Words that make a click destructive. Such a click needs "allowDestructive": true in its step.
    // No word boundaries: widget names run words together (DeleteButton, BtnRemoveUser).
    static readonly Regex Destructive = new("delete|remove|erase|destroy|purge|apagar|eliminar|excluir", RegexOptions.IgnoreCase);

    public async Task<JsonObject> Step(int n, JsonElement step)
    {
        if (step.ValueKind != JsonValueKind.Object) throw new QaUsage($"step {n}: a step is an object such as {{\"click\": \"SaveButton\"}}.");
        var action = Actions.FirstOrDefault(a => step.TryGetProperty(a, out _))
            ?? throw new QaUsage($"step {n}: no action. Use one of: {string.Join(", ", Actions)}.");
        var others = Actions.Where(a => a != action && a != "expect" && step.TryGetProperty(a, out _)).ToList();
        if (others.Count > 0) throw new QaUsage($"step {n}: one action per step ({action} and {others[0]} given).");
        var result = new JsonObject { ["n"] = n, ["do"] = Describe(action, step.GetProperty(action)) };
        var clock = Stopwatch.StartNew();
        var seq = browser.Network.Seq;
        var (exceptions, console) = browser.ErrorCounts();
        var notes = new JsonArray();
        try
        {
            var value = step.GetProperty(action);
            switch (action)
            {
                case "open": await Open(value); break;
                case "click": await Click(value, step); break;
                case "fill": await Fill(value, notes); break;
                case "select": await Select(value); break;
                case "press": await browser.Press(value.GetString() ?? throw new QaUsage("press: give a key such as \"Enter\".")); break;
                case "wait": await Wait(value); break;
                case "screenshot": result["screenshot"] = await browser.Screenshot(Path.Combine(shotDir, $"step-{n}-{Safe(value.GetString() ?? "shot")}.png")); break;
            }
            // Opening also covers the browser's cold start and the app's first load: at least 30 seconds.
            if (action is "open" or "click" or "fill" or "select" or "press") result["readyMs"] = await browser.WaitReady(action == "open" ? Math.Max(timeoutMs, 30_000) : timeoutMs);
            // A time limit from a saved run: slower than it means the app got slower, not that the test should wait longer.
            if (step.TryGetProperty("maxMs", out var max) && max.TryGetInt64(out var limit) && clock.ElapsedMilliseconds > limit)
                throw new QaFailure($"{action} took {clock.ElapsedMilliseconds} ms; the limit is {limit} ms (maxMs). The app is slower than when the file was saved.");
            if (capture && action is not ("wait" or "screenshot")) { var path = new Uri(await browser.Url()).AbsolutePath.TrimEnd('/'); result["screen"] = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]); }
            if (step.TryGetProperty("expect", out var expect)) await Expect(expect, seq);
            var allowErrors = step.TryGetProperty("allowErrors", out var ae) && ae.GetBoolean();
            var calls = await Calls(seq);
            if (calls.Count > 0) result["calls"] = calls;
            var problems = allowErrors ? [] : await Problems(seq, exceptions, console, notes, step);
            if (problems.Count > 0) throw new QaFailure(string.Join(" | ", problems));
            result["ok"] = true;
        }
        // A usage error stops the run; anything else (a failed check, an element that went away, a browser error) fails
        // this step only, with a screenshot, so one surprise does not lose the report of the steps before it.
        catch (Exception e) when (e is not QaUsage)
        {
            result["ok"] = false;
            result["error"] = e is QaFailure ? e.Message : $"{e.GetType().Name}: {e.Message}";
            try { result["screenshot"] = await browser.Screenshot(Path.Combine(shotDir, $"step-{n}-failed.png")); } catch (Exception x) when (x is not QaUsage) { }
            try { result["url"] = await browser.Url(); } catch (Exception x) when (x is not QaUsage) { }
        }
        if (notes.Count > 0) result["notes"] = notes;
        result["ms"] = clock.ElapsedMilliseconds;
        return result;
    }

    async Task Open(JsonElement value)
    {
        var text = value.GetString() ?? throw new QaUsage("open: give a screen name or URL.");
        var url = Uri.TryCreate(text, UriKind.Absolute, out var abs) && abs.Scheme.StartsWith("http") ? abs
            : baseUrl is not null ? new Uri(baseUrl, text.TrimStart('/'))
            : throw new QaUsage($"open: \"{text}\" is not a full URL; give baseUrl in the file or --base-url.");
        if (baseUrl is not null && !url.Host.Equals(baseUrl.Host, StringComparison.OrdinalIgnoreCase))
            throw new QaUsage($"open: {url.Host} is not the host of baseUrl ({baseUrl.Host}). A QA file stays on one environment.");
        await browser.Navigate(url.AbsoluteUri);
    }

    async Task Click(JsonElement value, JsonElement step)
    {
        var (target, nth) = Target(value);
        var at = await Locate(target, nth, field: false, actionable: true);
        var what = $"{target} {at.GetProperty("el")}";
        if (Destructive.IsMatch(what) && !(step.TryGetProperty("allowDestructive", out var ok) && ok.GetBoolean()))
            throw new QaFailure($"click {target}: looks destructive ({at.GetProperty("el")}). Add \"allowDestructive\": true to this step if it is meant, on test data only.");
        await browser.Activate(at);
    }

    async Task Fill(JsonElement value, JsonArray notes)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new QaUsage("fill: give an object of target and text, such as {\"Input_Name\": \"Ana\"}.");
        var typed = new List<(string Target, int? Nth, string Text, bool Secret)>();
        foreach (var field in value.EnumerateObject())
        {
            var (text, secret) = Value(field.Value);
            var (target, nth) = Target(field.Name);
            var at = await Locate(target, nth, field: true, actionable: true);
            if (at.TryGetProperty("kind", out var kind) && kind.GetString() == "select") { await Choose(target, text); continue; }
            await browser.Type(text);
            if (kind.ValueKind == JsonValueKind.String && kind.GetString() is "number" or "date" or "time") continue; // the field reformats
            var now = await ReadBack(text);
            if (now != text)
                throw new QaFailure($"fill {target}: the field shows {(secret ? "other text" : Quote(now))} after typing {(secret ? "the secret" : Quote(text))}. A mask, max length or script changed it.");
            typed.Add((target, nth, text, secret));
        }
        // A screen that loads its record late (a data action finishing after the form showed) puts the old value back
        // over what was typed. Once the screen settles, check every field again and type once more where it was reset.
        await browser.WaitReady(timeoutMs);
        foreach (var (target, nth, text, secret) in typed)
        {
            await Locate(target, nth, field: true, actionable: false);
            if (await ReadBack(text) == text) continue;
            notes.Add($"{target} was reset by the screen after typing (its data loaded later); typed again. A user who types fast loses this text.");
            await Locate(target, nth, field: true, actionable: true);
            await browser.Type(text);
            if (await ReadBack(text) is var again && again != text)
                throw new QaFailure($"fill {target}: the screen reset the field twice; it shows {(secret ? "other text" : Quote(again))}.");
        }
    }

    // The value a field reports can trail the change for a moment (UI Automation): read it back for up to a second.
    async Task<string> ReadBack(string expected)
    {
        var now = "";
        for (var wait = Stopwatch.StartNew(); wait.ElapsedMilliseconds < 1_000; await Task.Delay(50))
            if ((now = (await browser.Helper("valueOfLast")).ToString()) == expected) break;
        return now;
    }

    async Task Select(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new QaUsage("select: give an object of dropdown and option text, such as {\"Dropdown_Role\": \"Admin\"}.");
        foreach (var field in value.EnumerateObject())
        {
            var (target, nth) = Target(field.Name);
            await Locate(target, nth, field: true, actionable: true);
            await Choose(target, Value(field.Value).Text);
        }
    }

    async Task Choose(string target, string option)
    {
        var r = await browser.Helper("selectLast", option);
        if (r.TryGetProperty("error", out var error))
            throw new QaFailure($"select {target}: {error.GetString()}{(r.TryGetProperty("options", out var o) ? "; options: " + string.Join(", ", o.EnumerateArray().Select(x => x.GetString())) : "")}.");
    }

    async Task Wait(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number) { await Task.Delay(Math.Clamp(value.GetInt32(), 0, 60_000)); return; }
        var text = value.GetString() ?? throw new QaUsage("wait: give milliseconds or a text to wait for.");
        await Until(async () => (await browser.Helper("text")).GetString()!.Contains(text, StringComparison.OrdinalIgnoreCase) ? null : $"the text {Quote(text)} did not appear");
    }

    // Finds the target, waiting for it to appear and (for an action) to be visible, enabled and not covered.
    async Task<JsonElement> Locate(string target, int? nth, bool field, bool actionable)
    {
        JsonElement at = default;
        await Until(async () =>
        {
            at = await browser.Helper("locate", target, nth, field);
            if (at.TryGetProperty("ambiguous", out var many))
                throw new QaFailure($"{target}: {at.GetProperty("found")} elements match ({string.Join(", ", many.EnumerateArray().Select(e => e.ToString()))}). Add \"#0\", \"#1\", ... to the target, or use a widget Name.");
            if (at.TryGetProperty("error", out var error)) throw new QaFailure($"{target}: {error.GetString()}");
            if (at.GetProperty("found").GetInt32() == 0) return $"no widget named {target} and no {(field ? "field labelled" : "button or link with the text")} {Quote(target)} on the screen";
            if (!actionable) return null;
            if (!at.GetProperty("visible").GetBoolean()) return $"{target} is not visible";
            if (at.GetProperty("disabled").GetBoolean()) return $"{target} is disabled";
            if (at.TryGetProperty("covered", out var c)) return $"{target} is covered by {c}";
            return null;
        });
        return at;
    }

    // Retries the check until it returns null (holds) or the timeout passes; then fails with its last answer.
    async Task Until(Func<Task<string?>> check)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var problem = await check();
            if (problem is null) return;
            if (clock.ElapsedMilliseconds > timeoutMs) throw new QaFailure(problem + $" (after {timeoutMs} ms)");
            await Task.Delay(100);
        }
    }

    async Task Expect(JsonElement expect, int seq)
    {
        if (expect.ValueKind != JsonValueKind.Object) throw new QaUsage("expect: give an object, such as {\"text\": \"Saved\"}.");
        foreach (var p in expect.EnumerateObject())
            if (!QaExpect.Keys.Contains(p.Name)) throw new QaUsage($"expect: unknown key {p.Name}. Known: {string.Join(", ", QaExpect.Keys)}.");
        if (browser.Mode == "uia" && expect.TryGetProperty("call", out _))
            throw new QaFailure("expect call: needs the DevTools engine to see the network; this run uses UI Automation. Check what the screen shows instead (table, text, count).");
        await Until(async () =>
        {
            var failed = new List<string>();
            foreach (var p in expect.EnumerateObject())
                if (await QaExpect.Check(browser, p.Name, p.Value, seq) is { } problem) failed.Add(problem);
            return failed.Count == 0 ? null : string.Join("; ", failed);
        });
    }

    async Task<JsonArray> Calls(int seq)
    {
        var list = new JsonArray();
        foreach (var r in browser.Network.CallsSince(seq).Where(r => r.Done))
        {
            var read = await browser.ReadCall(r);
            list.Add(new JsonObject { ["name"] = r.Call, ["status"] = r.Status, ["rows"] = read.Rows, ["exception"] = read.Exception });
        }
        return list;
    }

    async Task<List<string>> Problems(int seq, int exceptions, int console, JsonArray notes, JsonElement step)
    {
        var problems = new List<string>();
        var (newExceptions, newConsole) = browser.ErrorsSince(exceptions, console);
        problems.AddRange(newExceptions.Select(e => "JavaScript error: " + e));
        foreach (var c in newConsole.Take(5)) notes.Add("console: " + c);
        // A failed request to the app's own server is a defect; one to another host (analytics, a CDN) is a note.
        var host = new Uri(await browser.Url()).Host;
        foreach (var r in browser.Network.FailedSince(seq))
        {
            var line = $"{r.Call ?? QaNetwork.Short(r.Url)} {(r.Failed ?? "HTTP " + r.Status)}";
            if (Uri.TryCreate(r.Url, UriKind.Absolute, out var u) && u.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) problems.Add("request failed: " + line);
            else notes.Add("request failed: " + line);
        }
        foreach (var r in browser.Network.CallsSince(seq).Where(r => r.Done && r.Status < 400))
            if ((await browser.ReadCall(r)).Exception is { } ex) problems.Add($"server exception in {r.Call}: {ex}");
        var expectsError = step.TryGetProperty("expect", out var e) && e.TryGetProperty("feedback", out var f) && f.ToString().Contains("error", StringComparison.OrdinalIgnoreCase);
        if (!expectsError)
            foreach (var m in (await browser.Helper("feedback")).EnumerateArray().Where(m => m.GetProperty("type").GetString() == "error"))
                problems.Add("error message on screen: " + m.GetProperty("text").GetString());
        return problems;
    }

    static (string Target, int? Nth) Target(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Target(value.GetString()!),
        JsonValueKind.Object when value.TryGetProperty("target", out var t) => (t.GetString()!, value.TryGetProperty("nth", out var n) ? n.GetInt32() : null),
        _ => throw new QaUsage("A target is a widget Name, \"text:<label>\", \"css:<selector>\", or {\"target\": ..., \"nth\": N}."),
    };
    static (string, int?) Target(string text) => (text, null);

    // "$env:NAME" reads the variable, so a test user's password never sits in the file or the report.
    static (string Text, bool Secret) Value(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ToString();
        if (!text.StartsWith("$env:", StringComparison.Ordinal)) return (text, false);
        var name = text[5..];
        return (Environment.GetEnvironmentVariable(name) ?? throw new QaUsage($"The environment variable {name} is not set."), true);
    }

    static string Describe(string action, JsonElement value) => action switch
    {
        "fill" or "select" when value.ValueKind == JsonValueKind.Object =>
            action + " " + string.Join(", ", value.EnumerateObject().Select(p => $"{p.Name}={(p.Value.ToString().StartsWith("$env:") ? p.Value.ToString() : Quote(p.Value.ToString()))}")),
        "expect" => "expect " + JsonSerializer.Serialize(value), // one line, however the file indents it
        _ => action + " " + value,
    };

    internal static string Quote(string s) => JsonSerializer.Serialize(s);
    static string Safe(string name) => Regex.Replace(name, @"[^\w\-]+", "_");
}
