namespace OmlCli;

// Commands that talk to the running Service Studio (its MCP server): see Studio.cs.
public static partial class Cli
{
    static int? CmdStudio(List<string> args) => Emit(Studio.Status());

    static int? CmdPull(List<string> args)
    {
        if (args.Count > 4) return null;
        var outPath = Opt(args, "--out");
        var module = args.Skip(1).Where(a => a != "--out" && a != outPath).FirstOrDefault();
        return Studio.Pull(module, outPath);
    }

    static int? CmdPublish(List<string> args) =>
        args.Count == 2 ? Studio.Publish(args[1])
        : args.Count == 5 && Opt(args, "--in") is { } folder && Opt(args, "--order") is { } order ? PublishBatch.Execute(folder, order)
        : null;

    static int? CmdReferences(List<string> args) => args.Count == 2 ? Studio.References(args[1]) : null;
}
