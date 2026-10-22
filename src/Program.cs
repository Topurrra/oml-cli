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
        JsonOutput.Pretty = args.Contains("--pretty");
        var filtered = args.Where(a => a != "--pretty").ToArray();
        // These commands run in the launcher (or print their own output) and have no phases to time.
        if (filtered.Contains("--timings") && filtered.FirstOrDefault() is { } command && TimingsRefused.Contains(command))
        {
            Console.WriteLine(JsonOutput.Serialize(new { error = TimingsRefusal(command) }));
            return 2;
        }
        if (filtered.FirstOrDefault() == "log" && !filtered.Contains("--help") && !filtered.Contains("-h"))
            return UsageLog.Summary(filtered.Skip(1).ToArray());
        // OML_LOG: one line per command, written after it ends. Without the variable nothing is wrapped or written.
        return UsageLog.Enabled ? UsageLog.Measure(filtered, () => Dispatch(args, filtered)) : Dispatch(args, filtered);
    }

    internal static readonly string[] TimingsRefused = ["lt", "font", "doctor", "spec", "doc", "warn", "dashboard", "setup", "log", "image"];
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

    static int Dispatch(string[] args, string[] filtered)
    {
        // Commands the launcher runs itself print their usage lines here, without Service Studio.
        if (filtered.FirstOrDefault() is "lt" or "font" or "doctor" or "setup" or "image" or "dashboard" && (filtered.Contains("--help") || filtered.Contains("-h")))
        {
            // lt: one line per subcommand, with its options.
            Console.WriteLine(filtered[0] == "lt" ? LifeTime.Usage.Replace(" | ", Environment.NewLine + "oml lt ") : CliUsage.For(filtered[0]));
            return 0;
        }
        if (filtered.FirstOrDefault() == "lt") return LifeTime.Run(filtered.Skip(1).ToArray(), RunCore);
        if (filtered.FirstOrDefault() == "font") return FontAssets.Run(filtered.Skip(1).ToArray());
        if (filtered.FirstOrDefault() == "doctor") return Doctor.Run(filtered.Skip(1).ToArray());
        if (filtered.FirstOrDefault() == "spec") return SpecCommand.Run(filtered.Skip(1).ToArray());
        if (filtered.FirstOrDefault() == "doc") return DocCommand.Run(filtered.Skip(1).ToArray());
        if (filtered.FirstOrDefault() == "warn") return DocCommand.Warn(filtered.Skip(1).ToArray());
        if (filtered.FirstOrDefault() == "dashboard") return Dashboard.Run(filtered.Skip(1).ToArray());
        if (filtered.FirstOrDefault() == "setup" && OperatingSystem.IsWindows()) return Setup.Run(filtered.Skip(1).ToArray());
        if (filtered.FirstOrDefault() is "architecture" or "check" && filtered.Contains("--team"))
        {
            if (filtered[0] == "check" && CheckPreflight(filtered) is { } error)
            {
                Console.WriteLine(JsonOutput.Serialize(new { error }));
                return 2;
            }
            return LifeTime.Architecture(args, RunCore);
        }
        return RunCore(args);
    }

    // `check --team`: refuse bad outputs, rules file or baseline before the team's applications are downloaded.
    internal static string? CheckPreflight(string[] args)
    {
        var team = System.Array.IndexOf(args, "--team");
        if (team + 1 >= args.Length || args[team + 1].StartsWith("--")) return null; // LifeTime.Architecture reports the missing name
        try { CheckInput.Parse(args.Where((_, i) => i != team && i != team + 1).ToList(), teamPreflight: true); return null; }
        catch (CheckInputError e) { return e.Message; }
    }

    static int RunCore(string[] args)
    {
        var filtered = args.Where(a => a != "--pretty").ToArray();

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
        // `oml run` steps include `spec`, `doc` and `warn`, which live here and need no Service Studio.
        cli.GetField("LauncherRun")!.SetValue(null, (Func<string[], int>)(step => step[0] switch
        {
            "spec" => SpecCommand.Run(step[1..]),
            "doc" => DocCommand.Run(step[1..]),
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
