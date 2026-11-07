using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli.Qa;

// `oml qa start` keeps one browser open in a background `oml qa serve` process, so later `qa run` and `qa page`
// skip the browser's cold start (6 to 8 seconds) and keep the app's login. They find the session through a state
// file and talk to it over a named pipe that only the same Windows user can open. One request at a time.
// The session stops on `oml qa stop`, after 30 minutes without a request, or when its browser is closed.
static class QaSession
{
    static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);
    static string StateFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oml-cli", "qa-session.json");

    sealed record State(string Pipe, int Pid, string Engine, string? Note, int Width, int Height, DateTime Started);

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
        viewport = new { width = s.Width, height = s.Height }, since = s.Started.ToString("s"),
        hint = "qa run and qa page now use this browser. oml qa stop closes it.",
    };

    public static int Start(QaCommand.Settings o)
    {
        if (ReadState() is { } existing && Running(existing))
        {
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
        foreach (var a in new[] { "qa", "serve", pipe }) info.ArgumentList.Add(a);
        foreach (var (key, value) in o.Values) { info.ArgumentList.Add(key); if (value is not null) info.ArgumentList.Add(value); }
        using var server = Process.Start(info) ?? throw new QaUsage("Could not start the QA session.");
        for (var clock = Stopwatch.StartNew(); clock.Elapsed < TimeSpan.FromSeconds(60); Thread.Sleep(100))
        {
            if (ReadState() is { } s && s.Pipe == pipe)
            {
                TerminalOutput.PrintObject(Describe(s, started: true));
                return 0;
            }
            if (server.HasExited)
                throw new QaUsage("The QA session stopped at start: " + (File.Exists(ErrorFile) ? File.ReadAllText(ErrorFile).Trim() : $"exit code {server.ExitCode}"));
        }
        try { server.Kill(true); } catch (InvalidOperationException) { }
        throw new QaUsage("The QA session did not start in 60 seconds.");
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
        TerminalOutput.PrintObject(new { session = "stopped" });
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
        if (sent[0] == "run") sent[1] = Path.GetFullPath(sent[1]);
        for (var i = 2; i < sent.Length - 1; i++)
            if (sent[i] is "--out" or "--screenshot" or "--css") sent[i + 1] = Path.GetFullPath(sent[i + 1]);
        JsonObject answer;
        try { answer = Send(s, new JsonObject { ["args"] = new JsonArray([.. sent.Select(a => (JsonNode)a)]) }, 30 * 60_000); }
        catch (TimeoutException) { throw new QaUsage("The QA session is busy with another request. Wait, or run with --no-session."); }
        return (answer["code"]!.GetValue<int>(), answer["output"]!.AsObject());
    }

    static JsonObject Send(State s, JsonObject request, int answerMs)
    {
        using var pipe = new NamedPipeClientStream(".", s.Pipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        pipe.Connect(3_000);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        writer.WriteLine(request.ToJsonString());
        var line = reader.ReadLineAsync();
        if (!line.Wait(answerMs)) throw new TimeoutException();
        return JsonNode.Parse(line.Result ?? throw new IOException("The QA session closed the connection."))!.AsObject();
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
        File.WriteAllText(StateFile, JsonSerializer.Serialize(new State(pipeName, Environment.ProcessId, browser.Mode, note, width, height, DateTime.Now)));
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
                (int Code, JsonObject Output) answer;
                try { answer = QaCommand.Execute(args, (browser, note)).GetAwaiter().GetResult(); }
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
