using System.Security.Cryptography;
using OutSystems.Model;
using OutSystems.Model.Enumerations;
using OutSystems.Model.UI;

namespace OmlCli;

public static class Cli
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
        oml describe <module.oml> <Element> [--depth N] [--in <folder|file.oml>]...
        oml find <Name|Pat*> [module.oml] [--in <folder|file.oml>]...  (which module has it: own, referenced, producer files)
        oml census <module.oml>
        oml architecture [--in <folder|file.oml|file.oap>]... [--app <Name>]... [--team <Name>] [--layers <layers.json>] [--out <file.html>] [--json <file.json>]
        oml openapi <module.oml> [--api <RestServiceName>] [--out <file.json|folder>] [--server <url>]...
        oml tree <module.oml> <ElementName>   (object tree of one action, screen, entity, ...)
        oml validate <module.oml> [--warnings]
        oml apply <source.oml> <spec.json> <staged-out.oml>
        oml translations export <module.oml> <file> [--format Excel|xlsx|ResX]
        oml install <staged.oml> <target.oml>

        oml studio                          (Service Studio's MCP server: reachable? open modules)
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
        Output is compact JSON on stdout. Global --pretty includes empty fields and indents. Exit 0 ok, 1 model/validation failure, 2 usage/environment.
        """;

    public static int Run(string serviceStudioDir, string[] argv)
    {
        Model.ServiceStudioDir = serviceStudioDir;
        JsonOutput.Pretty = argv.Contains("--pretty");
        var args = argv.Where(a => a != "--pretty").ToList();
        try
        {
            var command = args.FirstOrDefault();
            if (command is null or "help" || args.Contains("--help") || args.Contains("-h")) { Console.WriteLine(Usage); return 0; }
            if (command is "new" or "inspect" or "census" or "validate" or "apply" or "install")
                SelfTest.EnsurePassed();
            switch (command)
            {
                case "architecture-render" when args.Count == 3:
                {
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
                case "version":
                    var build = Model.InstalledBuild();
                    return Emit(new { oml = typeof(Cli).Assembly.GetName().Version?.ToString(3), serviceStudio = build,
                        selfTestPassed = SelfTest.Passed(build), runtime = Environment.Version.ToString() });
                case "api" when args.Count >= 2: return Emit(Api.Types(args.Skip(1)));
                case "widgets": return Emit(Api.Widgets());
                case "find" when args.Count >= 2:
                {
                    var searchIn = new List<string>();
                    string? module = null;
                    for (var i = 2; i < args.Count; i++)
                        if (args[i] == "--in" && i + 1 < args.Count) searchIn.Add(args[++i]);
                        else module = args[i];
                    if (module is null && searchIn.Count == 0) throw new CliError("Give a module .oml, --in <folder or .oml>, or both.", 2);
                    return Emit(Finder.Find(args[1], module, searchIn));
                }
                case "studio": return Emit(Studio.Status());
                case "publish" when args.Count == 2: return Studio.Publish(args[1]);
                case "references" when args.Count == 2: return Studio.References(args[1]);
                case "selftest":
                    var (code, report) = SelfTest.Run();
                    Emit(report);
                    return code;
                case "app": return Packages.Run(args);
                case "architecture": return Architecture.Run(args);
                case "extension": return Extensions.Run(args);
                case "templates": return Emit(Templates.List());
                case "openapi" when args.Count >= 2: return OpenApi.Run(args);
                case "new" when args.Count >= 2:
                    if (args.Contains("--from-template")) {
                        if (Opt(args, "--from-template") is not { } from || from.StartsWith("--") || args.Contains("--template"))
                            throw new CliError("--from-template requires a .oml path and cannot be combined with --template", 2);
                        return TemplateCreation.Run(args[1], from, Opt(args, "--name"), Opt(args, "--kind"));
                    }
                    if (args.Contains("--template") && Opt(args, "--template") is null)
                        throw new CliError("--template requires a template name or exported .oml path.", 2);
                    return Opt(args, "--template") is { } template ? Templates.New(args[1], template, Opt(args, "--name"))
                        : New(args[1], Opt(args, "--name"), Opt(args, "--kind"));
                case "inspect" when args.Count >= 2:
                {
                    string? only = null;
                    var own = false;
                    for (var i = 2; i < args.Count; i++)
                    {
                        if (args[i] == "--own") { own = true; continue; }
                        if (args[i] == "--only" && i + 1 < args.Count) { only = args[++i]; continue; }
                        throw new CliError("inspect accepts --own and --only <sections>.", 2);
                    }
                    using var module = Load(args[1]);
                    return Emit(Inspect.Filter(module, own, only));
                }
                case "describe" when args.Count >= 3:
                {
                    var depth = 0;
                    var searchIn = new List<string>();
                    for (var i = 3; i < args.Count; i++)
                    {
                        if (args[i] == "--depth" && i + 1 < args.Count && int.TryParse(args[++i], out depth) && depth >= 0) continue;
                        if (args[i] == "--in" && i + 1 < args.Count) { searchIn.Add(args[++i]); continue; }
                        throw new CliError("describe requires --depth N (non-negative) or --in <folder|file.oml>.", 2);
                    }
                    using var module = Load(args[1]);
                    return Emit(Describe.Run(module, args[2], depth, searchIn));
                }
                case "tree" when args.Count == 3: return Emit(Inspect.Tree(Load(args[1]), args[2]));
                case "census" when args.Count == 2: return Emit(Inspect.Census(Load(args[1])));
                case "validate" when args.Count is 2 or 3:
                {
                    var module = Load(args[1]);
                    var errors = Model.Errors(module);
                    var warnings = args.Contains("--warnings") ? Model.Warnings(module) : null;
                    Emit(new { errorCount = errors.Length, errors = Model.Grouped(errors),
                               warningCount = warnings?.Length, warnings = warnings is null ? null : Model.Grouped(warnings) });
                    return errors.Length == 0 ? 0 : 1;
                }
                case "apply" when args.Count == 4: return Apply.Run(args[1], args[2], args[3]);
                case "translations" when args.Count is 4 or 6 && args[1] == "export":
                    return Content.Export(args[2], args[3], Opt(args, "--format"));
                case "install" when args.Count == 3: return Install(args[1], args[2]);
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (ModelError e) { Emit(new { error = e.Message }); return 2; }
        catch (CliError e)
        {
            Emit(new { error = e.Message });
            return e.ExitCode;
        }
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

    static int New(string outPath, string? name, string? kind)
    {
        outPath = Path.GetFullPath(outPath);
        if (File.Exists(outPath)) throw new CliError($"Output already exists: {outPath}. Use a new output path, or delete that file first.", 2);
        var services = Model.Services();
        var segmentation = Enum.Parse<SegmentationKind>(kind ?? "WebResponsive", ignoreCase: true);
        name ??= Path.GetFileNameWithoutExtension(outPath);
        using var module = services.CreateESpace(segmentation, name, "", false, services.NewKey());
        Model.Save(module, outPath);
        return Emit(new { created = outPath, name, kind = segmentation.ToString() });
    }

    // Backup, copy, reopen-verify, restore on failure (skill section 16).
    static int Install(string staged, string target)
    {
        staged = Path.GetFullPath(staged);
        target = Path.GetFullPath(target);
        if (!File.Exists(staged) || !File.Exists(target)) throw new CliError("Both staged and target must exist.", 2);
        if (string.Equals(staged, target, StringComparison.OrdinalIgnoreCase)) throw new CliError("Staged and target are the same file.", 2);

        var stagedErrors = Model.Errors(Load(staged));
        if (stagedErrors.Length > 0)
            throw new CliError($"Staged module has {stagedErrors.Length} validation errors; refusing to install.");

        var backup = $"{target}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        if (File.Exists(backup)) throw new CliError($"Backup already exists: {backup}", 2);
        var targetHash = Hash(target);
        File.Copy(target, backup);
        if (Hash(backup) != targetHash) throw new CliError("Backup hash mismatch; nothing installed.", 2);

        File.Copy(staged, target, overwrite: true);
        try
        {
            if (Hash(target) != Hash(staged)) throw new CliError("Installed hash differs from staged.");
            var errors = Model.Errors(Load(target));
            if (errors.Length > 0) throw new CliError($"Installed module reopened with {errors.Length} errors.");
        }
        catch
        {
            File.Copy(backup, target, overwrite: true);
            throw;
        }
        return Emit(new { installed = target, backup, sha256 = Hash(target) });
    }

    static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
