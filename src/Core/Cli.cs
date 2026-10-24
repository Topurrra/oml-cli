using OutSystems.Model;

namespace OmlCli;

// Usage text, argument helpers, Emit, error handling and the dispatch table. Each command group's handlers live in
// Commands.Read.cs, Commands.Change.cs, Commands.Studio.cs and Commands.Assets.cs.
public static partial class Cli
{
    const string Usage = CliUsage.Text;

    // A handler returns null when the arguments do not fit its command; Run then prints the usage and exits 2.
    delegate int? Handler(List<string> args);

    static readonly Dictionary<string, Handler> Dispatch = new()
    {
        // Commands.Read.cs
        ["version"] = CmdVersion, ["selftest"] = CmdSelfTest, ["api"] = CmdApi, ["widgets"] = CmdWidgets,
        ["find"] = CmdFind, ["inspect"] = CmdInspect, ["describe"] = CmdDescribe, ["tree"] = CmdTree,
        ["census"] = CmdCensus, ["validate"] = CmdValidate, ["openapi"] = CmdOpenApi, ["preview"] = CmdPreview,
        ["meta"] = CmdMeta, ["expr"] = CmdExpr,
        ["architecture"] = CmdArchitecture, ["architecture-render"] = CmdArchitectureRender, ["check"] = CmdCheck, ["review"] = CmdReview,
        ["test"] = CmdTest,
        // Commands.Change.cs
        ["new"] = CmdNew, ["apply"] = CmdApply, ["install"] = CmdInstall,
        // Commands.Run.cs
        ["run"] = CmdRun,
        // Commands.Studio.cs
        ["studio"] = CmdStudio, ["pull"] = CmdPull, ["publish"] = CmdPublish, ["references"] = CmdReferences,
        // Commands.Assets.cs
        ["app"] = CmdApp, ["extension"] = CmdExtension, ["templates"] = CmdTemplates, ["translations"] = CmdTranslations,
    };

    public static int Run(string serviceStudioDir, string[] argv)
    {
        Model.ServiceStudioDir = serviceStudioDir;
        JsonOutput.Pretty = argv.Contains("--pretty");
        Timings.Start(argv.Contains("--timings"));
        var args = argv.Where(a => a is not ("--pretty" or "--timings")).ToList();
        if (TerminalOutput.ForceJsonFlag(args.ToArray())) { TerminalOutput.ForceJson = true; args = TerminalOutput.StripJson(args.ToArray()).ToList(); }
        try
        {
            var command = args.FirstOrDefault();
            TerminalOutput.Command = command;
            if (command is null or "help" || args.Contains("--help") || args.Contains("-h")) { TerminalOutput.PrintHelp(CliUsage.For(command), command); return 0; }
            if (command is "new" or "inspect" or "census" or "validate" or "apply" or "install" or "preview")
                Timings.Measure("selfTestCheck", SelfTest.EnsurePassed);
            if (!Dispatch.TryGetValue(command, out var handler)) { Console.Error.WriteLine(Usage); return 2; }
            if (handler(args) is { } code) return code;
            // Missing arguments or an unknown option: that command's lines, not the whole usage.
            Console.Error.WriteLine(CliUsage.For(command));
            return 2;
        }
        catch (ModelError e) { Emit(new { error = e.Message }); return 2; }
        catch (CliError e)
        {
            Emit(new { error = e.Message });
            return e.ExitCode;
        }
    }

    // `oml doctor --selftest`: the same self-test as `oml selftest`, returned as JSON and not printed.
    public static string SelfTestReport(string serviceStudioDir)
    {
        Model.ServiceStudioDir = serviceStudioDir;
        return JsonOutput.Serialize(SelfTest.Run().report);
    }

    public static int Emit(object value)
    {
        var text = Timings.Measure("json", () => JsonOutput.Serialize(value));
        // --timings: added to the report object; output of another shape (a list) keeps its shape, and they go to stderr.
        if (Timings.Enabled)
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject report)
            {
                report["timings"] = Timings.Report();
                text = JsonOutput.Write(report);
            }
            else Console.Error.WriteLine(JsonOutput.Write(new System.Text.Json.Nodes.JsonObject { ["timings"] = Timings.Report() }));
        }
        Console.WriteLine(TerminalOutput.Render(text));
        return 0;
    }

    static string? Opt(List<string> args, string name)
    {
        var i = args.IndexOf(name);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    // For commands that only read the module: an `oml run` may keep it open for the next command (see Model.Borrow).
    static Borrowed Open(string path) => Model.Borrow(Path.GetFullPath(path));
}
