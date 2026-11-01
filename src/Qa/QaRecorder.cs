using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli.Qa;

// `oml qa record <url> [--out <qa.jsonc>]`: opens the app in a visible window and writes a QA file from what a
// person does there. Each click becomes a click step with the widget Name (or text: label) the page helper gives;
// text typed and options picked before it become fill and select steps (from the field values before and after);
// a click that changes the screen gets "expect screen", one that shows a feedback message gets "expect feedback".
// A password field is written as "$env:QA_PASSWORD", never its value. Ends when the window closes or on Ctrl+C.
static class QaRecorder
{
    public static async Task<int> Run(string url, QaCommand.Settings o)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Scheme.StartsWith("http")) throw new QaUsage($"{url} is not an http(s) URL.");
        var file = Path.GetFullPath(o.Get("--out") ?? "qa-recorded.jsonc");
        if (File.Exists(file)) throw new QaUsage($"{file} exists. Give a new --out file.");
        MouseHook.PerMonitorDpi();
        var (browser, note) = await QaCommand.Engine(o, o.Int("--width", 1366), o.Int("--height", 900), headed: true);
        using var _ = browser;
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

        await browser.Navigate(uri.AbsoluteUri);
        await browser.WaitReady(30_000);
        var start = new Uri(await browser.Url());
        // baseUrl is the module's root (https://host/Module/); steps open screens relative to it.
        var module = start.AbsolutePath.Trim('/').Split('/')[0];
        var baseUrl = new Uri($"{start.Scheme}://{start.Authority}/{module}/");
        var screen = Screen(start);
        var steps = new JsonArray { new JsonObject { ["open"] = Relative(baseUrl, start), ["expect"] = new JsonObject { ["screen"] = screen } } };
        var values = Inputs((await browser.Helper("summary")).GetProperty("inputs"));
        var notes = new List<string>();
        // Fields the person clicked into. Only their changes are recorded: a screen that loads its record late also
        // changes fields, and those are not the person's input.
        var touched = new HashSet<string>();
        Console.Error.WriteLine($"Recording {start}. Use the app in the window. Close the window, or press Ctrl+C here, to write {file}.");

        while (true)
        {
            var click = await browser.NextClick(stop.Token);
            if (click is not { } c)
            {
                // Ctrl+C with the window still open: keep what was typed after the last click.
                if (browser.Alive && stop.IsCancellationRequested)
                    try { AddChanges(steps, values, Inputs((await browser.Helper("summary")).GetProperty("inputs")), touched, notes); }
                    catch (Exception e) when (e is QaFailure or CdpException) { }
                break;
            }
            AddChanges(steps, values, Inputs(c.GetProperty("inputs")), touched, notes);
            values = Inputs(c.GetProperty("inputs"));
            var target = c.GetProperty("target").GetString()!;
            if (c.GetProperty("field").GetBoolean()) { touched.Add(target); continue; } // a click into a field only focuses it; its text comes as a fill
            var step = new JsonObject { ["click"] = target };
            if (target.Contains("delete", StringComparison.OrdinalIgnoreCase) || target.Contains("remove", StringComparison.OrdinalIgnoreCase)
                || (c.TryGetProperty("text", out var t) && t.GetString() is { } text && (text.Contains("delete", StringComparison.OrdinalIgnoreCase) || text.Contains("remove", StringComparison.OrdinalIgnoreCase))))
                step["allowDestructive"] = true;
            steps.Add(step);
            Console.Error.WriteLine($"  click {target}");
            try
            {
                // The click is seen when the button goes down; the app acts when it comes up. Give it that moment to
                // start its screen change, then the ready wait sees the transition (UiaPage.ScreenSettled).
                await Task.Delay(400);
                await browser.WaitReady(15_000);
                var now = Screen(new Uri(await browser.Url()));
                var expect = new JsonObject();
                if (now != screen) { expect["screen"] = screen = now; touched.Clear(); }
                var feedback = (await browser.Helper("feedback")).EnumerateArray().FirstOrDefault();
                if (feedback.ValueKind == JsonValueKind.Object) expect["feedback"] = feedback.GetProperty("type").GetString();
                if (expect.Count > 0) step["expect"] = expect;
                values = Inputs((await browser.Helper("summary")).GetProperty("inputs"));
            }
            catch (Exception e) when (e is QaFailure or CdpException) { } // the window closed, or the screen did not settle: keep the click
        }

        var qa = new JsonObject { ["baseUrl"] = baseUrl.AbsoluteUri, ["steps"] = steps };
        var header = $"// Recorded by oml qa record from {start} on {DateTime.Now:yyyy-MM-dd HH:mm}. Check the steps, add expectations, and run it with oml qa run.\n";
        await File.WriteAllTextAsync(file, header + qa.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        TerminalOutput.PrintObject(new { file, steps = steps.Count, engine = browser.Mode, engineNote = note, notes });
        return 0;
    }

    static string Screen(Uri u)
    {
        var path = u.AbsolutePath.TrimEnd('/');
        return Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
    }

    static string Relative(Uri baseUrl, Uri u) => Uri.UnescapeDataString(baseUrl.MakeRelativeUri(u).ToString());

    sealed record Field(string Type, string Value);

    static Dictionary<string, Field> Inputs(JsonElement inputs)
    {
        var map = new Dictionary<string, Field>();
        foreach (var i in inputs.EnumerateArray())
            if (i.TryGetProperty("target", out var t) && t.GetString() is { } target)
                map[target] = new Field(i.TryGetProperty("type", out var ty) ? ty.GetString() ?? "text" : "text", i.TryGetProperty("value", out var v) ? v.ToString() : "");
        return map;
    }

    // Fields the person clicked into whose value changed between two looks at the screen: one fill step (texts) and
    // one select step (dropdowns). Other changed fields go to the notes (typed after Tab, or set by the screen).
    static void AddChanges(JsonArray steps, Dictionary<string, Field> before, Dictionary<string, Field> after, HashSet<string> touched, List<string> notes)
    {
        var fill = new JsonObject();
        var select = new JsonObject();
        foreach (var (target, field) in after)
        {
            if (before.TryGetValue(target, out var old) && old.Value == field.Value) continue;
            if (!before.ContainsKey(target) && field.Value.Length == 0) continue;
            if (!touched.Remove(target))
            {
                var note = $"{target} changed without a click into it and was not recorded (the screen set it, or it was reached with Tab). Add it by hand if you typed it.";
                if (!notes.Contains(note)) notes.Add(note);
                continue;
            }
            if (field.Type == "select") select[target] = field.Value;
            else if (field.Type == "password")
            {
                fill[target] = "$env:QA_PASSWORD";
                notes.Add($"{target} is a password field: set QA_PASSWORD before you run the file.");
            }
            else fill[target] = field.Value;
        }
        if (fill.Count > 0) { steps.Add(new JsonObject { ["fill"] = fill }); Console.Error.WriteLine($"  fill {string.Join(", ", fill.Select(f => f.Key))}"); }
        if (select.Count > 0) { steps.Add(new JsonObject { ["select"] = select }); Console.Error.WriteLine($"  select {string.Join(", ", select.Select(f => f.Key))}"); }
    }
}
