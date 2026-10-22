using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml dashboard`: a local web page for developers who prefer not to use a terminal. It runs the same commands as the CLI
// (DashboardApi.cs) and shows their results. The page itself is plain HTML, CSS and JavaScript embedded from Dashboard/web.
//
// Security: the listener binds 127.0.0.1 only. A random key is in the URL the browser opens, and every API call must send it
// in the X-Oml-Key header. The Host header must be 127.0.0.1:<port> or localhost:<port> (DNS rebinding), a cross-origin
// Origin is refused, and there are no CORS headers. The API has no endpoint that installs, publishes, refreshes references
// or writes to LifeTime.
static partial class Dashboard
{
    const string Usage = "oml dashboard [--port N] [--no-open] [--module <file.oml>]";
    const long MaxBody = 200L * 1024 * 1024; // per request, uploads included
    static string key = "";
    static int port;
    static readonly ManualResetEventSlim Stopping = new();

    public static int Run(string[] args)
    {
        int? requested = null;
        var open = true;
        string? module = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--no-open") open = false;
            else if (args[i] == "--module" && i + 1 < args.Length && module is null)
            {
                module = Path.GetFullPath(args[++i]);
                if (!File.Exists(module) || !Path.GetExtension(module).Equals(".oml", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"--module needs an existing .oml file: {module}");
                    return 2;
                }
            }
            else if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out var p) && p is >= 1024 and <= 65535) requested = p;
            else { Console.Error.WriteLine($"Usage: {Usage} (port 1024-65535)"); return 2; }
        }

        key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        HttpListener listener;
        try { listener = Listen(requested); }
        catch (HttpListenerException e)
        {
            Console.Error.WriteLine($"Cannot listen on 127.0.0.1:{requested}: {e.Message}. Use another --port, or leave it out for a free one.");
            return 2;
        }
        new Thread(Worker) { IsBackground = true, Name = "oml-dashboard-commands" }.Start();
        _ = Serve(listener);

        var url = $"http://127.0.0.1:{port}/?key={key}";
        // The page opens a module from #/explorer?module=<path> (see route() in web/app.js).
        if (module is not null) url += "#/explorer?module=" + Uri.EscapeDataString(module);
        Console.WriteLine(JsonOutput.Serialize(new { url, workFolder = WorkRoot }));
        Console.WriteLine($"oml dashboard is running at {url}");
        Console.WriteLine("Keep this window open while you use it. Press Ctrl+C to stop.");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stopping.Set(); };
        if (open)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            { Console.WriteLine("Could not open the browser. Open the address above yourself."); }
        }
        Stopping.Wait();
        listener.Stop();
        listener.Close();
        Console.WriteLine("oml dashboard stopped.");
        return 0;
    }

    // A free port is found by letting the system pick one, then giving it to HttpListener (http.sys); another program can
    // take it in between, so try a few times.
    static HttpListener Listen(int? requested)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (requested is { } fixedPort) port = fixedPort;
            else
            {
                var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/"); // an IP literal: http.sys binds this address only, never the LAN
            try { listener.Start(); return listener; }
            catch (HttpListenerException) when (requested is null && attempt < 10) { listener.Close(); }
        }
    }

    static async Task Serve(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                if (!listener.IsListening) return;
                continue;
            }
            _ = Task.Run(() => Handle(context));
        }
    }

    static async Task Handle(HttpListenerContext context)
    {
        var (request, response) = (context.Request, context.Response);
        try
        {
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["Cache-Control"] = "no-store";
            if (!HostAllowed(request) || !OriginAllowed(request) || request.HttpMethod is not ("GET" or "POST"))
            {
                await Text(response, 403, "Forbidden.");
                return;
            }
            var path = request.Url!.AbsolutePath;
            if (path.StartsWith("/api/"))
            {
                if (!KeyMatches(request.Headers["X-Oml-Key"]))
                {
                    await Json(response, 403, new JsonObject { ["error"] = "Missing or wrong dashboard key. Open the address that oml dashboard printed." });
                    return;
                }
                var reply = await Api(request, path[5..]);
                await Json(response, reply.Status, reply.Body);
                return;
            }
            if (path.StartsWith("/view/") && request.HttpMethod == "GET") { await View(response, path[6..]); return; }
            if (path.StartsWith("/download/") && request.HttpMethod == "GET")
            {
                // A plain link cannot send a header, so the key comes in the query, as for the page itself.
                if (!KeyMatches(request.QueryString["key"])) { await Text(response, 403, "Missing or wrong dashboard key."); return; }
                await SendDownload(response, path[10..]);
                return;
            }
            if (path == "/" && request.HttpMethod == "GET")
            {
                if (!KeyMatches(request.QueryString["key"]))
                {
                    await Text(response, 403, "This dashboard needs its key. Open the full address that oml dashboard printed in its window.");
                    return;
                }
                await Asset(response, "index.html");
                return;
            }
            if (request.HttpMethod == "GET" && path.Length > 1 && !path[1..].Contains('/')) { await Asset(response, path[1..]); return; }
            await Text(response, 404, "Not found.");
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The browser went away; nothing to answer.
        }
        catch (Exception e)
        {
            try { await Json(response, 500, new JsonObject { ["error"] = "The dashboard hit an unexpected problem: " + e.Message }); }
            catch (Exception again) when (again is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException) { }
        }
        finally
        {
            try { response.Close(); } catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
        }
    }

    // DNS rebinding: a page on another name that resolves to 127.0.0.1 still sends its own name in Host.
    static bool HostAllowed(HttpListenerRequest request) =>
        request.Headers["Host"] is { } host && (host == $"127.0.0.1:{port}" || host == $"localhost:{port}");

    static bool OriginAllowed(HttpListenerRequest request) =>
        request.Headers["Origin"] is not { } origin || origin == $"http://127.0.0.1:{port}" || origin == $"http://localhost:{port}";

    static bool KeyMatches(string? given) =>
        given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key));

    static async Task Text(HttpListenerResponse response, int status, string text)
    {
        response.StatusCode = status;
        response.ContentType = "text/plain; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(text);
        await response.OutputStream.WriteAsync(bytes);
    }

    static async Task Json(HttpListenerResponse response, int status, JsonNode body)
    {
        var text = body.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        // Last line of defence: the LifeTime token never leaves this process, even inside an error message.
        if (Environment.GetEnvironmentVariable("OML_LIFETIME_TOKEN") is { Length: >= 4 } token)
            text = text.Replace(token, "[removed]", StringComparison.Ordinal).Replace(JsonEncodedText.Encode(token).ToString(), "[removed]", StringComparison.Ordinal);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".css"] = "text/css; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".svg"] = "image/svg+xml",
    };

    // The page and its files. A strict policy: scripts and styles from this server only, no inline script, nothing remote.
    static async Task Asset(HttpListenerResponse response, string name)
    {
        using var stream = typeof(Dashboard).Assembly.GetManifestResourceStream("OmlCli.web." + name);
        if (stream is null || !Types.TryGetValue(Path.GetExtension(name), out var type)) { await Text(response, 404, "Not found."); return; }
        response.ContentType = type;
        response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; " +
            "connect-src 'self'; frame-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        response.Headers["X-Frame-Options"] = "DENY";
        await stream.CopyToAsync(response.OutputStream);
    }

    // Generated HTML (previews, architecture pages) is shown in a sandboxed frame from a short-lived, unguessable address.
    // The sandbox gives it no origin, so its scripts cannot call the API even though they run.
    static readonly ConcurrentDictionary<string, (string html, DateTime until)> Views = new();

    internal static string AddView(string html)
    {
        foreach (var old in Views.Where(v => v.Value.until < DateTime.UtcNow).Select(v => v.Key).ToArray()) Views.TryRemove(old, out _);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Views[id] = (html, DateTime.UtcNow.AddMinutes(30));
        return id;
    }

    static async Task View(HttpListenerResponse response, string id)
    {
        if (!Views.TryGetValue(id, out var view) || view.until < DateTime.UtcNow) { await Text(response, 404, "This preview has expired. Render it again."); return; }
        response.ContentType = "text/html; charset=utf-8";
        response.Headers["Content-Security-Policy"] = "sandbox allow-scripts; default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
            "img-src data: blob: https:; font-src data:; frame-ancestors 'self'";
        await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(view.html));
    }

    // Large results (zips, .oap files, reports) are fetched from /download/<id>?key=<key> instead of travelling as base64
    // in the JSON answer. An id is random, works for 10 minutes, and needs the dashboard key.
    // OML_DASHBOARD_DOWNLOAD_SECONDS (1-600) is for tests only: a short life to check that an id expires.
    internal static readonly TimeSpan DownloadLife =
        int.TryParse(Environment.GetEnvironmentVariable("OML_DASHBOARD_DOWNLOAD_SECONDS"), out var s) && s is >= 1 and <= 600 ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(10);
    // Only a file path is kept, never the bytes: a result is written to the work folder first.
    static readonly ConcurrentDictionary<string, (string name, string type, string file, DateTime until)> Downloads = new();

    internal static string AddDownload(string name, string type, string file)
    {
        SweepDownloads();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Downloads[id] = (name, type, file, DateTime.UtcNow + DownloadLife);
        return id;
    }

    static void SweepDownloads()
    {
        foreach (var old in Downloads.Where(d => d.Value.until < DateTime.UtcNow).Select(d => d.Key).ToArray()) Downloads.TryRemove(old, out _);
    }

    static async Task SendDownload(HttpListenerResponse response, string id)
    {
        SweepDownloads();
        if (!Downloads.TryGetValue(id, out var d) || d.until < DateTime.UtcNow) { await Text(response, 404, "This download has expired. Run the action again."); return; }
        if (!File.Exists(d.file)) { await Text(response, 404, "The file is gone: " + d.file); return; }
        // A downloaded HTML report opened from here must not run as this origin.
        response.Headers["Content-Security-Policy"] = "sandbox";
        response.ContentType = d.type;
        var safe = new string(d.name.Select(c => c < 32 || c > 126 || c is '"' or '\\' or ';' ? '_' : c).ToArray());
        response.Headers["Content-Disposition"] = $"attachment; filename=\"{safe}\"; filename*=UTF-8''{Uri.EscapeDataString(d.name)}";
        await using var stream = new FileStream(d.file, FileMode.Open, FileAccess.Read, FileShare.Read);
        await stream.CopyToAsync(response.OutputStream);
    }

    // ---- commands ----

    // Every command runs on this one thread, one at a time: they share Console.Out, and Service Studio's model is not
    // meant for parallel use. Long commands make the next ones wait; the page shows them as running.
    static readonly BlockingCollection<Action> Queue = new();
    static MethodInfo? capture, abandoned;

    static void Worker()
    {
        foreach (var work in Queue.GetConsumingEnumerable()) work();
    }

    internal sealed record Result(int Code, JsonNode? Output, string Stderr, string Cli);

    static readonly string[] LauncherCommands = ["doctor", "spec", "doc", "warn", "font", "lt", "log"];

    // The only way the dashboard runs oml. The arguments are built by DashboardApi.cs, never taken from the page as they are.
    internal static Task<Result> Oml(params string[] args)
    {
        var done = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            // Temporary upload and output folders mean nothing to a person: only their last part is shown.
            var temp = Path.GetTempPath();
            running = Tuple.Create(CliText(args.Select(a => a.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(a) : a)), DateTime.UtcNow);
            try { done.SetResult(Execute(args)); }
            catch (Exception e) { done.SetResult(new Result(2, new JsonObject { ["error"] = (e.InnerException ?? e).Message }, "", CliText(args))); }
            finally { running = null; }
        });
        return done.Task;
    }

    // What the worker runs now, so a page that waits can say what it waits for.
    static volatile Tuple<string, DateTime>? running;

    static Reply QueueState()
    {
        var r = running;
        return new(200, new JsonObject
        {
            ["running"] = r?.Item1,
            ["seconds"] = r is null ? 0 : (int)(DateTime.UtcNow - r.Item2).TotalSeconds,
            ["waiting"] = Queue.Count,
        });
    }

    // Also `oml setup run`, which runs one command the same way outside the dashboard.
    internal static Result Execute(string[] args)
    {
        string stdout, stderr;
        int code;
        // After a self-test past its limit (Core), every command is refused with its message; see Cli.SelfTestAbandoned.
        if (capture is not null && (abandoned ??= capture.DeclaringType!.GetMethod("SelfTestAbandoned")!).Invoke(null, null) is string stopped)
            return new Result(2, new JsonObject { ["error"] = stopped }, "", CliText(args));
        // `architecture --team` and `check --team` download through LifeTime in the launcher first, as on the command line.
        var team = args[0] is "architecture" or "check" && args.Contains("--team");
        if (LauncherCommands.Contains(args[0]) || team)
        {
            var (previousOut, previousError) = (Console.Out, Console.Error);
            var (output, error) = (new StringWriter(), new StringWriter());
            Console.SetOut(output);
            Console.SetError(error);
            try
            {
                JsonOutput.Pretty = false;
                var rest = args[1..];
                code = args[0] switch
                {
                    "doctor" => Doctor.Run(rest),
                    "spec" => SpecCommand.Run(rest),
                    "doc" => DocCommand.Run(rest),
                    "warn" => DocCommand.Warn(rest),
                    "font" => FontAssets.Run(rest),
                    "lt" => LifeTime.Run(rest, CoreRun),
                    "architecture" or "check" when args[0] == "check" && Program.CheckPreflight(args) is { } refused => Refuse(refused),
                    "architecture" or "check" => LifeTime.Architecture(args, CoreRun),
                    _ => UsageLog.Summary(rest),
                };
            }
            finally
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
            }
            (stdout, stderr) = (output.ToString(), error.ToString());
        }
        else
        {
            var ss = Locations.ServiceStudio;
            if (Program.CoreProblem(ss) is { } problem) return new Result(2, new JsonObject { ["error"] = problem }, "", CliText(args));
            capture ??= Program.LoadCore(ss).GetType("OmlCli.Cli", throwOnError: true)!.GetMethod("Capture")!;
            var parts = (string[])capture.Invoke(null, [ss, args])!;
            (code, stdout, stderr) = (int.Parse(parts[0]), parts[1], parts[2]);
        }
        JsonNode? parsed;
        var text = stdout.Trim();
        try { parsed = text.Length == 0 ? null : JsonNode.Parse(text); }
        catch (JsonException) { parsed = JsonValue.Create(text); }
        return new Result(code, parsed, stderr.Trim(), CliText(args));
    }

    static int Refuse(string error)
    {
        Console.WriteLine(JsonOutput.Serialize(new { error }));
        return 2;
    }

    // One Core command from a launcher command (lt download --extract, architecture --team): its output goes to the
    // launcher's captured Console.
    static int CoreRun(string[] args)
    {
        var ss = Locations.ServiceStudio;
        if (Program.CoreProblem(ss) is { } problem) { Console.Error.WriteLine(problem); return 2; }
        capture ??= Program.LoadCore(ss).GetType("OmlCli.Cli", throwOnError: true)!.GetMethod("Capture")!;
        var parts = (string[])capture.Invoke(null, [ss, args])!;
        Console.Out.Write(parts[1]);
        Console.Error.Write(parts[2]);
        return int.Parse(parts[0]);
    }

    // The same command as a person would type it, so the page can teach the CLI.
    internal static string CliText(IEnumerable<string> args) =>
        "oml " + string.Join(" ", args.Select(a => a.Length == 0 || a.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));

    // Outputs that a person keeps (pulled modules, OpenAPI files, architecture pages): one new folder per action, so
    // nothing is ever overwritten.
    internal static string WorkRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oml-cli", "dashboard");

    internal static string NewWorkFolder(string kind)
    {
        var folder = Path.Combine(WorkRoot, kind, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant());
        Directory.CreateDirectory(folder);
        return folder;
    }

    internal static void RequestStop() => Stopping.Set();
}
