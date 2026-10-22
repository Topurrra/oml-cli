using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml run <steps.json>`: several read/change commands in one process. Service Studio's model loads once, and a
// module that a step only reads stays open for the next step (see Model.Borrow). Every step is the argument list
// of a normal `oml` command and prints through its own writer, so its JSON comes back inside the result.
public static partial class Cli
{
    // Set by the launcher: the commands that live there and need no Service Studio (`spec`, `doc`, `warn`). It takes
    // the whole step, command name first.
    public static Func<string[], int>? LauncherRun;
    static readonly string[] LauncherCommands = ["spec", "doc", "warn"];

    static readonly string[] RunAllowed =
        ["apply", "architecture", "census", "check", "describe", "doc", "find", "inspect", "openapi", "preview", "review", "spec", "test", "tree", "validate", "warn"];

    static readonly Dictionary<string, string> RunRefused = new()
    {
        ["install"] = "it replaces a module file and needs a person's confirmation",
        ["publish"] = "it changes the tenant and needs a person's confirmation",
        ["references"] = "it changes the module in Service Studio and needs a person's confirmation",
        ["pull"] = "it needs a person to approve it in Service Studio",
        ["lt"] = "it changes or reads LifeTime and needs a person's confirmation",
        ["run"] = "runs cannot be nested",
    };

    static int? CmdRun(List<string> args)
    {
        var rest = args.Skip(1).ToList();
        var keepGoing = rest.Remove("--continue");
        if (rest.Count != 1) return null;

        var steps = ReadSteps(rest[0]);
        var timings = Timings.Enabled; // `oml run --timings`: each step reports its own phases
        var total = Stopwatch.StartNew();
        var pretty = JsonOutput.Pretty;
        var results = new JsonArray();
        var worst = 0;
        int? stoppedAfter = null;
        Model.ShareModules = true;
        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                Model.TrimShared();
                var (code, stdout, stderr, ms) = RunStep(timings && !LauncherCommands.Contains(steps[i][0]) && !steps[i].Contains("--timings") ? [.. steps[i], "--timings"] : steps[i]);
                JsonOutput.Pretty = pretty;
                var result = new JsonObject
                {
                    ["step"] = i + 1,
                    ["args"] = new JsonArray(steps[i].Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                    ["exitCode"] = code,
                    ["output"] = StepOutput(stdout),
                    ["ms"] = ms,
                };
                if (stderr.Trim().Length > 0) result["stderr"] = stderr.Trim();
                results.Add(result);
                worst = Math.Max(worst, code);
                if (code == 2 && !keepGoing && i < steps.Count - 1) { stoppedAfter = i + 1; break; }
            }
        }
        finally { Model.EndSharing(); }

        var envelope = new JsonObject { ["results"] = results, ["ms"] = total.ElapsedMilliseconds };
        if (stoppedAfter is { } stopped) envelope["stoppedAfterStep"] = stopped;
        Console.WriteLine(JsonOutput.Write(envelope));
        return worst;
    }

    // {"steps": [["inspect","App.oml"], ...]} from a file, or from stdin with "-".
    static List<string[]> ReadSteps(string source)
    {
        string text;
        // A pipe is UTF-8 (a byte order mark wins), not the console code page: Git Bash and pwsh 7 send UTF-8.
        try { text = source != "-" ? File.ReadAllText(source) : Console.IsInputRedirected ? new StreamReader(Console.OpenStandardInput(), Encoding.UTF8).ReadToEnd() : Console.In.ReadToEnd(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new CliError($"Cannot read the steps: {e.Message}", 2); }

        JsonNode? document;
        try { document = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { throw new CliError($"Invalid steps JSON: {e.Message}", 2); }
        if (JsonOutput.UnpairedSurrogatePath(document) is { } bad) throw new CliError($"Invalid steps JSON at {bad}: {JsonOutput.UnpairedSurrogate}", 2);

        if ((document as JsonObject)?["steps"] is not JsonArray array || array.Count == 0)
            throw new CliError("The steps file must be {\"steps\": [[\"inspect\", \"App.oml\"], ...]}: a non-empty list of commands.", 2);
        var steps = new List<string[]>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonArray step && step.Count > 0 && step.All(a => a is JsonValue v && v.TryGetValue<string>(out _)))
                steps.Add(step.Select(a => a!.GetValue<string>()).ToArray());
            else throw new CliError($"Step {i + 1} must be a non-empty list of strings: the arguments of an oml command, such as [\"describe\", \"App.oml\", \"Order\"].", 2);
        }
        return steps;
    }

    // One step with its own stdout and stderr writers. A step cannot take the run down: a crash becomes exit code 2.
    static (int code, string stdout, string stderr, long ms) RunStep(string[] step, Func<string[], int>? command = null)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var (previousOut, previousError) = (Console.Out, Console.Error);
        Console.SetOut(output);
        Console.SetError(error);
        var watch = Stopwatch.StartNew();
        int code;
        try { code = (command ?? RunStepCommand)(step); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Emit(new { error = $"{e.GetType().Name}: {e.Message}" });
            code = 2;
        }
        finally
        {
            watch.Stop();
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
        var (stdout, stderr) = (output.ToString(), error.ToString());
        if (command is null) UsageLog.Record(step, code, watch.ElapsedMilliseconds, Encoding.UTF8.GetByteCount(stdout), stdout, stderr, inRun: true);
        return (code, stdout, stderr, watch.ElapsedMilliseconds);
    }

    // `oml dashboard`: one read command with its own output, called from the launcher's single command thread.
    // Modules it only reads stay open for the next call, as in a run. Not logged: these are a person's clicks, not agent turns.
    // The launcher allows only read commands; this refusal is the second lock on the same door.
    // `check` and `architecture` with --team arrive here already rewritten by the launcher (no --team left).
    static readonly string[] DashboardAllowed = ["describe", "find", "image", "inspect", "openapi", "preview", "architecture", "check", "studio", "pull", "version",
        "review", "tree", "validate", "test scaffold", "app list", "app extract", "extension inspect", "templates", "translations export"];
    // A command with subcommands is allowed per subcommand: `app list` yes, `app pack` no; `test scaffold` only.
    static bool DashboardAllows(string[] s) => s[0] is "app" or "extension" or "translations" or "test"
        ? s.Length > 1 && DashboardAllowed.Contains(s[0] + " " + s[1])
        : DashboardAllowed.Contains(s[0]);

    public static string[] Capture(string serviceStudioDir, string[] step)
    {
        // A self-test past its limit may still hold the model: refuse before anything touches it.
        if (SelfTest.Abandoned is { } stopped) return ["2", JsonOutput.Serialize(new { error = stopped }), ""];
        Model.ServiceStudioDir = serviceStudioDir;
        Model.ShareModules = true;
        Model.TrimShared();
        SelfTest.KeepProcess = true;
        var (code, stdout, stderr, _) = RunStep(step, s =>
            !DashboardAllows(s) || (s[0] is "architecture" or "check" && s.Contains("--team")) || (s[0] == "preview" && s.Contains("--serve")) ? Refuse($"'{s[0]}' is not available in the dashboard.")
            : s[0] == "image" ? ImageAssets.Run(s[1..])
            : Run(serviceStudioDir, s));
        return [code.ToString(), stdout, stderr];
    }

    // `oml dashboard`: the launcher refuses its own commands too once this is set, because the late self-test
    // thread may still write to Console.Out while a launcher command has it captured.
    public static string? SelfTestAbandoned() => SelfTest.Abandoned;

    static int RunStepCommand(string[] step)
    {
        var command = step[0];
        if (RunRefused.TryGetValue(command, out var reason))
            return Refuse($"'{command}' is not allowed inside 'oml run': {reason}. Run it as its own command.");
        if (!RunAllowed.Contains(command))
            return Refuse($"'{command}' is not supported inside 'oml run'. Use one of: {string.Join(", ", RunAllowed)}.");
        if (command == "preview" && step.Contains("--serve"))
            return Refuse("'preview --serve' is not allowed inside 'oml run': it keeps running until Ctrl+C. Run it as its own command.");
        if (command is "architecture" or "check" && step.Contains("--team"))
            return Refuse($"'{command} --team' is not allowed inside 'oml run': it reads LifeTime and needs a person's confirmation. Run it as its own command.");
        if (LauncherCommands.Contains(command))
        {
            if (step.Contains("--timings")) return Refuse($"--timings is not available for {command}.");
            if (LauncherRun is null) return Refuse($"'{command}' is not available here: the launcher did not provide it.");
            JsonOutput.Pretty = step.Contains("--pretty");
            return LauncherRun(step.Where(a => a != "--pretty").ToArray());
        }
        return Run(Model.ServiceStudioDir, step);
    }

    static int Refuse(string message)
    {
        Emit(new { error = message });
        return 2;
    }

    // The command's own JSON where it printed JSON, else its text; null when it printed nothing.
    static JsonNode? StepOutput(string stdout)
    {
        var text = stdout.Trim();
        if (text.Length == 0) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return JsonValue.Create(text); }
    }
}
