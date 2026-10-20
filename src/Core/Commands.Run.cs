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
    // Set by the launcher: the commands that live there and need no Service Studio (`spec`).
    public static Func<string[], int>? LauncherRun;

    static readonly string[] RunAllowed =
        ["apply", "architecture", "census", "describe", "find", "inspect", "openapi", "preview", "spec", "tree", "validate"];

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
                var (code, stdout, stderr, ms) = RunStep(steps[i]);
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
        try { text = source == "-" ? Console.In.ReadToEnd() : File.ReadAllText(source); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new CliError($"Cannot read the steps: {e.Message}", 2); }

        JsonNode? document;
        try { document = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { throw new CliError($"Invalid steps JSON: {e.Message}", 2); }

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
    static readonly string[] DashboardAllowed = ["describe", "find", "image", "inspect", "openapi", "preview", "architecture", "studio", "pull", "version"];

    public static string[] Capture(string serviceStudioDir, string[] step)
    {
        Model.ServiceStudioDir = serviceStudioDir;
        Model.ShareModules = true;
        Model.TrimShared();
        var (code, stdout, stderr, _) = RunStep(step, s =>
            !DashboardAllowed.Contains(s[0]) || (s[0] == "architecture" && s.Contains("--team")) ? Refuse($"'{s[0]}' is not available in the dashboard.")
            : s[0] == "image" ? ImageAssets.Run(s[1..])
            : Run(serviceStudioDir, s));
        return [code.ToString(), stdout, stderr];
    }

    static int RunStepCommand(string[] step)
    {
        var command = step[0];
        if (RunRefused.TryGetValue(command, out var reason))
            return Refuse($"'{command}' is not allowed inside 'oml run': {reason}. Run it as its own command.");
        if (!RunAllowed.Contains(command))
            return Refuse($"'{command}' is not supported inside 'oml run'. Use one of: {string.Join(", ", RunAllowed)}.");
        if (command == "architecture" && step.Contains("--team"))
            return Refuse("'architecture --team' is not allowed inside 'oml run': it reads LifeTime and needs a person's confirmation. Run it as its own command.");
        if (command == "spec")
        {
            if (LauncherRun is null) return Refuse("'spec' is not available here: the launcher did not provide it.");
            JsonOutput.Pretty = step.Contains("--pretty");
            return LauncherRun(step.Skip(1).Where(a => a != "--pretty").ToArray());
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
