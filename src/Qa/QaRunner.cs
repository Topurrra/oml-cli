using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli.Qa;

// Runs the steps of a browser script like a tester would: one action per step, then wait until the screen is ready, then
// the step's expectations (retried until they hold or the timeout passes), then the checks every step gets for free:
// no JavaScript exception, no failed request, no server exception from an OutSystems call, no error feedback message.
// capture: also report the screen each step ended on, for `browser run --save`. person: how a handoff step reaches the
// person who logs in (see QaHandoff).
sealed class QaRunner(IQaEngine browser, Uri? baseUrl, int timeoutMs, string shotDir, bool capture = false, QaShot.Limit shots = default, QaHandoff? person = null)
{
    static readonly string[] Actions = ["open", "click", "fill", "select", "press", "wait", "screenshot", "handoff", "raw", "expect"];
    const int HandoffMs = 300_000;   // a handoff waits 5 minutes unless the step gives "timeout"
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
                case "open": seq = await Open(value, seq, notes); break;
                case "click": await Click(value, step); break;
                case "fill": await Fill(value, notes); break;
                case "select": await Select(value); break;
                case "press": await browser.Press(value.GetString() ?? throw new QaUsage("press: give a key such as \"Enter\".")); break;
                case "wait": await Wait(value); break;
                case "screenshot": await QaShot.Take(browser, Path.Combine(shotDir, $"step-{n}-{Safe(value.GetString() ?? "shot")}.png"), shots, result); break;
                case "handoff": result["handoff"] = await Handoff(value, step); break;
                case "raw": result["raw"] = await Raw(value, step); break;
            }
            // Opening also covers the browser's cold start and the app's first load: at least 30 seconds.
            if (action is "open" or "click" or "fill" or "select" or "press" or "handoff") result["readyMs"] = await browser.WaitReady(action == "open" ? Math.Max(timeoutMs, 30_000) : timeoutMs);
            // A time limit from a saved run: slower than it means the app got slower, not that the test should wait longer.
            if (step.TryGetProperty("maxMs", out var max) && max.TryGetInt64(out var limit) && clock.ElapsedMilliseconds > limit)
                throw new QaFailure($"{action} took {clock.ElapsedMilliseconds} ms; the limit is {limit} ms (maxMs). The app is slower than when the file was saved.");
            if (capture && action is not ("wait" or "screenshot")) { var path = new Uri(await browser.Url()).AbsolutePath.TrimEnd('/'); result["screen"] = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]); }
            if (step.TryGetProperty("expect", out var expect)) await Expect(expect, seq);
            // What happens on a login page while a person logs in (an identity provider's scripts) is not the app under test.
            var allowErrors = action == "handoff" || step.TryGetProperty("allowErrors", out var ae) && ae.GetBoolean();
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
            try { await QaShot.Take(browser, Path.Combine(shotDir, $"step-{n}-failed.png"), shots, result); } catch (Exception x) when (x is not QaUsage) { }
            try { result["url"] = await browser.Url(); } catch (Exception x) when (x is not QaUsage) { }
            if (action != "handoff" && await LoginHint(browser) is { } login) result["hint"] = login;
        }
        if (notes.Count > 0) result["notes"] = notes;
        result["ms"] = clock.ElapsedMilliseconds;
        return result;
    }

    // Returns the network sequence the step's problems count from: after a retry, the first 404 is not one.
    async Task<int> Open(JsonElement value, int seq, JsonArray notes)
    {
        var text = value.GetString() ?? throw new QaUsage("open: give a screen name or URL.");
        var url = Uri.TryCreate(text, UriKind.Absolute, out var abs) && abs.Scheme.StartsWith("http") ? abs
            : baseUrl is not null ? new Uri(baseUrl, text.TrimStart('/'))
            : throw new QaUsage($"open: \"{text}\" is not a full URL; give baseUrl in the file or --base-url.");
        if (baseUrl is not null && !url.Host.Equals(baseUrl.Host, StringComparison.OrdinalIgnoreCase))
            throw new QaUsage($"open: {url.Host} is not the host of baseUrl ({baseUrl.Host}). A browser script stays on one environment.");
        await browser.Navigate(url.AbsoluteUri);
        // A Traditional Web screen's address ends in .aspx (Orders.aspx); a Reactive one has none. A bare screen name that
        // gets a 404 is tried once more as <name>.aspx.
        if (baseUrl is not null && BareScreen.IsMatch(text))
        {
            await browser.WaitReady(Math.Max(timeoutMs, 30_000));
            var page = url.AbsoluteUri.Split('?', '#')[0];
            if (browser.Network.FailedSince(seq).Any(r => r.Status == 404 && string.Equals(r.Url.Split('?', '#')[0], page, StringComparison.OrdinalIgnoreCase)))
            {
                var again = browser.Network.Seq;
                await browser.Navigate(new Uri(baseUrl, text.TrimStart('/') + ".aspx").AbsoluteUri);
                notes.Add($"{text} gave 404; opened {text}.aspx (Traditional Web). Write \"{text}.aspx\" to skip the first try.");
                return again;
            }
        }
        return seq;
    }

    static readonly System.Text.RegularExpressions.Regex BareScreen = new(@"^/?[A-Za-z_][A-Za-z0-9_]*$");

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

    // A person takes over for what an agent must not do: log in, an MFA code. The window comes to the front, one
    // "attention" line says what to do, and the run goes on when "until" holds (the URL contains a text, or a target
    // is visible) or the person presses Enter (only when there is a terminal). oml never sees what they type.
    async Task<string> Handoff(JsonElement value, JsonElement step)
    {
        var what = value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } w ? w : "log in";
        var until = step.TryGetProperty("until", out var u) ? u : default;
        var hasUntil = until.ValueKind == JsonValueKind.Object;
        if (until.ValueKind != JsonValueKind.Undefined && (!hasUntil || !until.EnumerateObject().Any()
            || until.EnumerateObject().Any(p => p.Name is not ("url" or "visible") || p.Value.ValueKind != JsonValueKind.String)))
            throw new QaUsage("handoff: \"until\" is {\"url\": \"<part of the URL after login>\"} or {\"visible\": \"<widget, text:<text> or css:<selector>>\"}.");
        var limit = !step.TryGetProperty("timeout", out var t) ? HandoffMs
            : t.TryGetInt32(out var ms) && ms > 0 ? ms : throw new QaUsage("handoff: \"timeout\" is a number of milliseconds.");
        var who = person ?? QaHandoff.FromConsole();
        if (!hasUntil && who.Enter is null)
            throw new QaUsage("handoff: there is no terminal to press Enter in. Give \"until\" ({\"url\": \"...\"} or {\"visible\": \"...\"}) so oml sees when the person is done, and a \"timeout\" in ms.");
        if (!browser.Visible)
            throw new QaUsage("handoff: nobody can see this browser. Run with --headed, or attach to your own Chrome (oml browser start --attach).");
        if (browser is CdpEngine cdp) try { await cdp.Cdp.Send("Page.bringToFront"); } catch (CdpException) { }
        string url;
        try { url = await browser.Url(); } catch (Exception e) when (e is not QaUsage) { url = ""; }
        who.Attention(new JsonObject
        {
            ["attention"] = !hasUntil ? $"{what} in the browser window, then press Enter"
                : $"{what} in the browser window; oml goes on when it is done" + (who.Enter is null ? "" : " (or press Enter)"),
            // No query string: a login URL can carry a one-time code or state.
            ["url"] = Uri.TryCreate(url, UriKind.Absolute, out var page) ? page.GetLeftPart(UriPartial.Path) : null,
            ["until"] = hasUntil ? JsonNode.Parse(until.GetRawText()) : null,
            ["timeoutMs"] = limit,
        }.ToJsonString());
        using var stop = new CancellationTokenSource();
        var pressed = who.Enter?.Invoke(stop.Token) ?? Task.Delay(Timeout.Infinite, stop.Token);
        var clock = Stopwatch.StartNew();
        try
        {
            string? problem = null;
            while (!pressed.IsCompletedSuccessfully)
            {
                if (hasUntil && (problem = await Holds(until)) is null) return "until";
                if (clock.ElapsedMilliseconds > limit) throw new QaFailure($"handoff {what}: not done in {limit} ms ({problem ?? "Enter was not pressed"}).");
                if (pressed.IsCompleted) await Task.Delay(500); else await Task.WhenAny(pressed, Task.Delay(500));
            }
        }
        finally { stop.Cancel(); }
        if (hasUntil) await Until(() => Holds(until));   // Enter pressed: the condition must hold now too
        return "enter";
    }

    async Task<string?> Holds(JsonElement until)
    {
        try
        {
            if (until.TryGetProperty("url", out var url) && !(await browser.Url()).Contains(url.GetString()!, StringComparison.OrdinalIgnoreCase))
                return $"the URL does not contain {Quote(url.GetString()!)} yet";
            if (until.TryGetProperty("visible", out var v))
            {
                var at = await browser.Helper("locate", v.GetString(), null, false);
                if (!at.TryGetProperty("ambiguous", out _) && !(at.TryGetProperty("found", out var f) && f.GetInt32() > 0 && at.GetProperty("visible").GetBoolean()))
                    return $"{v.GetString()} is not visible yet";
            }
            return null;
        }
        // A navigation (the login redirects) replaces the page while oml reads it: not yet.
        catch (Exception e) when (e is CdpException or QaFailure or InvalidOperationException or KeyNotFoundException) { return "the page is changing"; }
    }

    // Any DevTools method on oml's page (see CdpEngine.RawRefusal for what is refused).
    async Task<JsonNode?> Raw(JsonElement value, JsonElement step)
    {
        if (browser is not CdpEngine cdp) throw new QaUsage("raw: needs the DevTools engine; this run uses UI Automation.");
        var method = value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new QaUsage("raw: give a DevTools method name, such as \"Page.reload\".");
        var parameters = step.TryGetProperty("params", out var p) ? p : default;
        if (parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object)) throw new QaUsage("raw: \"params\" is an object.");
        return QaCommand.RawResult(await cdp.Raw(method, parameters));
    }

    // A visible password field: the screen asks to log in. Only a hint; oml types a password only from "$env:NAME".
    public static async Task<string?> LoginHint(IQaEngine browser)
    {
        try
        {
            var at = await browser.Helper("locate", "css:input[type=password]", null, true);
            if (at.TryGetProperty("ambiguous", out _) || at.TryGetProperty("found", out var f) && f.GetInt32() > 0 && at.GetProperty("visible").GetBoolean())
                return "This screen asks to log in. Attach to a browser where you are logged in (oml browser start --attach), or add a handoff step so a person logs in.";
        }
        catch (Exception e) when (e is not QaUsage) { }
        return null;
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

// How a handoff step reaches the person: where the attention line goes, and, when there is a terminal, a task that ends
// when they press Enter. A session passes both through its pipe to the caller's terminal (QaSession).
sealed class QaHandoff(Action<string> attention, Func<CancellationToken, Task>? enter)
{
    public Action<string> Attention { get; } = attention;
    public Func<CancellationToken, Task>? Enter { get; } = enter;

    // stderr, so stdout stays one JSON report; Enter only when stdin is a terminal (an agent's run has none).
    public static QaHandoff FromConsole() => new(line => Console.Error.WriteLine(line), Console.IsInputRedirected ? null : async cancel =>
    {
        if (await Task.Run(Console.ReadLine) is null) await Task.Delay(Timeout.Infinite, cancel);
    });
}
