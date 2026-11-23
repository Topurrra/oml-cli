using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml warm serve <key> <slot> <idle minutes> <Service Studio folder> <oml folder>` (hidden; WarmPool starts it): one
// warm worker. It loads Service Studio's model once, then runs the commands of other oml processes, one at a time, each
// as a fresh process would: the caller's folder, environment and console facts, the same output and exit code.
// Threads: the listener accepts connections and answers status, stop, starting and busy at once; commands run on one
// command thread (the current folder, the console and the model are per process). It ends after the idle time, on
// oml warm stop (after the command it runs), when memory stays high, or when a self-test past its limit holds the model.
internal static class WarmWorker
{
    // Set in the worker: the launcher then never sends a command on to another worker.
    public static bool Active;

    const long TrimMb = 1536, ExitMb = 3072;
    static int busy;   // 0 idle, 1 a command runs, 2 closing
    static volatile bool ready, stopping;
    static long lastUsed = Environment.TickCount64;
    static int commands, idleMinutes;
    static readonly BlockingCollection<(NamedPipeServerStream Pipe, JsonObject Request)> Queue = new();
    static readonly ManualResetEventSlim Wake = new();

    public static int Serve(string[] rest)
    {
        if (rest is not [var key, var slotText, var idleText, var ss, var source] || !int.TryParse(slotText, out var slot) || !int.TryParse(idleText, out idleMinutes))
        {
            Console.Error.WriteLine("oml warm serve is started by oml itself.");
            return 2;
        }
        Active = true;
        foreach (var name in WarmPool.Secrets) Environment.SetEnvironmentVariable(name, null);
        Console.SetIn(TextReader.Null);
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        using var mutex = new Mutex(false, WarmPool.MutexName(key, slot));
        try { if (!mutex.WaitOne(0)) return 0; }   // another worker has this slot
        catch (AbandonedMutexException) { }        // the last one died: the slot is ours
        var home = WarmPool.StateDir;
        Directory.CreateDirectory(home);
        Environment.CurrentDirectory = home;
        var state = WarmPool.StatePath(key, slot);
        try
        {
            // The state file first, so oml warm status and stop see a worker that is still starting.
            var build = File.ReadAllText(Path.Combine(ss, "Version.txt")).Split(' ', '\r', '\n')[0];
            File.WriteAllText(state, JsonSerializer.Serialize(new WarmPool.State(key, slot, Environment.ProcessId, DateTime.Now, ss, build, source)));
            new Thread(() => Listen(WarmPool.PipeName(key, slot))) { IsBackground = true, Name = "oml warm listener" }.Start();
            if (Program.CoreProblem(ss) is not null) return 2;
            var cli = Program.LoadCore(ss).GetType("OmlCli.Cli", throwOnError: true)!;
            cli.GetMethod("WarmUp")!.Invoke(null, [ss]);
            if (stopping) return 0;
            new Thread(() => Work(cli, ss, home), 16 * 1024 * 1024) { IsBackground = true, Name = "oml warm" }.Start();
            Interlocked.Exchange(ref lastUsed, Environment.TickCount64);
            ready = true;
            while (true)
            {
                Wake.Wait(TimeSpan.FromSeconds(5));
                Wake.Reset();
                var idle = Environment.TickCount64 - Interlocked.Read(ref lastUsed) > idleMinutes * 60_000L;
                if ((stopping || idle) && Interlocked.CompareExchange(ref busy, 2, 0) == 0) break;
            }
            return 0;
        }
        catch (Exception) { return 2; }   // Service Studio did not start: callers run their commands themselves
        finally
        {
            try { File.Delete(state); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static void Listen(string name)
    {
        while (true)
        {
            NamedPipeServerStream pipe;
            try { pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(200); continue; }
            try { pipe.WaitForConnection(); }
            catch (IOException) { pipe.Dispose(); continue; }
            ThreadPool.QueueUserWorkItem(_ => Accept(pipe));
        }
    }

    static void Accept(NamedPipeServerStream pipe)
    {
        var handed = false;
        try
        {
            if (WarmFrames.Read(pipe) is not { Kind: 'q' } frame || JsonNode.Parse(frame.Text) is not JsonObject request) return;
            switch (request["op"]?.GetValue<string>())
            {
                case "status":
                    WarmFrames.Write(pipe, 'i', Status());
                    break;
                case "stop":
                    stopping = true;
                    WarmFrames.Write(pipe, 'k', "{}");
                    Wake.Set();
                    break;
                case "run" when !ready:
                    WarmFrames.Write(pipe, 's', "");
                    break;
                case "run":
                    if (stopping || Interlocked.CompareExchange(ref busy, 1, 0) != 0) { WarmFrames.Write(pipe, 'b', ""); break; }
                    handed = true;
                    Queue.Add((pipe, request));
                    break;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or ObjectDisposedException or InvalidOperationException or EndOfStreamException) { }
        finally { if (!handed) pipe.Dispose(); }
    }

    static string Status() => new JsonObject
    {
        ["busy"] = Volatile.Read(ref busy) == 1, ["ready"] = ready, ["commands"] = Volatile.Read(ref commands),
        ["idleSeconds"] = (Environment.TickCount64 - Interlocked.Read(ref lastUsed)) / 1000, ["idleMinutes"] = idleMinutes,
        ["memoryMb"] = Environment.WorkingSet / (1024 * 1024),
    }.ToJsonString();

    static void Work(Type cli, string ss, string home)
    {
        foreach (var (pipe, request) in Queue.GetConsumingEnumerable())
        {
            try { Run(pipe, request, cli, ss, home); }
            catch (Exception) { }   // nothing or part of the answer reached the caller: it runs the command itself or reports the crash
            finally
            {
                pipe.Dispose();
                Interlocked.Increment(ref commands);
                Interlocked.Exchange(ref lastUsed, Environment.TickCount64);
                Volatile.Write(ref busy, 0);
                Wake.Set();
            }
        }
    }

    static void Run(NamedPipeServerStream pipe, JsonObject r, Type cli, string ss, string home)
    {
        string[] Strings(string k) => r[k]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        bool Flag(string k) => r[k]?.GetValue<bool>() == true;
        var (args, filtered) = (Strings("args"), Strings("filtered"));
        if (r["idleMinutes"]?.GetValue<int>() is int idle and > 0) idleMinutes = idle;
        SetEnvironment(r["env"] as JsonObject);
        try { Environment.CurrentDirectory = r["cwd"]!.GetValue<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            WarmFrames.Write(pipe, 'r', "The caller's folder cannot be opened here.");
            return;
        }
        using var sink = new WarmSink(pipe);
        var (code, crash) = (2, (string?)null);
        var (stdoutRedirected, stderrRedirected, unicode, colors) = (Flag("stdoutRedirected"), Flag("stderrRedirected"), Flag("unicode"), Flag("colors"));
        var width = r["width"]?.GetValue<int>() ?? 100;
        Console.SetOut(new WarmWriter(sink, 'o'));
        Console.SetError(new WarmWriter(sink, 'e'));
        try
        {
            if (cli.GetMethod("WarmBegin")!.Invoke(null, [ss, stdoutRedirected, stderrRedirected, width, unicode, colors, r["startupMs"]?.GetValue<long>() ?? 0L]) is string refusal)
            {
                stopping = true;   // a self-test past its limit may still hold the model: no more commands here
                sink.Send('r', refusal);
                return;
            }
            // The launcher's own state, as Program.Main sets it in the caller.
            ConsoleFacts.Set(stdoutRedirected, stderrRedirected, width, unicode);
            TerminalColors.Enabled = colors;
            TerminalOutput.Reset(filtered.FirstOrDefault());
            TerminalOutput.ForceJson = Flag("forceJson");
            if (Flag("forceHuman")) TerminalOutput.ForceHuman = true;
            JsonOutput.Pretty = Flag("pretty");
            UsageLog.CrashType = null;
            code = Program.Guard(() => Program.RunTail(args, filtered));
            crash = UsageLog.CrashType;
        }
        finally
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            try { Environment.CurrentDirectory = home; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        sink.Flush();
        sink.Send('x', new JsonObject { ["code"] = code, ["crash"] = crash }.ToJsonString());
        if ((long)cli.GetMethod("WarmEnd")!.Invoke(null, [TrimMb])! > ExitMb) stopping = true;
    }

    // The caller's environment, exactly: what it lacks is removed, so nothing of an earlier caller stays.
    static void SetEnvironment(JsonObject? env)
    {
        if (env is null) return;
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in env) wanted[name] = value?.GetValue<string>() ?? "";
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string name && !wanted.ContainsKey(name)) Environment.SetEnvironmentVariable(name, null);
        foreach (var (name, value) in wanted)
            if (Environment.GetEnvironmentVariable(name) != value) Environment.SetEnvironmentVariable(name, value.Length == 0 ? null : value);
    }
}
