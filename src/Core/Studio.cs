using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// Client for the MCP server built into Service Studio 11.55.91+ (Edit > MCP Server > Start MCP server).
// Publishing and refreshing references need the live Service Studio session, so these commands drive it there.
static class Studio
{
    static readonly int Port = Locations.StudioPort;
    static readonly string Url = Locations.StudioUrl;
    // The token is an authorization secret: keep it in the temp folder, never in a project.
    static readonly string TokenFile = Path.Combine(Path.GetTempPath(), "oml-cli", $"studio-token-{Port}");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) }; // a publish can take minutes
    static int nextId;
    static TimeSpan? callTimeout; // set around one best-effort call; null means the 15 minute client timeout

    const string ManualPublish = "Publish it manually instead: open the module in Service Studio and use 1-Click Publish.";
    const string ManualRefresh = "Refresh them manually instead: in Service Studio open Manage Dependencies and use Refresh All.";

    public static object Status() => Guard("Reading Service Studio's open modules", "Check Edit > MCP Server in Service Studio.", () =>
    {
        Connect();
        return new { url = Url, apps = Call("listApps", input: null)["apps"] };
    });

    public static int Publish(string target) => Guard("Publishing through Service Studio", ManualPublish, () =>
    {
        var name = OpenModule(target);
        var result = Call("omlPublish", new JsonObject { ["eSpaceName"] = name });
        var published = Flag(result, "published", "omlPublish");
        Cli.Emit(new { module = name, published, result, manualStep = published ? null : ManualPublish });
        return published ? 0 : 1;
    });

    public static int References(string target) => Guard("Refreshing references through Service Studio", ManualRefresh, () =>
    {
        var name = OpenModule(target);
        var result = Call("omlRefreshReferences", new JsonObject { ["eSpaceName"] = name });
        var hasErrors = Flag(result, "hasErrors", "omlRefreshReferences");
        Cli.Emit(new
        {
            module = name, result, manualStep = hasErrors ? ManualRefresh : null,
            next = "The refreshed module is open in Service Studio; save or publish it there.",
        });
        return hasErrors ? 1 : 0;
    });

    const string ManualPull = "Save it manually instead: in Service Studio, save the module as an .oml file.";

    // Service Studio 11.55.93+ edit sessions: check out a working copy, save it with an empty change, copy the
    // saved file, then end the session. Nothing is merged, so the live module never changes.
    public static int Pull(string? module, string? output)
    {
        // A wrong output path is the user's to fix, not a Service Studio failure: check it before connecting.
        if (output is not null && File.Exists(Path.GetFullPath(output)))
            throw new CliError($"Output already exists: {Path.GetFullPath(output)}. Use a new output path, or delete that file first.", 2);
        return Guard("Pulling the open module from Service Studio", ManualPull, () => PullOpen(module, output));
    }

    static int PullOpen(string? module, string? output)
    {
        Connect();
        var open = (Call("listApps", input: null)["apps"] as JsonArray ?? new())
            .Where(a => a?["isOpen"]?.GetValue<bool>() == true).Select(a => a!["name"]!.GetValue<string>()).ToList();
        var name = module ?? (open.Count == 1 ? open[0]
            : throw new CliError(open.Count == 0 ? "No module is open in Service Studio. Open the module first."
                : $"Several modules are open ({string.Join(", ", open)}). Name the one to pull: oml pull <Module>.", 2));
        if (!open.Contains(name)) throw new CliError($"Module '{name}' is not open in Service Studio. Open it first.", 2);
        var path = Path.GetFullPath(output ?? name + ".oml");
        if (File.Exists(path)) throw new CliError($"Output already exists: {path}. Use a new output path, or delete that file first.", 2);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); // before the session, so a bad path costs nothing

        var session = new JsonObject { ["eSpaceName"] = name };
        try { Call("startOmlEditSession", session); }
        catch (CliError e) when (e.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            // Never end a session another agent started: it would discard that agent's unmerged work.
            throw new CliError($"Another agent already has an edit session on '{name}', and Service Studio allows only one. " +
                               "Wait until it ends, then run oml pull again.", 2);
        }
        try
        {
            var applied = Call("applyModelApiCode", new JsonObject
            {
                ["eSpaceName"] = name, ["code"] = "eSpace => { }", ["imports"] = new JsonArray(),
            });
            var saved = applied["mutatedOmlPath"]?.GetValue<string>();
            if (string.IsNullOrEmpty(saved) || !File.Exists(saved))
                throw new CliError($"applyModelApiCode returned no saved file (got: {Shorten(applied.ToJsonString())}).", 2);
            File.Copy(saved, path);
        }
        finally { Call("endOmlEditSession", new JsonObject { ["eSpaceName"] = name }); }

        string pulled;
        OutSystems.Model.IValidationMessage[] errors;
        object[] grouped = []; // read while the module is open: owner names need it
        int? studioCount = null; int found = 0; string? failure = null;
        using (var m = Model.Load(path))
        {
            pulled = ((OutSystems.Model.Applications.IModule)m).Name;
            errors = Model.Errors(m);
            if (errors.Length > 0)
            {
                // Ask Service Studio what it shows for the open module. A failed call keeps the 1.5.1 behavior.
                bool[]? flags = null;
                try
                {
                    // Best effort: this one call gets 30 s, so a hung Service Studio cannot hold the pull.
                    callTimeout = TimeSpan.FromSeconds(30);
                    var studio = StudioCompare.ParseErrors(Call("getValidationMessages", new JsonObject { ["eSpaceName"] = name, ["filter"] = new JsonArray("error") }));
                    studioCount = studio.Count;
                    flags = StudioCompare.InStudio(errors.Select(e => new StudioCompare.Message(e.Id, e.Message)).ToList(), studio);
                    found = flags.Count(f => f);
                }
                catch (Exception e) { failure = Shorten(e.Message); }
                finally { callTimeout = null; }
                var byMessage = flags is null ? null : errors.Zip(flags).ToDictionary(p => (object)p.First, p => p.Second, ReferenceEqualityComparer.Instance);
                grouped = Model.Grouped(errors, byMessage is null ? null : e => byMessage[e], pulled);
            }
        }
        const string note = "Taken from the module as it is open in Service Studio now. The open module was not changed.";
        if (errors.Length == 0)
        {
            Cli.Emit(new { module = pulled, file = path, bytes = new FileInfo(path).Length, note });
            return 0;
        }
        // Service Studio's MCP has no plain save (Ctrl+S): pull saves an edit-session copy with an empty change, and that
        // copy can show errors the open module does not have. The comparison above tells which ones.
        Cli.Emit(new
        {
            module = pulled, file = path, bytes = new FileInfo(path).Length, note,
            errors = grouped,
            attention = StudioCompare.Attention(pulled, errors.Length, studioCount, found, failure),
        });
        return 1;
    }
    // publish/references use Service Studio's Beta MCP write tools, which may change in a later release. Whatever
    // breaks (tool renamed, result reshaped, non-JSON reply), fail with one clear message and the manual step.
    static T Guard<T>(string what, string manualStep, Func<T> run)
    {
        const string beta = "This uses Service Studio's built-in MCP server, whose write tools are a Beta feature and may change between Service Studio releases.";
        try { return run(); }
        catch (CliError e) when (e.ExitCode == 2)
        {
            throw new CliError($"{what} failed: {e.Message} {manualStep}", 2);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException
                                       or HttpRequestException or TaskCanceledException)
        {
            throw new CliError($"{what} failed: unexpected reply from Service Studio ({e.GetType().Name}: {e.Message}). {beta} {manualStep}", 2);
        }
    }

    static bool Flag(JsonNode result, string field, string tool) =>
        result[field] is JsonValue v && v.TryGetValue<bool>(out var flag) ? flag
            : throw new CliError($"{tool} returned no '{field}' field (got: {Shorten(result.ToJsonString())}); " +
                                 "Service Studio's MCP tool may have changed in this release.", 2);

    static string Shorten(string text) => text.Length <= 300 ? text : text[..300] + "...";

    // A module name must already be open. A file is opened in Service Studio first, unless a module with
    // the same name is already open: then publishing would act on that copy, not on this file.
    static string OpenModule(string target)
    {
        Connect();
        if (!target.EndsWith(".oml", StringComparison.OrdinalIgnoreCase))
            return IsOpen(target) ? target : throw new CliError($"Module '{target}' is not open in Service Studio. Open it, or pass its .oml file.", 2);

        var path = Path.GetFullPath(target);
        if (!File.Exists(path)) throw new CliError($"File not found: {path}", 2);
        string name;
        using (var m = Model.Load(path)) name = ((OutSystems.Model.Applications.IModule)m).Name;
        if (IsOpen(name))
            throw new CliError($"A module named '{name}' is already open in Service Studio, so this file would not be the one used. " +
                               $"Close it there and run again, or run 'oml publish {name}' to use the open copy.", 2);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); // .oml opens in Service Studio
        for (var waited = 0; waited < 180; waited += 2)
        {
            Thread.Sleep(2000);
            if (IsOpen(name)) return name;
        }
        throw new CliError($"Service Studio did not open '{name}' within 3 minutes.", 2);
    }

    static bool IsOpen(string name) =>
        (Call("listApps", input: null)["apps"] as JsonArray ?? new()).Any(a =>
            a?["name"]?.GetValue<string>() == name && a["isOpen"]?.GetValue<bool>() == true);

    static string? token;

    static void Connect()
    {
        if (token is not null) return;
        try
        {
            Rpc("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "oml-cli", ["version"] = "1" },
            });
        }
        catch (HttpRequestException e)
        {
            throw new CliError($"Service Studio's MCP server is not reachable at {Url} ({e.Message}). Open a module in Service Studio, " +
                               "then Edit > MCP Server > Start MCP server. Set OML_STUDIO_PORT for another port.", 2);
        }
        Notify("notifications/initialized");
        token = File.Exists(TokenFile) ? File.ReadAllText(TokenFile).Trim() : null;
        if (string.IsNullOrEmpty(token)) NewToken();
    }

    // Service Studio shows the user an approval dialog naming this client; the call returns once they answer.
    static void NewToken()
    {
        Console.Error.WriteLine("Approve 'oml-cli' in the Service Studio dialog...");
        var result = Rpc("tools/call", new JsonObject
        {
            ["name"] = "createSessionToken",
            ["arguments"] = new JsonObject { ["clientName"] = "oml-cli" },
        });
        if (result["isError"]?.GetValue<bool>() == true)
            throw new CliError("Service Studio did not approve oml-cli: " + Text(result), 2);
        token = Text(result).Trim();
        Directory.CreateDirectory(Path.GetDirectoryName(TokenFile)!);
        File.WriteAllText(TokenFile, token);
    }

    // Every tool but listApps wraps its arguments in "input"; every tool takes the token.
    static JsonNode Call(string tool, JsonObject? input, bool retried = false)
    {
        var args = new JsonObject { ["sessionToken"] = token };
        if (input is not null) args["input"] = input;
        JsonNode result;
        try { result = Rpc("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args }); }
        catch (CliError e) when (!retried && e.Message.Contains("session token", StringComparison.OrdinalIgnoreCase))
        {
            NewToken(); // stale after a Service Studio restart or a revoke; ask once, never loop
            return Call(tool, input, retried: true);
        }
        if (result["isError"]?.GetValue<bool>() == true) throw new CliError($"{tool}: {Shorten(Text(result))}", 2);
        if (result["structuredContent"] is { } structured) return structured;
        var text = Text(result);
        try { return JsonNode.Parse(text) ?? new JsonObject(); }
        catch (JsonException) { throw new CliError($"{tool} returned text instead of JSON: {Shorten(text)}", 2); }
    }

    static string Text(JsonNode result) =>
        string.Concat((result["content"] as JsonArray ?? new()).Select(c => c?["text"]?.GetValue<string>()));

    static JsonNode Rpc(string method, JsonObject parameters)
    {
        var response = Post(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++nextId, ["method"] = method, ["params"] = parameters });
        if (response?["error"] is { } error)
        {
            var tool = parameters["name"]?.GetValue<string>();
            var message = error["message"]?.ToString() ?? error.ToJsonString();
            // -32601/-32602: unknown method or tool; it may have been renamed or removed in this release.
            var unknown = error["code"]?.ToString() is "-32601" or "-32602"
                          && !message.Contains("session token", StringComparison.OrdinalIgnoreCase);
            throw new CliError(tool is not null && unknown
                ? $"Service Studio does not accept the tool '{tool}' ({message}); it may have been renamed or removed in this release."
                : $"{tool ?? method}: {message}", 2);
        }
        return response?["result"] ?? throw new CliError($"{method}: empty response", 2);
    }

    static void Notify(string method) => Post(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });

    // The endpoint wants both content types and answers JSON or SSE ("data: {...}" lines).
    static JsonNode? Post(JsonObject body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var cts = new CancellationTokenSource(callTimeout ?? Timeout.InfiniteTimeSpan);
        using var response = Http.Send(request, cts.Token);
        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var json = text.TrimStart().StartsWith('{') ? text
            : string.Join("", text.Split('\n').Where(l => l.StartsWith("data:")).Select(l => l[5..].Trim()));
        if (!response.IsSuccessStatusCode && !json.TrimStart().StartsWith('{'))
            throw new CliError($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} from {Url}: {Shorten(text)}", 2);
        return json.Length == 0 ? null : JsonNode.Parse(json);
    }
}
