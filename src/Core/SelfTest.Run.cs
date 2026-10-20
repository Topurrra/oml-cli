using System.Text.Json.Nodes;

namespace OmlCli;

static partial class SelfTest
{
    // `oml run`: a module is loaded once while its file is unchanged and again when the file changes;
    // a refused command stops the run; --continue goes on; exit code is the highest step code.
    static void RunCommandTest(string blank, string dir, List<string> failures)
    {
        var path = Path.Combine(dir, "run-cache.oml");
        File.Copy(blank, path);
        Model.ShareModules = true;
        try
        {
            var first = Model.LoadShared(path);
            if (!ReferenceEquals(first, Model.LoadShared(path))) failures.Add("run: an unchanged file was loaded twice");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
            var second = Model.LoadShared(path);
            if (ReferenceEquals(first, second)) failures.Add("run: a file with a new write time was not reloaded");
            var other = Path.Combine(dir, "run-cache-other.oml");
            File.Copy(blank, other);
            if (ReferenceEquals(second, Model.LoadShared(other))) failures.Add("run: two paths shared one module");
        }
        finally { Model.EndSharing(); }

        var steps = Path.Combine(dir, "run-steps.json");
        File.WriteAllText(steps, new JsonObject { ["steps"] = new JsonArray(new JsonArray("validate", path), new JsonArray("install", "a.oml", "b.oml"), new JsonArray("census", path)) }.ToJsonString());
        var (stopCode, stopped) = RunSteps(steps);
        if (stopCode != 2 || stopped?["results"] is not JsonArray { Count: 2 } || stopped["stoppedAfterStep"]?.GetValue<int>() != 2)
            failures.Add("run: a refused step did not stop the run after step 2");
        else if (stopped["results"]![0]!["exitCode"]!.GetValue<int>() != 0 || stopped["results"]![1]!["output"]?["error"] is null)
            failures.Add("run: step results lack the exit code or the refusal message");
        var (goCode, went) = RunSteps(steps, "--continue");
        if (goCode != 2 || went?["results"] is not JsonArray { Count: 3 } || went["results"]![2]!["exitCode"]!.GetValue<int>() != 0)
            failures.Add("run: --continue did not go on after a refused step");
    }

    // Runs `oml run` in this process and returns its exit code and printed JSON.
    static (int code, JsonNode? json) RunSteps(string steps, params string[] extra)
    {
        var (previous, writer) = (Console.Out, new StringWriter());
        Console.SetOut(writer);
        try
        {
            var code = Cli.Run(Model.ServiceStudioDir, new[] { "run", steps }.Concat(extra).ToArray());
            return (code, JsonNode.Parse(writer.ToString()));
        }
        finally { Console.SetOut(previous); }
    }
}
