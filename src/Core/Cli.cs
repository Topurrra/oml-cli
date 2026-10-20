using OutSystems.Model;

namespace OmlCli;

// Usage text, argument helpers, Emit, error handling and the dispatch table. Each command group's handlers live in
// Commands.Read.cs, Commands.Change.cs, Commands.Studio.cs and Commands.Assets.cs.
public static partial class Cli
{
    const string Usage = """
        oml version
        oml new <out.oml> [--name N] [--kind WebResponsive|Mobile|Service|ReactiveLibrary|...]
        oml new <out.oml> --template <name-or-exported.oml> [--name N]
        oml new <out.oml> --from-template <template.oml> [--name N] [--kind K]
        oml app list <file.oap|file.osp>
        oml app extract <file.oap|file.osp> <outdir>
        oml app pack <out.oap> --name <AppName> <module.oml|extension.xif>...
        oml extension inspect <file.xif>
        oml extension new <out.xif> --name <Name> [--spec <spec.json>]
        oml templates           (local template/sample modules and bundles)
        oml inspect <module.oml> [--own] [--only entities,structures,actions,screens,blocks,references,timers,roles,processes,rest,static,siteProperties]
        oml describe <module.oml> <Element>... [--depth N] [--in <folder|file.oml>]...
        oml find <Name|Pat*> [module.oml] [--in <folder|file.oml>]...  (which module has it: own, referenced, producer files)
        oml census <module.oml>
        oml architecture [--in <folder|file.oml|file.oap>]... [--app <Name>]... [--team <Name>] [--layers <layers.json>] [--out <file.html>] [--json <file.json>]
        oml openapi <module.oml> [--api <RestServiceName>] [--out <file.json|folder>] [--server <url>]...
        oml preview <module.oml> <Block|Screen> [--arg Name=<expression>]... [--theme light|dark] [--width <px>] [--height <px>] [--out <file.html>] [--screenshot <file.png>]
        oml tree <module.oml> <ElementName>   (object tree of one action, screen, entity, ...)
        oml validate <module.oml> [--warnings]
        oml apply <source.oml> <spec.json>... <staged-out.oml> [--describe A,B,...] [--describe-created]
        oml spec [<key>]                   (reference for one spec key; no key lists them. Runs without Service Studio)
        oml spec check <spec.json>        (checks a spec's shape without Service Studio; exit 1 on problems)
        oml spec schema [--out <file.json>] [--force]   (JSON Schema for spec files)
        oml run <steps.json> [--continue]   (several commands in one process, so Service Studio loads once; "-" reads the steps from stdin)
        oml translations export <module.oml> <file> [--format Excel|xlsx|ResX]
        oml install <staged.oml> <target.oml>

        oml studio                          (Service Studio's MCP server: reachable? open modules)
        oml pull [Module] [--out file.oml]  (save the module open in Service Studio to a file; needs 11.55.93+)
        oml publish <Module | file.oml>     (1-Click Publish through Service Studio)
        oml references <Module | file.oml>  (Refresh All references through Service Studio)
        oml lt environments|apps|tag|deploy|status|abort|continue (LifeTime API v2; run 'oml lt' for usage)
        oml font woff2 <input> [output.woff2] [--force]
        oml font batch <folder> [--out folder] [--force]
        oml image <input> [--to png|jpeg|webp|svg] [--max-width N] [--max-height N] [--quality 1-100] [--out file] [--force]
        oml image batch <folder> [same options] [--out folder]

        oml api <TypeName...>   (members of model/plugin types on the installed build)
        oml widgets             (registered widget descriptors)
        oml selftest   (build check; also runs by itself on a Service Studio build not seen before)
        oml doctor [--text] [--selftest]   (checks the setup and says what to fix)
        oml dashboard [--port N] [--no-open]   (local web dashboard for developers; reads only, on 127.0.0.1)
        oml log summary [--file <usage.jsonl>]   (per command: count, failures, median ms, top failures; set OML_LOG=1 to write the log)
        Output is compact JSON on stdout. Global --pretty includes empty fields and indents. Exit 0 ok, 1 model/validation failure, 2 usage/environment.
        """;

    // A handler returns null when the arguments do not fit its command; Run then prints the usage and exits 2.
    delegate int? Handler(List<string> args);

    static readonly Dictionary<string, Handler> Dispatch = new()
    {
        // Commands.Read.cs
        ["version"] = CmdVersion, ["selftest"] = CmdSelfTest, ["api"] = CmdApi, ["widgets"] = CmdWidgets,
        ["find"] = CmdFind, ["inspect"] = CmdInspect, ["describe"] = CmdDescribe, ["tree"] = CmdTree,
        ["census"] = CmdCensus, ["validate"] = CmdValidate, ["openapi"] = CmdOpenApi, ["preview"] = CmdPreview,
        ["architecture"] = CmdArchitecture, ["architecture-render"] = CmdArchitectureRender,
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
        var args = argv.Where(a => a != "--pretty").ToList();
        try
        {
            var command = args.FirstOrDefault();
            if (command is null or "help" || args.Contains("--help") || args.Contains("-h")) { Console.WriteLine(Usage); return 0; }
            if (command is "new" or "inspect" or "census" or "validate" or "apply" or "install" or "preview")
                SelfTest.EnsurePassed();
            if (Dispatch.TryGetValue(command, out var handler) && handler(args) is { } code) return code;
            Console.Error.WriteLine(Usage);
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
        Console.WriteLine(JsonOutput.Serialize(value));
        return 0;
    }

    static string? Opt(List<string> args, string name)
    {
        var i = args.IndexOf(name);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    static IESpace Load(string path) => Model.Load(Path.GetFullPath(path));

    // For commands that only read the module: an `oml run` may keep it open for the next command (see Model.Borrow).
    static Borrowed Open(string path) => Model.Borrow(Path.GetFullPath(path));
}
