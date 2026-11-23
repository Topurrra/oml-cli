using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// The handlers of the Check, Review and Docs pages and of the Explorer additions (find --text, tree, validate,
// test bdd). Same rules as DashboardApi.cs: arguments are built from checked input, nothing here writes a module.
static partial class Dashboard
{
    static bool Flag(JsonObject body, string name) => body[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    // "Show timings" on the page: --timings on commands that load a module (the launcher commands refuse it).
    static List<string> Timed(JsonObject body, List<string> args)
    {
        if (Flag(body, "timings")) args.Add("--timings");
        return args;
    }

    // Absent (or null) gives the default; anything else must be a whole number in range, never quietly replaced.
    static int Number(JsonObject body, string name, int min, int max, int fallback)
    {
        if (body[name] is null) return fallback;
        if (body[name] is JsonValue v && v.TryGetValue<int>(out var n) && n >= min && n <= max) return n;
        throw new BadInput($"{name} must be a whole number from {min} to {max}.");
    }

    // Free text that becomes one argument: search words, a selector, a test value. A leading dash would read as an option.
    static string Words(string? text, string what, int max = 200)
    {
        if (text is null) throw new BadInput($"Type {what}.");
        if (text.Length > max) throw new BadInput($"{what} is longer than {max} characters.");
        if (text.StartsWith('-')) throw new BadInput($"{what} cannot start with '-': {text}");
        if (text.Any(char.IsControl)) throw new BadInput($"{what} has a control character.");
        return text;
    }

    // An element path as describe, tree and check print it: MainFlow/Orders/Button[0], Screen:Orders, (System).Login.
    static readonly Regex PathPattern = new(@"^[A-Za-z0-9_(][A-Za-z0-9_ .:/\[\]()*]{0,399}$");

    static string ElementPath(string? text)
    {
        if (text is null) throw new BadInput("Type an element name or path.");
        if (!PathPattern.IsMatch(text)) throw new BadInput($"'{text}' is not an element path. Use names, / between parts, Kind: and [index], as oml tree prints them.");
        return text;
    }

    static string? JsonFile(JsonObject body, string name, string what)
    {
        if (Str(body, name) is null) return null;
        var path = UserPath(Str(body, name), what);
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new BadInput($"{what} must be a .json file: {path}");
        if (!File.Exists(path)) throw new BadInput($"File not found: {path}");
        return path;
    }

    // ---- Review (the review fence of an agent's change walkthrough) ----

    static async Task<Reply> ReviewModules(JsonObject body)
    {
        var (before, after) = (ModuleFile(body, "before"), ModuleFile(body, "after"));
        if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase)) throw new BadInput("Before and after are the same file. Choose the staged file as after.");
        var folder = NewWorkFolder("review");
        var (md, html, notes) = (Path.Combine(folder, "review.md"), Path.Combine(folder, "review.html"), Path.Combine(folder, "release-notes.md"));
        var args = new List<string> { "review", before, after, "--md", md, "--out", html, "--notes", notes };
        if (JsonFile(body, "rules", "The rules file") is { } rules) args.AddRange(["--rules", rules]);
        var r = await Oml([.. Timed(body, args)]);
        return Wrap(r, await ReviewFiles(folder, md, html, notes));
    }

    // The files of a review (this page and the Platform server page's compare).
    static async Task<JsonObject> ReviewFiles(string folder, string md, string html, string notes)
    {
        var extra = Report(html, null);
        // The Markdown and the notes stay in the answer for Copy; Download takes the Markdown file from /download/<id>.
        extra["markdown"] = File.Exists(md) ? await File.ReadAllTextAsync(md) : null;
        if (File.Exists(md)) extra["markdownDownload"] = AddDownload("review.md", "text/markdown; charset=utf-8", md);
        extra["notes"] = File.Exists(notes) ? await File.ReadAllTextAsync(notes) : null;
        extra["folder"] = folder;
        return extra;
    }

    // ---- Search and Docs (launcher commands: no Service Studio) ----

    static async Task<Reply> SearchDocs(JsonObject body)
    {
        var q = Words(Str(body, "q"), "what to search for");
        return Str(body, "scope") switch
        {
            "spec" => Wrap(await Oml("spec", "--search", q, "--top", "8")),
            "warn" => Wrap(await Oml("explain-warning", q)),
            _ => Wrap(await Oml("doc", "--search", q, "--top", "8")),
        };
    }

    static readonly Regex OpenCommand = new("^oml (doc|spec|explain-warning) \"?([^\"]+?)\"?$");
    static readonly Regex OpenHelp = new("^oml ([a-z-]+(?: [a-z-]+)?) --help$");
    static readonly Regex OpenRecipe = new(@"^skill/recipes/([A-Za-z0-9-]+\.jsonc)$");

    // Opens one search hit: its `open` value is a command (oml doc ..., oml spec ..., oml explain-warning ..., oml <cmd> --help)
    // or a recipe path. Only these forms run; the target is checked like any other input.
    static async Task<Reply> OpenDoc(JsonObject body)
    {
        var open = Str(body, "open") ?? throw new BadInput("Choose a search result to open.");
        // Help first: `oml doc --help` is the usage of doc, not a doc topic.
        if (OpenHelp.Match(open) is { Success: true } help)
            return new Reply(200, new JsonObject { ["code"] = 0, ["output"] = "```text\n" + CliUsage.For(help.Groups[1].Value) + "\n```", ["cli"] = open });
        if (OpenCommand.Match(open) is { Success: true } m)
            return Wrap(await Oml(m.Groups[1].Value, Words(m.Groups[2].Value, "the topic")));
        if (OpenRecipe.Match(open) is { Success: true } recipe && Resource("recipes." + recipe.Groups[1].Value) is { } text)
            return new Reply(200, new JsonObject { ["code"] = 0, ["output"] = "```json\n" + text.TrimEnd() + "\n```", ["cli"] = "type " + open.Replace('/', '\\') });
        throw new BadInput($"This result cannot be opened here: {open}");
    }

    // ---- Explorer additions ----

    static async Task<Reply> FindText(JsonObject body) =>
        Wrap(await Oml([.. Timed(body, ["find", ModuleFile(body), "--text", Words(Str(body, "text"), "words to search for"), "--top", Number(body, "top", 1, 50, 10).ToString()])]));

    static async Task<Reply> TreeElement(JsonObject body) =>
        Wrap(await Oml([.. Timed(body, ["tree", ModuleFile(body), ElementPath(Str(body, "name")), "--depth", Number(body, "depth", 1, 13, 2).ToString()])]));

    static async Task<Reply> ValidateModule(JsonObject body) => Wrap(await Oml([.. Timed(body, ["validate", ModuleFile(body), "--warnings"])]));
}
