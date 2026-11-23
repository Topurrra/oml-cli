using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml mcp`: an MCP server on stdin and stdout (JSON-RPC 2.0, one message per line) with three tools, read, write and
// gated. Each takes {"args": [...]}: an oml command line. Commands run in this process, so Service Studio's model and
// the self-test load once, and a module a command only reads stays open for the next call (Model.Borrow, keyed by
// full path, write time and length). Which tool runs which command is CommandTable.McpTools.
//
// Threads: the reader answers initialize, ping, tools/list and refusals at once. Commands in this process run one at
// a time on one worker thread, because they share Console.Out, the current Service Studio model, Timings and other
// process-wide state. Commands in a child oml process (InChild) run at the same time as everything else. Every request
// with an id gets exactly one answer. Only JSON-RPC messages go to stdout: a command's output is captured per call, and
// anything written outside a call goes to stderr.
internal static class McpServer
{
    // Set while the server runs: Program.RunCore then asks Core to keep modules open and the process alive (Cli.Serve).
    public static bool Active;

    static readonly string[] Versions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    static readonly string[] Tools = ["read", "write", "gated"];
    // With JsonOutput.Safe: pure ASCII as oml prints it (\uXXXX above 0x7F), quotes and angle brackets not escaped.
    static readonly JsonSerializerOptions Wire = new() { MaxDepth = 1024, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonDocumentOptions Deep = new() { MaxDepth = 1024 };
    static readonly BlockingCollection<Action> Queue = new();
    static readonly object WriteLock = new();
    static TextWriter protocol = TextWriter.Null;
    static int pending;

    public static int Run(string[] rest)
    {
        if (rest.Length > 0)
        {
            TerminalOutput.PrintObject(new { error = "oml mcp takes no arguments.", next = "Start it as an MCP server over stdio: oml mcp" });
            return 2;
        }
        Active = true;
        TerminalOutput.InRun = true;   // every captured output is machine JSON
        TerminalOutput.ForceJson = true;
        protocol = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        Console.SetOut(Console.Error);   // output outside a call (a late thread) must never reach the protocol stream
        Console.SetIn(TextReader.Null);   // a command that reads stdin gets nothing instead of protocol messages
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        new Thread(() => { foreach (var work in Queue.GetConsumingEnumerable()) work(); }, 16 * 1024 * 1024) { IsBackground = true, Name = "oml mcp" }.Start();

        while (input.ReadLine() is { } line)
            if (line.Trim().Length > 0) Handle(line);

        // stdin closed: answer what was asked (at most 5 minutes more), then end.
        Queue.CompleteAdding();
        var wait = Stopwatch.StartNew();
        while (Volatile.Read(ref pending) > 0 && wait.Elapsed < TimeSpan.FromMinutes(5)) Thread.Sleep(50);
        return 0;
    }

    static void Handle(string line)
    {
        JsonNode? message;
        try { message = JsonNode.Parse(line, null, Deep); }
        catch (JsonException e) { Send(null, error: Error(-32700, $"Parse error: {e.Message}")); return; }
        if (message is not JsonObject request) { Send(null, error: Error(-32600, "Invalid request: send one JSON-RPC object per line.")); return; }
        // A notification (no id) and a response to a request of ours (no method) need no answer.
        if (!request.TryGetPropertyValue("id", out var idNode) || idNode is null || request["method"] is not JsonValue methodNode) return;
        var id = idNode.DeepClone();
        var method = methodNode.TryGetValue<string>(out var m) ? m : "";
        var parameters = request["params"] as JsonObject;
        switch (method)
        {
            case "initialize":
                var asked = (parameters?["protocolVersion"] as JsonValue)?.TryGetValue<string>(out var v) == true ? v : null;
                Send(id, new JsonObject
                {
                    ["protocolVersion"] = Versions.Contains(asked) ? asked : Versions[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "oml", ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) },
                    ["instructions"] = "Each tool runs one oml command line: {\"args\": [\"inspect\", \"App.oml\", \"--own\"]}. read only reads, write writes new files, gated needs a person's approval. Paths are relative to the folder oml mcp started in.",
                });
                break;
            case "ping":
                Send(id, new JsonObject());
                break;
            case "tools/list":
                Send(id, new JsonObject { ["tools"] = new JsonArray(Tools.Select(t => (JsonNode)ToolEntry(t)).ToArray()) });
                break;
            case "tools/call":
                Call(id, parameters);
                break;
            default:
                Send(id, error: Error(-32601, $"Method not found: {method}"));
                break;
        }
    }

    static JsonObject ToolEntry(string tool) => new()
    {
        ["name"] = tool,
        ["description"] = Describe(tool),
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["args"] = new JsonObject
                {
                    ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["minItems"] = 1,
                    ["description"] = "The oml command line as a list, for example [\"inspect\", \"App.oml\", \"--own\"].",
                },
            },
            ["required"] = new JsonArray("args"),
        },
    };

    // The commands come from the command table; the exceptions are the rules of CommandTable.McpTools.
    static string Describe(string tool)
    {
        var commands = string.Join(", ", CommandTable.McpCommands(tool));
        var result = " Returns {exit, output, stderr}: output is the command's JSON.";
        return tool switch
        {
            "read" => $"Run an oml command that only reads. Commands: {commands}, help. Not: memory add/retract/dream/init, map build, team-rules add/remove, "
                      + "review --server, check or architecture --team, test data allow/create/update/cleanup. A batch here may only have read steps." + result,
            "write" => $"Run an oml command that writes new files: {commands}, memory add/retract/dream/init, map build, team-rules add/remove. "
                       + "A batch goes here when a step writes (apply). apply --user-approved is gated. browser runs in a child process." + result,
            _ => $"Run a command that needs a person's approval first: {commands}, apply --user-approved, review --server, check or architecture --team, "
                 + "test data allow/create/update/cleanup. Runs in a child oml process." + result,
        };
    }

    static void Call(JsonNode id, JsonObject? parameters)
    {
        var tool = (parameters?["name"] as JsonValue)?.TryGetValue<string>(out var n) == true ? n : null;
        if (tool is null || !Tools.Contains(tool)) { Send(id, error: Error(-32602, $"Unknown tool: {tool ?? "(none)"}. The tools are read, write and gated.")); return; }
        if ((parameters!["arguments"] as JsonObject)?["args"] is not JsonArray list || list.Count == 0
            || !list.All(a => a is JsonValue value && value.TryGetValue<string>(out _)))
        {
            Refuse(id, "args must be a non-empty list of strings: the oml command line, for example [\"inspect\", \"App.oml\", \"--own\"].");
            return;
        }
        var args = CliUsage.Normalize(list.Select(a => a!.GetValue<string>()).ToArray());
        string? temp = null;
        try
        {
            var refusal = Check(tool, args, out var useTool);
            if (refusal is null && args[0] == "batch") (refusal, useTool, args, temp) = CheckBatch(tool, args);
            if (refusal is not null) { Refuse(id, refusal, useTool); Delete(temp); return; }
        }
        catch (Exception e) { Refuse(id, e.Message); Delete(temp); return; }

        Interlocked.Increment(ref pending);
        void Answer(JsonObject envelope)
        {
            try { Send(id, Result(envelope, false)); }
            finally { Delete(temp); Interlocked.Decrement(ref pending); }
        }
        if (InChild(tool, args)) Task.Run(() => Answer(Guarded(() => Child(args))));
        else
        {
            try { Queue.Add(() => Answer(Guarded(() => InProcess(args)))); }
            catch (InvalidOperationException) { Answer(Envelope(2, JsonOutput.Serialize(new { error = "oml mcp is stopping." }), "")); }
        }
    }

    // Whatever goes wrong around a command, the call still gets its one answer.
    static JsonObject Guarded(Func<JsonObject> run)
    {
        try { return run(); }
        catch (Exception e) { return Envelope(2, JsonOutput.Serialize(new { error = $"oml mcp could not run the command: {e.Message}" }), ""); }
    }

    // Null when this tool may run the command line, else why not (and the tool that runs it).
    static string? Check(string tool, string[] args, out string? useTool)
    {
        useTool = null;
        if (CliUsage.IsHelp(args)) return tool == "read" ? null : Wrong("help", useTool = "read", tool);
        var tools = CommandTable.McpTools(args);
        if (tools.Length == 0)
            return $"'{args[0]}' is not a command of oml mcp. Read commands: {string.Join(", ", CommandTable.McpCommands("read"))}; ['help', '--all'] lists every usage line.";
        if (tools.Contains(tool)) return null;
        useTool = tools[0];
        return Wrong(Name(args), useTool, tool);
    }

    // The command, its subcommand and the option that decides the tool, without paths: "memory add", "review --server".
    static string Name(string[] args) =>
        string.Join(' ', new[] { args[0], args.Length > 1 && args[1].Length > 0 && args[1].All(c => c is >= 'a' and <= 'z' or '-') && !args[1].StartsWith('-') ? args[1] : null }
            .Concat(args.Where(a => a is "--server" or "--team")).Where(a => a is not null));

    static string Wrong(string what, string right, string tool) =>
        $"'{what}' runs with the {right} tool, not {tool}{(right == "gated" ? ": it needs a person's approval" : right == "write" ? ": it writes files" : "")}. Call {right} with the same args.";

    // `batch <steps.json>`: every step must be a command of this tool, so a write or gated command never runs through
    // read. The checked steps go to a temporary copy, which the batch then runs, so the file cannot change in between.
    // Paths inside steps are relative to the current folder, not to the steps file, so the copy runs the same.
    static (string? refusal, string? useTool, string[] args, string? temp) CheckBatch(string tool, string[] args)
    {
        var sources = args.Skip(1).Where(a => !a.StartsWith("--")).ToList();
        if (sources.Count != 1) return (null, null, args, null);   // oml batch prints its own usage
        if (sources[0] == "-") return ("oml mcp cannot pass steps on stdin: write them to a file and run ['batch', '<steps.json>'].", null, args, null);
        string text;
        try { text = File.ReadAllText(sources[0]); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return ($"Cannot read the steps: {e.Message}", null, args, null); }
        JsonArray? steps;
        try { steps = (JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject)?["steps"] as JsonArray; }
        catch (JsonException e) { return ($"Invalid steps JSON: {e.Message}", null, args, null); }
        if (steps is null || steps.Count == 0) return ("The steps file must be {\"steps\": [[\"inspect\", \"App.oml\"], ...]}: a non-empty list of commands.", null, args, null);
        var needs = "read";
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i] is not JsonArray step || step.Count == 0 || !step.All(a => a is JsonValue value && value.TryGetValue<string>(out _)))
                return ($"Step {i + 1} must be a non-empty list of strings: the arguments of an oml command.", null, args, null);
            var stepArgs = CliUsage.Normalize(step.Select(a => a!.GetValue<string>()).ToArray());
            var tools = CommandTable.McpTools(stepArgs);
            if (tools.Contains("read")) continue;
            if (tools.Contains("write")) { needs = "write"; continue; }
            return ($"Step {i + 1} '{Name(stepArgs)}' cannot run in a batch: {(tools.Contains("gated") ? "it needs a person's approval. Run it on its own with the gated tool" : "it is not a command of oml mcp")}.",
                tools.Contains("gated") ? "gated" : null, args, null);
        }
        if (needs != tool) return ($"This batch runs with the {needs} tool, not {tool}: {(needs == "write" ? "a step writes files" : "every step only reads")}. Call {needs} with the same args.", needs, args, null);
        var temp = Path.Combine(Path.GetTempPath(), "oml-mcp-steps-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        var index = Array.IndexOf(args, sources[0], 1);
        var copy = (string[])args.Clone();
        copy[index] = temp;
        return (null, null, copy, temp);
    }

    // In a child oml process: every gated command (it may wait minutes for a person, Service Studio or the network, and
    // server and review --server get the person's OML_SERVER_* variables for that call only); browser (its session lives
    // on between commands, and a handoff waits for Enter on stdin); doctor (doctor --selftest runs the self-test without
    // the time limit of this server, and doctor checks the setup of a fresh process).
    static bool InChild(string tool, string[] args) => tool == "gated" || args[0] is "browser" or "doctor";

    static JsonObject InProcess(string[] args)
    {
        var watch = Stopwatch.StartNew();
        var (output, error) = (new StringWriter(), new StringWriter());
        var (previousOut, previousError) = (Console.Out, Console.Error);
        var filtered = TerminalOutput.StripJson(args.Where(a => a != "--pretty").ToArray());
        var code = 2;
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            // After a self-test past its limit, the late self-test may still hold the model and write to Console.Out.
            if (Program.LoadedCore?.GetType("OmlCli.Cli")?.GetMethod("SelfTestAbandoned")?.Invoke(null, null) is string stopped)
                TerminalOutput.PrintObject(new { error = stopped });
            else if (filtered.Contains("--timings") && Program.TimingsRefused.Contains(filtered[0]))
                TerminalOutput.PrintObject(new { error = Program.TimingsRefusal(filtered[0]) });
            else
            {
                JsonOutput.Pretty = args.Contains("--pretty");
                code = Program.Dispatch(args, filtered);
            }
        }
        catch (Exception e)
        {
            while (e is TargetInvocationException { InnerException: { } inner }) e = inner;
            Console.Out.WriteLine(JsonOutput.Serialize(new { error = e.Message }));
            code = 2;
        }
        finally
        {
            JsonOutput.Pretty = false;
            TerminalOutput.InRun = true;
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
        var (stdout, stderr) = (output.ToString(), error.ToString());
        UsageLog.Record(filtered, code, watch.ElapsedMilliseconds, Encoding.UTF8.GetByteCount(stdout), stdout, stderr);
        return Envelope(code, stdout, stderr);
    }

    static JsonObject Child(string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) info.ArgumentList.Add(a);
            info.Environment["OML_OUTPUT"] = "json";
            if (args[0] == "server" || args[0] == "review" && args.Contains("--server")) AddServerVariables(info.Environment);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start oml.");
            process.StandardInput.Close();   // nothing to read: a command that waits for stdin ends at once
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return Envelope(process.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return Envelope(2, JsonOutput.Serialize(new { error = $"Could not run oml in a child process: {e.Message}" }), "");
        }
    }

    // A client may start this server with a small environment. The person's Windows user variables (HKCU\Environment)
    // fill the OML_SERVER_* names that are missing, for this child only. The values are never logged or printed.
    static void AddServerVariables(IDictionary<string, string?> environment)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment");
            foreach (var name in new[] { "OML_SERVER_HOST", "OML_SERVER_USER", "OML_SERVER_PASSWORD" })
                if (string.IsNullOrEmpty(environment.TryGetValue(name, out var set) ? set : null) && key?.GetValue(name) is string value && value.Length > 0)
                    environment[name] = value;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    static JsonObject Envelope(int code, string stdout, string stderr)
    {
        var text = stdout.Trim();
        JsonNode? output;
        try { output = text.Length == 0 ? JsonValue.Create("") : JsonNode.Parse(text, null, Deep); }
        catch (JsonException) { output = JsonValue.Create(text); }
        var err = stderr.Trim();
        return new JsonObject { ["exit"] = code, ["output"] = output, ["stderr"] = err.Length > 2000 ? err[^2000..] : err };
    }

    static JsonObject Result(JsonObject envelope, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = JsonOutput.Safe(envelope.ToJsonString(Wire), false) }),
        ["isError"] = isError,
    };

    // A refused call: oml did not run. Same shape as a result, with isError true.
    static void Refuse(JsonNode id, string message, string? useTool = null)
    {
        var output = new JsonObject { ["error"] = message };
        if (useTool is not null) output["useTool"] = useTool;
        Send(id, Result(new JsonObject { ["exit"] = 2, ["output"] = output, ["stderr"] = "" }, true));
    }

    static JsonObject Error(int code, string message) => new() { ["code"] = code, ["message"] = message };

    static void Send(JsonNode? id, JsonObject? result = null, JsonObject? error = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        if (error is not null) message["error"] = error;
        else message["result"] = result;
        var text = JsonOutput.Safe(message.ToJsonString(Wire), false);
        lock (WriteLock)
        {
            try { protocol.Write(text + "\n"); }
            catch (IOException) { }   // the client is gone; the reader sees the end of stdin next
        }
    }

    static void Delete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
