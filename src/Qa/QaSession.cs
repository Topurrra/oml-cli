using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli.Qa;

// `oml browser start` keeps one browser open in a background `oml browser serve` process, so later `browser run` and `browser page`
// skip the browser's cold start (6 to 8 seconds) and keep the app's login. They find the session through a state
// file and talk to it over a named pipe that only the same Windows user can open. One request at a time.
// The session stops on `oml browser stop`, after 30 minutes without a request, or when its browser is closed. Attached
// (--attach), it drives one tab of the user's own Chrome, and stop closes only that tab.
static class QaSession
{
    static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    // OML_BROWSER_SESSION moves the state file (the tests use it, so they never touch a session the user runs).
    static string StateFile => Environment.GetEnvironmentVariable("OML_BROWSER_SESSION") is { Length: > 0 } file ? Path.GetFullPath(file)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oml-cli", "browser-session.json");

    // Attached: "127.0.0.1:<port>" when the session drives a tab in the user's own Chrome (--attach).
    sealed record State(string Pipe, int Pid, string Engine, string? Note, int Width, int Height, DateTime Started, string? Attached = null);

    static State? ReadState()
    {
        try { return File.Exists(StateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) : null; }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    static bool Running(State s)
    {
        try { using var p = Process.GetProcessById(s.Pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    static object Describe(State s, bool started) => new
    {
        session = started ? "started" : "running", engine = s.Engine, engineNote = s.Note, pid = s.Pid,
        viewport = new { width = s.Width, height = s.Height }, since = s.Started.ToString("s"), attached = s.Attached,
        hint = s.Attached is null ? "browser run, page, look, sweep, diff and raw now use this browser. oml browser stop closes it."
            : "browser run, page, look, sweep, diff and raw now use a new tab in your browser, where oml acts as you. oml browser stop closes that tab only.",
    };

    public static int Start(QaCommand.Settings o)
    {
        if (o.Has("--attach"))
        {
            CdpEngine.AttachPort(o.Get("--attach"));   // a bad port or a host that is not this computer: refused here
            if (o.Get("--engine") == "uia") throw new QaUsage("--attach drives your browser over DevTools; it does not work with --engine uia.");
        }
        if (ReadState() is { } existing && Running(existing))
        {
            if (o.Has("--attach") && existing.Attached is null)
                throw new QaUsage("A browser session of oml's own is running. Run oml browser stop first, then oml browser start --attach.");
            TerminalOutput.PrintObject(Describe(existing, started: false));
            return 0;
        }
        File.Delete(StateFile);
        File.Delete(ErrorFile);
        var pipe = $"oml-qa-{Environment.ProcessId}-{Guid.NewGuid():N}";
        // Shell execute, hidden: a plain start would let the session inherit the caller's handles, and a caller that
        // reads oml's output through a pipe would wait for the session to end. The session reports a start error in
        // ErrorFile instead of its output.
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var a in new[] { "browser", "serve", pipe }) info.ArgumentList.Add(a);
        foreach (var (key, value) in o.Values) { info.ArgumentList.Add(key); if (value is not null) info.ArgumentList.Add(value); }
        using var server = Process.Start(info) ?? throw new QaUsage("Could not start the browser session.");
        for (var clock = Stopwatch.StartNew(); clock.Elapsed < TimeSpan.FromSeconds(60); Thread.Sleep(100))
        {
            if (ReadState() is { } s && s.Pipe == pipe)
            {
                TerminalOutput.PrintObject(Describe(s, started: true));
                return 0;
            }
            if (server.HasExited)
                throw new QaUsage("The browser session stopped at start: " + (File.Exists(ErrorFile) ? File.ReadAllText(ErrorFile).Trim() : $"exit code {server.ExitCode}"));
        }
        try { server.Kill(true); } catch (InvalidOperationException) { }
        throw new QaUsage("The browser session did not start in 60 seconds.");
    }

    static string ErrorFile => Path.ChangeExtension(StateFile, ".error.txt");

    public static int Stop()
    {
        var s = ReadState();
        if (s is null || !Running(s))
        {
            File.Delete(StateFile);
            TerminalOutput.PrintObject(new { session = "none" });
            return 0;
        }
        try { Send(s, new JsonObject { ["stop"] = true }, 15_000); }
        catch (Exception e) when (e is IOException or TimeoutException or JsonException)
        {
            try { using var p = Process.GetProcessById(s.Pid); p.Kill(true); } catch (Exception x) when (x is ArgumentException or InvalidOperationException) { }
        }
        File.Delete(StateFile);
        TerminalOutput.PrintObject(s.Attached is null ? (object)new { session = "stopped" }
            : new { session = "detached", note = "oml's tab is closed; your browser and its tabs stay open." });
        return 0;
    }

    // A run or page request for the running session, with its file paths made absolute (the session has its own
    // current folder). Null when no session runs, so the caller starts its own browser.
    public static (int Code, JsonObject Output)? Forward(string[] args)
    {
        var s = ReadState();
        if (s is null) return null;
        if (!Running(s)) { File.Delete(StateFile); return null; }
        var sent = args.ToArray();
        if (sent[0] is "run" or "diff") sent[1] = Path.GetFullPath(sent[1]);
        if (sent[0] is "raw" && sent.Length > 2 && !sent[2].TrimStart().StartsWith('{')) sent[2] = Path.GetFullPath(sent[2]);
        for (var i = 2; i < sent.Length - 1; i++)
            if (sent[i] is "--out" or "--screenshot" or "--snapshot" or "--js") sent[i + 1] = Path.GetFullPath(sent[i + 1]);
            else if (sent[i] is "--css" && File.Exists(sent[i + 1])) sent[i + 1] = Path.GetFullPath(sent[i + 1]);
            else if (sent[i] is "--html" or "--append" && sent[i + 1].IndexOf('=') is var eq and > 0)
                sent[i + 1] = sent[i + 1][..(eq + 1)] + Path.GetFullPath(sent[i + 1][(eq + 1)..]);
        JsonObject answer;
        // The session runs in its own folder: the test accounts are the caller's project's.
        try { answer = Send(s, new JsonObject { ["args"] = new JsonArray([.. sent.Select(a => (JsonNode)a)]), ["accounts"] = TestAccounts.PathFor() }, 30 * 60_000); }
        catch (TimeoutException) { throw new QaUsage("The browser session is busy with another request. Wait, or run with --no-session."); }
        return (answer["code"]!.GetValue<int>(), answer["output"]!.AsObject());
    }

    static JsonObject Send(State s, JsonObject request, int answerMs)
    {
        using var pipe = new NamedPipeClientStream(".", s.Pipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        pipe.Connect(3_000);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        request["terminal"] = !Console.IsInputRedirected;
        writer.WriteLine(request.ToJsonString());
        var clock = Stopwatch.StartNew();
        var listening = false;
        while (true)
        {
            var line = reader.ReadLineAsync();
            if (!line.Wait(Math.Max(1, answerMs - (int)clock.ElapsedMilliseconds))) throw new TimeoutException();
            var answer = JsonNode.Parse(line.Result ?? throw new IOException("The browser session closed the connection."))!.AsObject();
            if (answer["attention"] is null) return answer;
            // A handoff step in the session: the attention line goes to this terminal, and Enter pressed here goes back.
            Console.Error.WriteLine(answer.ToJsonString());
            if (listening || Console.IsInputRedirected) continue;
            listening = true;
            _ = Task.Run(() =>
            {
                try { if (Console.ReadLine() is not null) writer.WriteLine("{\"enter\":true}"); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            });
        }
    }

    // The background process: one browser, requests one by one, until stop, idle or the browser is gone.
    public static int Serve(string pipeName, QaCommand.Settings o)
    {
        var (width, height) = (o.Int("--width", 1366), o.Int("--height", 900));
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        IQaEngine engine;
        string? note;
        try
        {
            (engine, note) = QaCommand.Engine(o, width, height).GetAwaiter().GetResult();
            if (engine.Mode == "uia") engine.Navigate("about:blank").GetAwaiter().GetResult(); // opens the window now, not on the first request
        }
        catch (Exception e)
        {
            File.WriteAllText(ErrorFile, e.Message);
            return 2;
        }
        using var browser = engine;
        File.WriteAllText(StateFile, JsonSerializer.Serialize(new State(pipeName, Environment.ProcessId, browser.Mode, note, width, height, DateTime.Now, (browser as CdpEngine)?.Attached)));
        try
        {
            while (browser.Alive)
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var idle = new CancellationTokenSource(Idle);
                try { pipe.WaitForConnectionAsync(idle.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { break; }
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var request = JsonNode.Parse(reader.ReadLine() ?? "{}")!.AsObject();
                if (request["stop"] is not null) { writer.WriteLine(new JsonObject { ["stopped"] = true }.ToJsonString()); break; }
                var args = request["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
                // A handoff step reaches the person through the caller: attention lines go back on the pipe, and when the
                // caller has a terminal, its Enter comes back as {"enter": true}.
                var person = new QaHandoff(line => { try { writer.WriteLine(line); } catch (IOException) { } },
                    request["terminal"]?.GetValue<bool>() == true ? async cancel =>
                    {
                        while (await reader.ReadLineAsync(cancel) is { } line) if (line.Contains("enter", StringComparison.Ordinal)) return;
                        await Task.Delay(Timeout.Infinite, cancel);   // the caller went away: only the condition can end the wait
                    } : null);
                (int Code, JsonObject Output) answer;
                TestAccounts.FileOverride = request["accounts"]?.GetValue<string>();
                try { answer = QaCommand.Execute(args, (browser, note), person).GetAwaiter().GetResult(); }
                catch (Exception e) when (e is QaUsage or QaFailure or CdpException or IOException) { answer = (2, new JsonObject { ["error"] = e.Message }); }
                try { writer.WriteLine(new JsonObject { ["code"] = answer.Code, ["output"] = answer.Output }.ToJsonString()); }
                catch (IOException) { } // the caller went away; the session goes on
            }
        }
        finally
        {
            if (ReadState() is { } s && s.Pipe == pipeName) File.Delete(StateFile);
        }
        return 0;
    }
}
