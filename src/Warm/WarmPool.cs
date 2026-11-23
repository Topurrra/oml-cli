using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// Warm workers: a model command (CommandTable.Warms) goes to a background oml process that has Service Studio's model
// loaded already, so it takes a fraction of a second instead of the 2 to 5 s that Service Studio's start-up costs every
// fresh process. The caller prints exactly what the worker sends (stdout, stderr, exit code); the usage log line is the
// caller's, as for any command.
//   - A pool of performance.warmWorkers workers (project config, default 2). A worker runs one command at a time
//     (the current folder and the console are per process); when every worker is busy, the caller runs the command
//     itself, so parallel agents never wait in line behind one worker.
//   - A worker belongs to one oml build and one Service Studio build (the key). It runs from a copy of oml's files
//     (bin-<key> in the state folder), so a rebuild or an update of oml is never blocked by a running worker.
//   - It stops after performance.warmIdleMinutes without a command (default 20), and on oml warm stop.
//   - Anything that goes wrong on the warm path runs the command here instead (cold). The one exception: when the
//     worker dies after the command printed something, the caller reports it as the crash it is.
// No command takes a worker when the build's self-test is not recorded yet: the self-test then runs cold, as before.
internal static class WarmPool
{
    const int Protocol = 1;
    internal const int MaxWorkers = 8;
    // Never sent to a worker, and removed from a worker's own environment: the warm commands do not use them.
    internal static readonly string[] Secrets = ["OML_LIFETIME_TOKEN", "OML_LIFETIME_HOST", "OML_SERVER_PASSWORD", "OML_SERVER_USER", "OML_SERVER_HOST", "OML_TESTDATA_KEY"];

    internal static string StateDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oml-cli", "warm");
    internal static string PipeName(string key, int slot) => $"oml-warm-{key}-{slot}";
    internal static string MutexName(string key, int slot) => $"oml-warm-{key}-{slot}";
    internal static string StatePath(string key, int slot) => Path.Combine(StateDir, $"{key}-{slot}.json");

    // This oml build and this Service Studio build.
    internal sealed record Build(string Key, string ServiceStudio, string ServiceStudioBuild, string BinDir);

    static readonly string[] KeyFiles = ["oml.exe", "oml.dll", "OmlCli.Core.dll", "oml.deps.json", "oml.runtimeconfig.json"];

    // Null when no worker can serve this process: Service Studio or one of oml's files is missing.
    internal static Build? Current()
    {
        try
        {
            var ss = Path.GetFullPath(Locations.ServiceStudio);
            var version = Path.Combine(ss, "Version.txt");
            var model = new FileInfo(Path.Combine(ss, "OutSystems.Model.V1.dll"));
            if (!File.Exists(version) || !model.Exists) return null;
            var build = File.ReadAllText(version).Split(' ', '\r', '\n')[0];
            var bin = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var text = new StringBuilder($"{Protocol}|{Environment.UserName}|{Process.GetCurrentProcess().SessionId}|{bin.ToLowerInvariant()}|{ss.ToLowerInvariant()}|{build}|{model.Length}|{model.LastWriteTimeUtc.Ticks}");
            foreach (var name in KeyFiles)
            {
                var file = new FileInfo(Path.Combine(bin, name));
                if (!file.Exists) return null;
                text.Append($"|{name}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
            }
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant()[..16];
            return new Build(key, ss, build, bin);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    internal static bool Alive(string key, int slot)
    {
        if (!Mutex.TryOpenExisting(MutexName(key, slot), out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    // The project's settings, read from the caller's folder. OML_WARM=0 or 1 (for oml's own test suite) wins over them.
    internal sealed record Settings(bool On, int Workers, int IdleMinutes, string? Forced);

    internal static Settings Read()
    {
        var values = ProjectConfig.Load(null);
        var forced = Environment.GetEnvironmentVariable("OML_WARM") is "0" or "1" ? Environment.GetEnvironmentVariable("OML_WARM") : null;
        var on = forced is not null ? forced == "1" : values.On("performance.warm");
        return new Settings(on, Number(values, "performance.warmWorkers"), Number(values, "performance.warmIdleMinutes"), forced is null ? null : $"OML_WARM={forced}");
    }

    static int Number(ProjectConfig.Values values, string key) =>
        int.TryParse(values.Get(key), out var n) ? n : int.Parse(ProjectConfig.Find(key)!.Default);

    // ---------------------------------------------------------------- the caller

    // The command's exit code when a worker ran it, or null: then the caller runs it itself. fromMcp: `oml mcp` sends
    // a call it would run in its own process (oml code starts a new oml mcp for every program, which would load
    // Service Studio again each time); its Console is the call's buffer, so the worker's output lands there.
    public static int? Run(string[] args, string[] filtered, bool fromMcp = false)
    {
        if (McpServer.Active && !fromMcp || WarmWorker.Active || Program.LoadedCore is not null || !CommandTable.Warms(filtered)) return null;
        Settings settings;
        Build? build;
        try
        {
            settings = Read();
            if (!settings.On || settings.Workers < 1) return null;
            build = Current();
            if (build is null || !SelfTestRecorded(build.ServiceStudioBuild)) return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }

        string? request = null;
        var workers = Math.Min(settings.Workers, MaxWorkers);
        var spawn = true;
        int? free = null;
        for (var slot = 0; slot < workers; slot++)
        {
            if (!Alive(build.Key, slot)) { free ??= slot; continue; }
            request ??= Request(args, filtered, settings);
            var (outcome, code) = Send(build.Key, slot, request);
            if (outcome == Outcome.Ran) return code;
            if (outcome == Outcome.Lost) return null;   // it died before it printed anything: run it here
            if (outcome != Outcome.Busy) spawn = false;   // one is starting (or refused): no new one yet
        }
        // Every worker that runs is busy, and the pool has room: one more for the next command. This one runs here.
        if (spawn && free is { } empty) Spawn(build, empty, settings.IdleMinutes);
        return null;
    }

    static bool SelfTestRecorded(string build)
    {
        var record = Locations.SelfTestRecord;
        return File.Exists(record) && File.ReadAllLines(record).Contains(build);
    }

    enum Outcome { Ran, Busy, Starting, Refused, Unreachable, Lost }

    static string Request(string[] args, string[] filtered, Settings settings)
    {
        var env = new JsonObject();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string name && !Secrets.Contains(name, StringComparer.OrdinalIgnoreCase)) env[name] = e.Value as string ?? "";
        var timings = filtered.Contains("--timings");
        return new JsonObject
        {
            ["op"] = "run",
            ["args"] = new JsonArray(args.Select(a => (JsonNode?)a).ToArray()),
            ["filtered"] = new JsonArray(filtered.Select(a => (JsonNode?)a).ToArray()),
            ["cwd"] = Environment.CurrentDirectory,
            ["env"] = env,
            ["pretty"] = JsonOutput.Pretty,
            ["forceJson"] = TerminalOutput.ForceJson,
            ["forceHuman"] = TerminalOutput.ForceHuman == true,   // the menu sets it
            ["stdoutRedirected"] = Console.IsOutputRedirected,
            ["stderrRedirected"] = Console.IsErrorRedirected,
            ["width"] = ConsoleFacts.OwnWidth(),
            ["unicode"] = ConsoleFacts.OwnUnicode(),
            ["colors"] = Console.IsOutputRedirected ? false : TerminalColors.Enabled,
            ["startupMs"] = timings ? (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds : 0,
            ["idleMinutes"] = settings.IdleMinutes,
        }.ToJsonString();
    }

    static (Outcome, int) Send(string key, int slot, string request)
    {
        var wrote = false;
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(key, slot), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            try { pipe.Connect(300); }
            catch (TimeoutException) { return (Outcome.Starting, 0); }
            WarmFrames.Write(pipe, 'q', request);
            while (WarmFrames.Read(pipe) is { } frame)
            {
                switch (frame.Kind)
                {
                    case 'o': Pass(Console.Out, frame.Text); wrote = true; break;
                    case 'e': Pass(Console.Error, frame.Text); wrote = true; break;
                    case 'b': return (Outcome.Busy, 0);
                    case 's': return (Outcome.Starting, 0);
                    case 'r': return (Outcome.Refused, 0);   // always before any output
                    case 'x':
                        var end = JsonNode.Parse(frame.Text)!.AsObject();
                        if (end["crash"]?.GetValue<string>() is { } crash) UsageLog.CrashType = crash;
                        Console.Out.Flush();
                        return (Outcome.Ran, end["code"]!.GetValue<int>());
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or ObjectDisposedException) { }
        if (!wrote) return (Outcome.Lost, 0);
        // The worker died in the middle of the command, after it printed part of the answer: the same report as a crash
        // the launcher catches (Program.Guard), and the usage log notes it.
        UsageLog.CrashType = "WarmWorkerLost";
        TerminalOutput.PrintObject(new { error = "The warm oml worker stopped in the middle of this command.", hint = "This is a bug in oml. Report the command and this message; the usage log notes it as a crash. Run the command again; oml warm stop stops every worker." });
        return (Outcome.Ran, 1);
    }

    // This process's own stdout or stderr failing (its reader went away) is not a worker problem: it goes to the
    // launcher's guard as it would in a cold run.
    sealed class CallerOutputException(IOException inner) : Exception(inner.Message, inner);

    static void Pass(TextWriter writer, string text)
    {
        try { writer.Write(text); }
        catch (IOException e) { throw new CallerOutputException(e); }
    }

    // A worker for this slot, in the background (hidden, with no handle of this process, so a caller that reads this
    // process's output through a pipe does not wait for the worker). It runs from its own copy of oml's files.
    static void Spawn(Build build, int slot, int idleMinutes)
    {
        try
        {
            // Workers of an older build of this same oml folder can never serve a command again: they stop now
            // instead of after their idle time.
            foreach (var old in States().Where(s => s.Key != build.Key && string.Equals(s.Source, build.BinDir, StringComparison.OrdinalIgnoreCase)))
                Ask(old, "stop");
            var exe = ShadowCopy(build);
            var info = new ProcessStartInfo(exe) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = StateDir };
            foreach (var a in new[] { "warm", "serve", build.Key, slot.ToString(), idleMinutes.ToString(), build.ServiceStudio, build.BinDir }) info.ArgumentList.Add(a);
            Process.Start(info)?.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    // bin-<key>: oml's files for this build. Copied to a new folder that is renamed into place when complete, so two
    // callers that start workers at once never see half a copy. Older copies no worker runs from are removed.
    static string ShadowCopy(Build build)
    {
        Directory.CreateDirectory(StateDir);
        var dir = Path.Combine(StateDir, "bin-" + build.Key);
        if (!File.Exists(Path.Combine(dir, ".complete")))
        {
            var temp = dir + "-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            Directory.CreateDirectory(temp);
            foreach (var file in Directory.EnumerateFiles(build.BinDir).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".exe" or ".dll" or ".json" or ".pdb"))
                File.Copy(file, Path.Combine(temp, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(temp, ".complete"), build.BinDir);
            try { Directory.Move(temp, dir); }
            catch (IOException) { TryDelete(temp); }   // another caller was first
            RemoveOldCopies(build.Key);
        }
        return Path.Combine(dir, "oml.exe");
    }

    static void RemoveOldCopies(string key)
    {
        foreach (var old in Directory.EnumerateDirectories(StateDir, "bin-*"))
        {
            if (Path.GetFileName(old) == "bin-" + key) continue;
            var marker = Path.Combine(old, ".complete");
            // A half copy of another caller (.tmp) or a copy made in the last minutes may be about to start a worker.
            var age = DateTime.UtcNow - (File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : Directory.GetLastWriteTimeUtc(old));
            if (age < TimeSpan.FromMinutes(10)) continue;
            try { using (new FileStream(Path.Combine(old, "oml.exe"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
            catch (FileNotFoundException) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }   // a worker runs from it
            TryDelete(old);
        }
    }

    static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- oml warm status | stop

    internal sealed record State(string Key, int Slot, int Pid, DateTime Started, string ServiceStudio, string ServiceStudioBuild, string Source);

    static IEnumerable<State> States()
    {
        if (!Directory.Exists(StateDir)) yield break;
        foreach (var file in Directory.EnumerateFiles(StateDir, "*.json"))
        {
            State? state = null;
            try { state = JsonSerializer.Deserialize<State>(File.ReadAllText(file)); }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
            if (state is null) continue;
            if (!Alive(state.Key, state.Slot)) { try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } continue; }
            yield return state;
        }
    }

    // A worker's own answer about itself (busy, commands, memory), or null when it does not answer at once.
    static JsonObject? Ask(State s, string op)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(s.Key, s.Slot), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(1000);
            WarmFrames.Write(pipe, 'q', new JsonObject { ["op"] = op }.ToJsonString());
            return WarmFrames.Read(pipe) is { } frame && frame.Kind is 'i' or 'k' ? JsonNode.Parse(frame.Text) as JsonObject : null;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or JsonException or InvalidDataException) { return null; }
    }

    const string Usage = "Usage: oml warm [status] | oml warm stop [--all]";

    public static int Command(string[] rest)
    {
        if (rest.Contains("--help") || rest.Contains("-h")) { Console.WriteLine(Usage); return 0; }
        var current = Current()?.Key;
        switch (rest)
        {
            case [] or ["status"]:
                var settings = Read();
                var workers = States().OrderBy(s => s.Key).ThenBy(s => s.Slot).Select(s =>
                {
                    var info = Ask(s, "status");
                    return new
                    {
                        slot = s.Slot, pid = s.Pid, thisBuild = s.Key == current, since = s.Started.ToString("s"), serviceStudio = s.ServiceStudioBuild, oml = s.Source,
                        ready = info?["ready"]?.GetValue<bool>(), busy = info?["busy"]?.GetValue<bool>(), commands = info?["commands"]?.GetValue<int>(), idleSeconds = info?["idleSeconds"]?.GetValue<long>(), memoryMb = info?["memoryMb"]?.GetValue<long>(),
                    };
                }).ToArray();
                TerminalOutput.PrintObject(new
                {
                    warm = settings.On, workers = settings.Workers, idleMinutes = settings.IdleMinutes, forcedBy = settings.Forced, running = workers,
                    note = settings.On ? "Model commands (inspect, describe, apply, check, ...) run in these workers; oml warm stop stops them." : "Warm workers are off here (performance.warm in .oml/oml-config.json): every command starts Service Studio itself.",
                });
                return 0;
            case ["stop", .. var options] when options.All(o => o == "--all"):
                var all = options.Contains("--all");
                var stopped = 0;
                foreach (var s in States().Where(s => all || s.Key == current).ToList())
                {
                    Ask(s, "stop");   // it ends after the command it runs, if any
                    try
                    {
                        using var process = Process.GetProcessById(s.Pid);
                        if (!process.WaitForExit(30_000)) process.Kill();
                        process.WaitForExit(5_000);
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    try { File.Delete(StatePath(s.Key, s.Slot)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    stopped++;
                }
                TerminalOutput.PrintObject(new { stopped, note = stopped == 0 ? "No warm worker ran" + (all ? "." : " for this oml build (--all: any build).") : null });
                return 0;
            default:
                TerminalOutput.PrintObject(new { error = $"Unknown warm command. {Usage}" });
                return 2;
        }
    }

    // For oml doctor: how many workers run for this build, or null when none.
    internal static string? Summary()
    {
        try
        {
            var current = Current()?.Key;
            var count = States().Count(s => s.Key == current);
            return count == 0 ? null : $"{count} warm worker{(count == 1 ? "" : "s")} for this oml build (oml warm status).";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
