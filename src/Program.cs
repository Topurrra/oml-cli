using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace OmlCli;

// Launcher: references no OutSystems type. It loads OmlCli.Core into a context that resolves from the
// installed Service Studio, so whatever library versions that build ships are the ones used.
public static class Program
{
    public static int Main(string[] args)
    {
        SetUpConsole();
        args = CliUsage.Normalize(args); // an old command name (lt, census, ...) is its new name from here on
        JsonOutput.Pretty = args.Contains("--pretty");
        var filtered = args.Where(a => a != "--pretty").ToArray();
        // `--json` (force JSON) and OML_OUTPUT=json|human decide the human view; see TerminalOutput.
        if (TerminalOutput.ForceJsonFlag(filtered)) { TerminalOutput.ForceJson = true; filtered = TerminalOutput.StripJson(filtered); }
        // These commands run in the launcher (or print their own output) and have no phases to time.
        if (filtered.Contains("--timings") && filtered.FirstOrDefault() is { } command && TimingsRefused.Contains(command))
        {
            TerminalOutput.PrintObject(new { error = TimingsRefusal(command) });
            return 2;
        }
        if (filtered.FirstOrDefault() == "log" && CommandTable.Find(filtered) is { } log) return log.Run!(args, filtered);
        // The menu is for a person at a terminal: taken here, before the usage log copies stdout, and a bare `oml` only when MenuModel.Bare says so.
        if (filtered.FirstOrDefault() == "menu" && CommandTable.Find(filtered) is { } menu) return menu.Run!(args, filtered);
        // oml mcp: a long-running server; the usage log must not copy its protocol stream. Each call is logged on its own.
        if (filtered.FirstOrDefault() == "mcp" && CommandTable.Find(filtered) is { } mcp) return mcp.Run!(args, filtered);
        if (args.Length == 0 && MenuModel.Bare(Console.IsInputRedirected, Console.IsOutputRedirected, Environment.GetEnvironmentVariable) == BareChoice.Menu) return MenuApp.Run([]);
        // The usage log: one line per command, written after it ends (on unless OML_LOG=0; then nothing is wrapped or written).
        return UsageLog.Enabled ? UsageLog.Measure(filtered, () => Dispatch(args, filtered)) : Dispatch(args, filtered);
    }

    internal static readonly string[] TimingsRefused = CommandTable.All.Where(c => c.NoTimings).Select(c => c.Name).ToArray(); // from the table
    internal static string TimingsRefusal(string command) => $"--timings is not available for {command}.";
    // Output encoding (commands.md, "Output encoding"). A console window gets UTF-16 through WriteConsoleW: every
    // character shows, and the console code page is not changed (.NET never sets code page 1200 on the console).
    // A pipe or a file gets UTF-8, not the console code page, which turns non-ASCII text into '?' or a plain '"'.
    // Core runs in this process and writes through the same Console, so this covers every command.
    static void SetUpConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!Console.IsOutputRedirected || !Console.IsErrorRedirected) Console.OutputEncoding = Encoding.Unicode;
        var utf8 = new UTF8Encoding(false);
        if (Console.IsOutputRedirected) Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        if (Console.IsErrorRedirected) Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
    }

    // The last guard: whatever a command did not handle itself becomes one JSON error, never a .NET stack trace.
    // Core is called by reflection, so the real exception sits inside a TargetInvocationException.
    internal static int Dispatch(string[] args, string[] filtered)
    {
        try { return DispatchCommand(args, filtered); }
        catch (Exception e)
        {
            while (e is TargetInvocationException { InnerException: { } inner }) e = inner;
            UsageLog.CrashType = e.GetType().Name; // the usage log still sees a crash, though the exit code is an ordinary one
            if (Environment.GetEnvironmentVariable("OML_STACK") == "1") Console.Error.WriteLine(e);   // for oml's own debugging
            var path = e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
            TerminalOutput.PrintObject(new { error = e.Message, hint = path ? "Check the path and the arguments of the command (oml <command> --help)." : "This is a bug in oml. Report the command and this message; the usage log notes it as a crash." });
            return path ? 2 : 1;
        }
    }

    static int DispatchCommand(string[] args, string[] filtered)
    {
        TerminalOutput.Command = filtered.FirstOrDefault();
        // `oml`, `oml help`, `oml --help`: the overview, or every usage line with --all. Needs no Service Studio.
        if (CliUsage.IsHelp(filtered)) { TerminalOutput.PrintHelp(CliUsage.HelpText(filtered), null); return 0; }
        // Commands the launcher runs itself print their usage lines here, without Service Studio.
        if (filtered.FirstOrDefault() is { } helpName && CommandTable.PrintsHelp(helpName) && (filtered.Contains("--help") || filtered.Contains("-h")))
        {
            // lifetime: one line per subcommand, with its options.
            TerminalOutput.PrintHelp(filtered[0] == "lifetime" ? LifeTime.Usage.Replace(" | ", Environment.NewLine + "oml lifetime ") : CliUsage.For(filtered[0]), filtered[0]);
            return 0;
        }
        // qa, test scenarios and the commands a project can turn off (commands.*) follow the project configuration (ProjectConfig.Adjust).
        {
            var (adjusted, refusal) = ProjectConfig.Adjust(filtered);
            if (refusal is not null) { TerminalOutput.PrintObject(new { error = refusal }); return 2; }
            if (adjusted.Length != filtered.Length) args = args.Where(a => a != "--no-profile").ToArray();
            filtered = adjusted;
        }
        return CommandTable.Find(filtered) is { } cmd ? cmd.Run!(args, filtered) : RunCore(args);
    }

    // `check --team`: refuse bad outputs, rules file or baseline before the team's applications are downloaded.
    internal static string? CheckPreflight(string[] args)
    {
        var team = System.Array.IndexOf(args, "--team");
        if (team + 1 >= args.Length || args[team + 1].StartsWith("--")) return null; // LifeTime.Architecture reports the missing name
        try { CheckInput.Parse(args.Where((_, i) => i != team && i != team + 1).ToList(), teamPreflight: true); return null; }
        catch (CheckInputError e) { return e.Message; }
    }

    // RunCore with stdout and stderr collected (a command of the launcher that needs a model answer, such as `memory doctor`).
    internal static (int Code, string Output) RunCoreCaptured(string[] args)
    {
        var (previousOut, previousError) = (Console.Out, Console.Error);
        var (output, error) = (new StringWriter(), new StringWriter());
        Console.SetOut(output);
        Console.SetError(error);
        var previousRun = TerminalOutput.InRun;
        TerminalOutput.InRun = true; // captured output is machine JSON, never the human view
        try { var code = RunCore(args); return (code, output.ToString() + error); }
        finally { TerminalOutput.InRun = previousRun; Console.SetOut(previousOut); Console.SetError(previousError); }
    }

    internal static int RunCore(string[] args)
    {
        var filtered = args.Where(a => a != "--pretty").ToArray();

        ProjectConfig.ApplyEnvironment(); // the project's Service Studio MCP address, for pull, publish, references and studio
        var ss = Locations.ServiceStudio;
        if (CoreProblem(ss) is { } problem)
        {
            Console.Error.WriteLine(problem);
            return 2;
        }

        var core = LoadCore(ss);
        if (filtered.FirstOrDefault() == "image")
        {
            var imageRun = core.GetType("OmlCli.ImageAssets", throwOnError: true)!.GetMethod("Run")!;
            return (int)imageRun.Invoke(null, new object[] { filtered.Skip(1).Concat(JsonOutput.Pretty ? new[] { "--pretty" } : []).ToArray() })!;
        }
        var cli = core.GetType("OmlCli.Cli", throwOnError: true)!;
        // oml mcp: modules stay open between calls, output is JSON, and a self-test past its limit fails the call, not the server.
        if (McpServer.Active && cli.GetMethod("Serve")!.Invoke(null, null) is string stopped)
        {
            TerminalOutput.PrintObject(new { error = stopped });
            return 2;
        }
        // `oml batch` steps include `spec`, `doc` and `explain-warning`, which live here and need no Service Studio.
        cli.GetField("LauncherRun")!.SetValue(null, (Func<string[], int>)(step => step[0] switch
        {
            "spec" => SpecCommand.Run(step[1..]),
            "doc" => DocCommand.Run(step[1..]),
            "memory" => MemoryCommand.Run(step[1..]),
            _ => DocCommand.Warn(step[1..]),
        }));
        var run = cli.GetMethod("Run")!;
        return (int)run.Invoke(null, new object[] { ss, args })!;
    }

    // Why Core cannot load from this Service Studio folder, or null when it can.
    internal static string? CoreProblem(string ss)
    {
        if (!File.Exists(Path.Combine(ss, "OutSystems.Model.V1.dll"))) return $"Service Studio not found at {ss}. Set OML_SERVICE_STUDIO.";
        if (RequiredRuntimeMajor(ss) is { } major && major > Environment.Version.Major)
            return $"This Service Studio runs on .NET {major}, but the newest .NET runtime here is {Environment.Version.Major}. {RuntimeInstallHint(major)}";
        return null;
    }

    // One load context per process: `oml dashboard` calls Core many times and must not load Service Studio again each time.
    static Assembly? core;
    internal static Assembly? LoadedCore => core;
    internal static Assembly LoadCore(string ss) => core ??=
        new ServiceStudioContext(ss, Path.Combine(AppContext.BaseDirectory, "OmlCli.Core.dll")).LoadFromAssemblyName(new AssemblyName("OmlCli.Core"));

    internal static string RuntimeInstallHint(int major) => $"Install it: winget install Microsoft.DotNet.Runtime.{major}";

    internal static int? RequiredRuntimeMajor(string ss)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ss, "ServiceStudio.runtimeconfig.json")));
            var options = doc.RootElement.GetProperty("runtimeOptions");
            var frameworks = options.TryGetProperty("includedFrameworks", out var inc) ? inc : options.GetProperty("frameworks");
            var core = frameworks.EnumerateArray().First(f => f.GetProperty("name").GetString() == "Microsoft.NETCore.App");
            return Version.Parse(core.GetProperty("version").GetString()!).Major;
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null; // shortcut: unknown layout, so let the load itself report any mismatch
        }
    }
}

class ServiceStudioContext(string ss, string corePath) : AssemblyLoadContext("ServiceStudio")
{
    readonly string[] dirs = { ss, Path.Combine(ss, "Plugins", "ServiceStudio") };

    static readonly Dictionary<string, string> Platform =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Where(p => p.Length > 0)
        .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == "OmlCli.Core") return LoadFromAssemblyPath(corePath);
        var local = dirs.Select(d => Path.Combine(d, name.Name + ".dll")).FirstOrDefault(File.Exists);
        if (local is null) return null;
        // Share the runtime's own copy unless Service Studio ships a newer one (e.g. its .NET 10 System.Text.Json).
        if (Platform.TryGetValue(name.Name!, out var platform)
            && (name.Name == "System.Private.CoreLib" || Version(local) <= Version(platform)))
            return null;
        return LoadFromAssemblyPath(local);
    }

    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var file = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
        var nativeDirs = dirs.Concat(new[] { Path.Combine(ss, "runtimes", "win-" + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), "native") });
        var local = nativeDirs.Select(d => Path.Combine(d, file)).FirstOrDefault(File.Exists);
        return local is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(local);
    }

    static Version Version(string path)
    {
        try { return AssemblyName.GetAssemblyName(path).Version ?? new Version(); }
        catch (BadImageFormatException) { return new Version(); }
    }
}
