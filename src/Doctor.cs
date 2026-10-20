using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace OmlCli;

// `oml doctor`: checks the setup and says what to fix. It lives in the launcher, so it still runs when Service Studio's
// assemblies do not load. Only the model and self-test checks load them, through the same context the normal path uses.
static class Doctor
{
    const string Usage = "oml doctor [--text] [--selftest]";
    const string InitializeRequest = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"oml-doctor","version":"1"}}}""";
    static readonly Version McpWritesFrom = new(11, 55, 91); // publish and Refresh All need this Service Studio build or later

    sealed record Check(string Name, string Status, string Detail, string? Fix = null);

    public static int Run(string[] args)
    {
        var unknown = args.FirstOrDefault(a => a is not ("--text" or "--selftest"));
        if (unknown is not null)
        {
            Console.Error.WriteLine($"Unknown option {unknown}. Usage: {Usage}");
            return 2;
        }
        var selfTest = args.Contains("--selftest");
        var ss = Locations.ServiceStudio;
        var checks = new List<Check>();

        // 1. serviceStudio
        var installed = File.Exists(Path.Combine(ss, "OutSystems.Model.V1.dll"));
        var version = installed ? ServiceStudioVersion(ss) : null;
        checks.Add(installed
            ? new Check("serviceStudio", "ok", $"Service Studio {version ?? "version unknown"} at {ss}")
            : new Check("serviceStudio", "fail", $"Service Studio not found at {ss}.",
                "Install Service Studio 11 or set OML_SERVICE_STUDIO to its folder."));

        // 2. dotnet
        var running = Environment.Version;
        var required = installed ? Program.RequiredRuntimeMajor(ss) : null;
        var dotnet = !installed
            ? new Check("dotnet", "skip", "Service Studio not found, so the .NET version it needs is unknown.")
            : required is null
                ? new Check("dotnet", "skip", $"Running .NET {running}; could not read the .NET version Service Studio needs.")
                : required > running.Major
                    ? new Check("dotnet", "fail", $"This Service Studio runs on .NET {required}, but the newest .NET runtime here is {running.Major}.",
                        Program.RuntimeInstallHint(required.Value))
                    : new Check("dotnet", "ok", $"Running .NET {running}; Service Studio needs .NET {required} or newer.");
        checks.Add(dotnet);

        // 3. model
        ServiceStudioContext? context = null;
        if (installed && dotnet.Status != "fail")
        {
            try
            {
                context = new ServiceStudioContext(ss, Path.Combine(AppContext.BaseDirectory, "OmlCli.Core.dll"));
                context.LoadFromAssemblyName(new AssemblyName("OutSystems.Model.V1"));
                checks.Add(new Check("model", "ok", "Service Studio's model API assemblies load."));
            }
            catch (Exception e)
            {
                context = null;
                checks.Add(new Check("model", "fail", $"Service Studio's model API did not load: {Shorten(e.Message)}",
                    "Reinstall Service Studio 11, or install the .NET runtime it needs."));
            }
        }
        else checks.Add(new Check("model", "skip", "Skipped: needs the Service Studio and .NET checks to pass."));

        // 4. selfTest
        var build = ServiceStudioBuild(ss);
        checks.Add(selfTest ? RunSelfTest(context, ss, build)
            : build is null ? new Check("selfTest", "skip", "Service Studio version unknown, so the self-test record cannot be read.")
            : SelfTestRecorded(build)
                ? new Check("selfTest", "ok", $"Self-test passed on Service Studio {build}.")
                : new Check("selfTest", "warn", $"Self-test has not run on Service Studio {build} yet.",
                    "Run oml doctor --selftest. The first model command runs it anyway."));

        // 5. studioMcp
        checks.Add(StudioMcp(version));

        // 6. lifeTime
        var host = Environment.GetEnvironmentVariable("OML_LIFETIME_HOST");
        var token = Environment.GetEnvironmentVariable("OML_LIFETIME_TOKEN"); // never printed, never measured
        var hasHost = !string.IsNullOrWhiteSpace(host);
        var hasToken = !string.IsNullOrWhiteSpace(token);
        checks.Add(hasHost && hasToken ? new Check("lifeTime", "ok", "LifeTime host and token are set.")
            : !hasHost && !hasToken ? new Check("lifeTime", "skip", "not configured, only needed for oml lt")
            : hasHost ? new Check("lifeTime", "warn", "OML_LIFETIME_HOST is set, but OML_LIFETIME_TOKEN is not.",
                "Set OML_LIFETIME_TOKEN, or clear OML_LIFETIME_HOST if you do not use oml lt.")
            : new Check("lifeTime", "warn", "OML_LIFETIME_TOKEN is set, but OML_LIFETIME_HOST is not.",
                "Set OML_LIFETIME_HOST to the LifeTime hostname without a scheme, or clear OML_LIFETIME_TOKEN."));

        // 7. browser
        var browser = Locations.BrowserPath();
        checks.Add(browser is null
            ? new Check("browser", "warn", "No Edge or Chrome found for oml preview --screenshot.",
                "Install Microsoft Edge or Chrome, or set OML_BROWSER to a browser .exe.")
            : new Check("browser", "ok", $"Browser for oml preview --screenshot: {browser}"));

        // 8. path
        var folder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(p => string.Equals(Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(p)), folder, StringComparison.OrdinalIgnoreCase));
        checks.Add(onPath ? new Check("path", "ok", $"oml is on PATH: {folder}")
            : new Check("path", "warn", $"{folder} is not on PATH, so oml does not run from other folders.",
                $"Add it for this session: $env:PATH = \"{folder};$env:PATH\". To keep it, add the folder to your user PATH in Windows Settings."));

        var ok = checks.All(c => c.Status != "fail");
        if (args.Contains("--text"))
        {
            foreach (var c in checks)
            {
                Console.WriteLine($"[{c.Status}]".PadRight(7) + c.Detail);
                if (c.Fix is not null) Console.WriteLine("       " + c.Fix);
            }
        }
        else
        {
            Console.WriteLine(JsonOutput.Serialize(new
            {
                checks = checks.Select(c => new { name = c.Name, status = c.Status, detail = c.Detail, fix = c.Fix }),
                ok,
            }));
        }
        return ok ? 0 : 1;
    }

    // The version Service Studio shows in its title bar. The model DLL's own file version is a different number (12.x).
    static string? ServiceStudioVersion(string ss)
    {
        var exe = Path.Combine(ss, "ServiceStudio.exe");
        return File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).FileVersion : null;
    }

    // The build id that the self-test record uses: the first word of Version.txt, as `oml version` reports it.
    static string? ServiceStudioBuild(string ss)
    {
        try { return File.ReadAllText(Path.Combine(ss, "Version.txt")).Split(' ', '\r', '\n')[0]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static bool SelfTestRecorded(string build)
    {
        try { return File.Exists(Locations.SelfTestRecord) && File.ReadAllLines(Locations.SelfTestRecord).Contains(build); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static Check RunSelfTest(ServiceStudioContext? context, string ss, string? build)
    {
        var label = build ?? "this build";
        if (context is null) return new Check("selfTest", "skip", "Not run: Service Studio's model API did not load.");
        try
        {
            // Same entry point as `oml selftest`; it returns the report as JSON and prints nothing.
            var core = context.LoadFromAssemblyName(new AssemblyName("OmlCli.Core"));
            var json = (string)core.GetType("OmlCli.Cli", throwOnError: true)!.GetMethod("SelfTestReport")!.Invoke(null, new object[] { ss })!;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("passed").GetBoolean())
                return new Check("selfTest", "ok", $"Self-test passed on Service Studio {label}.");
            var first = doc.RootElement.TryGetProperty("failures", out var failures)
                ? failures.EnumerateArray().Select(f => f.GetString()).FirstOrDefault() : null;
            return new Check("selfTest", "fail", $"Self-test failed on Service Studio {label}: {Shorten(first ?? "no details")}",
                "Run oml selftest for the full report. The model API may have changed in this Service Studio build.");
        }
        catch (Exception e)
        {
            return new Check("selfTest", "fail", $"Self-test could not run: {Shorten((e.InnerException ?? e).Message)}",
                "Run oml selftest for the full report.");
        }
    }

    // Only asks whether the server answers an MCP initialize. It does not create a session token, which would show a dialog.
    static Check StudioMcp(string? version)
    {
        var url = Locations.StudioUrl;
        bool reachable;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(InitializeRequest, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = http.Send(request);
            reachable = HasResult(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        }
        catch (Exception)
        {
            reachable = false; // refused, timed out or not an HTTP reply: the server is not there
        }

        if (!reachable)
            return new Check("studioMcp", "warn", $"Service Studio's MCP server does not answer at {url}.",
                "Optional, needed for publish and references: in Service Studio open Edit > MCP Server and select Start MCP server.");
        if (version is null)
            return new Check("studioMcp", "warn", $"Service Studio's MCP server answers at {url}, but its version is unknown.",
                "Publish and references need Service Studio 11.55.91 or later. Set OML_SERVICE_STUDIO to that folder.");
        if (!Version.TryParse(version, out var parsed) || parsed < McpWritesFrom)
            return new Check("studioMcp", "warn", $"Service Studio {version} answers at {url}, but publish and references need 11.55.91 or later.",
                "Install Service Studio 11.55.91 or later for publish and references.");
        return new Check("studioMcp", "ok", $"Service Studio {version} MCP server answers at {url}.");
    }

    // The reply is JSON, or server-sent events with "data:" lines.
    static bool HasResult(string text)
    {
        var json = text.TrimStart().StartsWith('{') ? text
            : string.Join("", text.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("result", out _);
    }

    static string Shorten(string text) => text.Length <= 200 ? text : text[..200] + "...";
}
