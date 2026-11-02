using System.Text.Json.Nodes;

namespace OmlCli;

// `oml dif <file.md>`: a Markdown change walkthrough an agent writes for a person (the idea of diffmap, MIT, rebuilt for
// OutSystems), shown on the dashboard's Dif page. The page renders the Markdown and three oml fences: `callstack` (rows
// marked + new, - removed), `review` (oml review of a module and its staged copy, from the model, not the agent) and
// element links [[Orders.oml#MainFlow/Orders/LoadData]] that open describe. Comments stay in the browser and copy as a
// list for the agent. The dashboard only reads the file; relative paths in it are read from the file's folder.
static partial class Dashboard
{
    const string DifUsage = "oml dif <file.md> [--port N] [--no-open]";
    const long MaxDifBytes = 2 * 1024 * 1024;
    static string? difFile;

    // `oml dif <file.md> [--port N] [--no-open]`: the dashboard, opened on the Dif page for that file.
    public static int Dif(string[] args)
    {
        var file = args.FirstOrDefault(a => !a.StartsWith("--") && !int.TryParse(a, out _));
        if (file is null || args.Count(a => !a.StartsWith("--") && !int.TryParse(a, out _)) != 1)
        {
            Console.Error.WriteLine($"Usage: {DifUsage}");
            return 2;
        }
        return Run([.. args.Where(a => a != file), "--dif", file]);
    }

    // Checked when the dashboard starts: an existing Markdown file of at most 2 MB.
    static string? DifProblem(string path) =>
        !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? $"oml dif needs a Markdown (.md) file: {path}"
        : !File.Exists(path) ? $"File not found: {path}"
        : new FileInfo(path).Length > MaxDifBytes ? $"{path} is larger than 2 MB." : null;

    // GET dif: the file oml dif started with. POST dif {path}: another Markdown file the person typed.
    static Reply DifRead(string? typed)
    {
        string path;
        if (typed is null)
        {
            if (difFile is null) return new Reply(200, new JsonObject { ["file"] = null, ["hint"] = "Start it with oml dif <file.md>, or type the path of a Markdown file." });
            path = difFile;
        }
        else path = UserPath(typed, "a Markdown (.md) file");
        if (DifProblem(path) is { } problem) throw new BadInput(problem);
        var info = new FileInfo(path);
        return new Reply(200, new JsonObject
        {
            ["file"] = path, ["folder"] = info.DirectoryName, ["markdown"] = File.ReadAllText(path),
            ["modified"] = info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), ["bytes"] = info.Length,
        });
    }
}
