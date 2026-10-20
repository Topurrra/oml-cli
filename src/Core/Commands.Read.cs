namespace OmlCli;

// Commands that read a module (or the installed Service Studio) and change nothing.
public static partial class Cli
{
    static int? CmdVersion(List<string> args)
    {
        var build = Model.InstalledBuild();
        return Emit(new { oml = typeof(Cli).Assembly.GetName().Version?.ToString(3), serviceStudio = build,
            selfTestPassed = SelfTest.Passed(build), runtime = Environment.Version.ToString() });
    }

    static int? CmdSelfTest(List<string> args)
    {
        var (code, report) = SelfTest.Run();
        Emit(report);
        return code;
    }

    static int? CmdApi(List<string> args) => args.Count >= 2 ? Emit(Api.Types(args.Skip(1))) : null;

    static int? CmdWidgets(List<string> args) => Emit(Api.Widgets());

    static int? CmdFind(List<string> args)
    {
        if (args.Count < 2) return null;
        var searchIn = new List<string>();
        string? module = null;
        for (var i = 2; i < args.Count; i++)
            if (args[i] == "--in" && i + 1 < args.Count) searchIn.Add(args[++i]);
            else module = args[i];
        if (module is null && searchIn.Count == 0) throw new CliError("Give a module .oml, --in <folder or .oml>, or both.", 2);
        return Emit(Finder.Find(args[1], module, searchIn));
    }

    static int? CmdInspect(List<string> args)
    {
        if (args.Count < 2) return null;
        string? only = null;
        var own = false;
        for (var i = 2; i < args.Count; i++)
        {
            if (args[i] == "--own") { own = true; continue; }
            if (args[i] == "--only" && i + 1 < args.Count) { only = args[++i]; continue; }
            throw new CliError("inspect accepts --own and --only <sections>.", 2);
        }
        using var open = Open(args[1]); var module = open.Module;
        return Emit(Inspect.Filter(module, own, only));
    }

    static int? CmdDescribe(List<string> args)
    {
        if (args.Count < 3) return null;
        var depth = 0;
        var searchIn = new List<string>();
        var names = new List<string>();
        for (var i = 2; i < args.Count; i++)
        {
            if (args[i] == "--depth" && i + 1 < args.Count && int.TryParse(args[++i], out depth) && depth >= 0) continue;
            if (args[i] == "--in" && i + 1 < args.Count) { searchIn.Add(args[++i]); continue; }
            if (args[i].StartsWith("--")) throw new CliError("describe requires --depth N (non-negative) or --in <folder|file.oml>.", 2);
            names.Add(args[i]);
        }
        if (names.Count == 0) throw new CliError("describe needs at least one element name.", 2);
        using var open = Open(args[1]); var module = open.Module;
        // One name keeps the single-object shape and fails as before; several give an array, a bad name an error entry.
        if (names.Count == 1) return Emit(Describe.Run(module, names[0], depth, searchIn));
        var results = Describe.Many(module, names, depth, searchIn, collectErrors: true);
        Emit(results);
        return results.Any(r => r is Describe.DescribeError) ? 1 : 0;
    }

    static int? CmdTree(List<string> args)
    {
        if (args.Count != 3) return null;
        using var open = Open(args[1]);
        return Emit(Inspect.Tree(open.Module, args[2]));
    }

    static int? CmdCensus(List<string> args)
    {
        if (args.Count != 2) return null;
        using var open = Open(args[1]);
        return Emit(Inspect.Census(open.Module));
    }

    static int? CmdValidate(List<string> args)
    {
        if (args.Count is not (2 or 3)) return null;
        using var open = Open(args[1]); var module = open.Module;
        var errors = Model.Errors(module);
        var warnings = args.Contains("--warnings") ? Model.Warnings(module) : null;
        Emit(new { errorCount = errors.Length, errors = Model.Grouped(errors),
                   warningCount = warnings?.Length, warnings = warnings is null ? null : Model.Grouped(warnings) });
        return errors.Length == 0 ? 0 : 1;
    }

    static int? CmdOpenApi(List<string> args) => args.Count >= 2 ? OpenApi.Run(args) : null;

    static int? CmdPreview(List<string> args) => args.Count >= 3 ? Preview.Run(args) : null;

    static int? CmdArchitecture(List<string> args) => Architecture.Run(args);

    static int? CmdArchitectureRender(List<string> args)
    {
        if (args.Count != 3) return null;
        var output = Path.GetFullPath(args[2]);
        if (File.Exists(output)) throw new CliError($"Output already exists: {output}. Use a new output path, or delete that file first.", 2);
        try
        {
            var html = ArchitecturePage.Render(File.ReadAllText(args[1]));
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false));
            writer.Write(html);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { throw new CliError(e.Message, 2); }
        return Emit(new { output });
    }
}
