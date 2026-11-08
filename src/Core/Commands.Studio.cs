namespace OmlCli;

// Commands that talk to the running Service Studio (its MCP server): see Studio.cs.
public static partial class Cli
{
    static int? CmdStudio(List<string> args) => args.Count switch
    {
        1 => Emit(Studio.Status()),
        2 when args[1] == "tabs" => Emit(StudioUi.Tabs()),
        2 when args[1] == "focus" => StudioUi.Focus(),
        3 when args[1] == "close" => Studio.CloseModule(args[2]),
        3 when args[1] == "open" => throw new CliError(StudioUi.OpenBlocked, 2),
        _ => null,
    };

    static int? CmdPull(List<string> args)
    {
        if (args.Count > 4) return null;
        var outPath = Opt(args, "--out");
        var module = args.Skip(1).Where(a => a != "--out" && a != outPath).FirstOrDefault();
        return Studio.Pull(module, outPath);
    }

    static int? CmdPublish(List<string> args) =>
        args.Count == 2 ? Studio.Publish(args[1])
        : args.Count == 3 && args[2] == "--close" && args[1].EndsWith(".oml", StringComparison.OrdinalIgnoreCase) ? Studio.Publish(args[1], close: true)
        : args.Count == 5 && Opt(args, "--in") is { } folder && Opt(args, "--order") is { } order ? PublishBatch.Execute(folder, order)
        : null;

    static int? CmdReferences(List<string> args) => args.Count == 2 ? Studio.References(args[1]) : null;
}
