using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// `oml skills`: the user's own company skills (team rules, naming, Confluence pages saved as Markdown). Each one is a
// Markdown file with `name` and `description` frontmatter, as SKILL.md has. `add` checks it and copies it into
// %LOCALAPPDATA%\oml-cli\skills (OML_SKILLS overrides it, for tests). Then `oml doc <name>` prints it, `oml doc` lists it,
// and `oml doc --search` ranks it before oml's own docs: company rules override OutSystems best practices, never rules.md.
static class CompanySkills
{
    const string Usage = "Usage: oml skills [list]\n       oml skills add <file.md> [--force]\n       oml skills remove <name>";
    const int WarnTokens = 2000, MaxTokens = 8000, LongSentence = 25;
    internal const string Kind = "company";

    public static string Root => Environment.GetEnvironmentVariable("OML_SKILLS") is { Length: > 0 } root
        ? Path.GetFullPath(root)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oml-cli", "skills");

    // The installed skills: name -> file. A file that lost its frontmatter is skipped (`list` shows it).
    internal static Dictionary<string, string> Installed()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(Root)) return found;
        foreach (var file in Directory.EnumerateFiles(Root, "*.md").Order(StringComparer.Ordinal))
            if (Header(File.ReadAllText(file)) is { } h) found[h.Name] = file;
        return found;
    }

    internal static string Text(string file) => Body(File.ReadAllText(file));

    static int Fail(string message, int code = 2)
    {
        Console.WriteLine(JsonOutput.Serialize(new { error = message }));
        return code;
    }

    public static int Run(string[] args) => args switch
    {
        [] or ["list"] => List(),
        ["add", var file] => Add(file, force: false),
        ["add", var file, "--force"] => Add(file, force: true),
        ["add", "--force", var forced] => Add(forced, force: true),
        ["remove", var name] => Remove(name),
        ["--help" or "-h"] => Help(),
        _ => Fail(Usage),
    };

    static int Help() { Console.WriteLine(CliUsage.For("skills")); return 0; }

    static int List()
    {
        var skills = new JsonArray();
        foreach (var (name, file) in Installed())
        {
            var text = File.ReadAllText(file);
            skills.Add(new JsonObject { ["name"] = name, ["description"] = Header(text)!.Value.Description, ["tokens"] = Tokens(text), ["open"] = $"oml doc {name}" });
        }
        var output = new JsonObject { ["folder"] = Root, ["skills"] = skills };
        if (skills.Count == 0) output["hint"] = "No company skills yet. Write a .md file with name and description frontmatter, then: oml skills add <file.md>";
        Console.WriteLine(JsonOutput.Write(output));
        return 0;
    }

    static int Add(string path, bool force)
    {
        if (!File.Exists(path)) return Fail($"File not found: {path}");
        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return Fail("A company skill is a Markdown file (.md). Save a Confluence or Word page as Markdown first.");
        var text = File.ReadAllText(path);
        if (Header(text) is not { } header)
            return Fail("The file needs frontmatter with name and description at the top:\n---\nname: acme-naming\ndescription: Use when you name modules, entities or actions in Acme projects.\n---");
        if (!Regex.IsMatch(header.Name, "^[a-z0-9]+(-[a-z0-9]+)*$") || header.Name.Length > 40)
            return Fail($"Name '{header.Name}': use 1-40 lower-case letters, digits and hyphens (acme-naming).");
        if (DocCommand.Topics().Contains(header.Name, StringComparer.OrdinalIgnoreCase))
            return Fail($"'{header.Name}' is an oml doc topic. Choose another name, for example 'acme-{header.Name}'.");
        if (header.Description.Length < 10) return Fail("The description must say when to use the skill (at least 10 characters).");
        var tokens = Tokens(text);
        if (tokens > MaxTokens) return Fail($"The skill is {tokens} tokens; the limit is {MaxTokens}. Split it into smaller skills.");
        if (MemorySecrets.Find(text) is { } secret) return Fail($"The file holds a {secret}. Remove it: skills are plain text that agents read.");
        var installed = Installed();
        var target = Path.Combine(Root, header.Name + ".md");
        var replaced = installed.ContainsKey(header.Name);
        if (replaced && !force) return Fail($"A skill named '{header.Name}' exists. Add --force to replace it.");

        Directory.CreateDirectory(Root);
        if (replaced && !string.Equals(installed[header.Name], target, StringComparison.OrdinalIgnoreCase)) File.Delete(installed[header.Name]);
        File.WriteAllText(target, text);
        var output = new JsonObject { ["added"] = header.Name, ["replaced"] = replaced, ["tokens"] = tokens, ["file"] = target, ["open"] = $"oml doc {header.Name}" };
        var warnings = Warnings(text, tokens);
        if (warnings.Count > 0) output["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray());
        Console.WriteLine(JsonOutput.Write(output));
        return 0;
    }

    static int Remove(string name)
    {
        if (!Installed().TryGetValue(name, out var file)) return Fail($"No company skill named '{name}'. Run 'oml skills' for the list.", 1);
        File.Delete(file);
        Console.WriteLine(JsonOutput.Serialize(new { removed = name }));
        return 0;
    }

    // Advice, not refusals: the skill is added anyway.
    static List<string> Warnings(string text, int tokens)
    {
        var warnings = new List<string>();
        if (tokens > WarnTokens) warnings.Add($"{tokens} tokens: agents read it whole. Under {WarnTokens} is better; split it by subject.");
        var body = Regex.Replace(Body(text), "```.*?```", " ", RegexOptions.Singleline);
        var sentences = Regex.Split(body, @"(?<=[.!?])\s+|\n").Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith('|') && !s.StartsWith('#'));
        var longOnes = sentences.Count(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > LongSentence);
        if (longOnes > 0) warnings.Add($"{longOnes} sentence(s) over {LongSentence} words. Short sentences work better for cheap models.");
        if (!Regex.IsMatch(body, @"^#{1,3} ", RegexOptions.Multiline)) warnings.Add("No headings. Headings let oml doc --search find and open one section.");
        return warnings;
    }

    static int Tokens(string text) => (text.Replace("\r\n", "\n").Length + 3) / 4;

    // The frontmatter between the first two "---" lines: name and description, or null.
    internal static (string Name, string Description)? Header(string text)
    {
        var m = Regex.Match(text.Replace("\r\n", "\n"), @"\A---\n(.*?)\n---\n", RegexOptions.Singleline);
        if (!m.Success) return null;
        string? Field(string key) => Regex.Match(m.Groups[1].Value, $@"^{key}:\s*(.+?)\s*$", RegexOptions.Multiline) is { Success: true } f ? f.Groups[1].Value.Trim('"', '\'') : null;
        return Field("name") is { Length: > 0 } name && Field("description") is { } description ? (name, description) : null;
    }

    static string Body(string text) => Regex.Replace(text.Replace("\r\n", "\n"), @"\A---\n.*?\n---\n", "", RegexOptions.Singleline).Trim();
}
