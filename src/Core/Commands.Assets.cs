namespace OmlCli;

// Commands for packages, extensions, templates and translations.
public static partial class Cli
{
    static int? CmdApp(List<string> args) => Packages.Run(args);

    static int? CmdExtension(List<string> args) => Extensions.Run(args);

    static int? CmdTemplates(List<string> args) => Emit(Templates.List());

    static int? CmdTranslations(List<string> args) =>
        args.Count is 4 or 6 && args[1] == "export" ? Content.Export(args[2], args[3], Opt(args, "--format")) : null;
}
