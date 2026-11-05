using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// `oml hooks`: a Claude Code PreToolUse hook that turns two skill instructions into checks the agent cannot skip, at no
// token cost. It refuses (exit 2, the reason on stderr, which Claude Code shows the agent):
//   - reading a large skill reference whole (commands.md about 47k tokens, spec.md about 19k, model-api.md about 14k):
//     a Read without a limit of at most 400 lines, a Grep in content mode without a head_limit of at most 100, or a
//     cat / type / Get-Content of the file without a pipe or a line limit;
//   - changing an .oml file with a tool or a shell (rule 9): Edit, Write, MultiEdit, NotebookEdit, a redirect,
//     Set-Content / Add-Content / Out-File, sed -i, or WriteAllBytes / WriteAllText.
// Anything it cannot read is allowed: a broken hook must never block the agent's work.
// `install` adds it to <project>\.claude\settings.local.json (personal and not committed: the command holds this
// machine's oml.exe path); `remove` takes out only oml's entry; `status` says whether it is there.
static class AgentHooks
{
    const string Usage = "Usage: oml hooks [status|install|remove] [--dir <project folder>]   (Claude Code hooks; oml hooks check is what the hook runs)";
    const string Matcher = "Read|Grep|Edit|Write|MultiEdit|NotebookEdit|Bash|PowerShell";
    const string Marker = " hooks check";
    const int MaxReadLines = 400, MaxGrepLines = 100;

    static readonly (string File, string Instead)[] BigDocs =
    [
        ("commands.md", "oml doc commands#<section> (oml doc commands lists the sections), or oml <command> --help"),
        ("spec.md", "oml spec <key>, or oml spec --search \"<words>\""),
        ("model-api.md", "a Read with offset and limit, or Grep with head_limit"),
    ];
    static readonly Regex Separators = new(@"&&|\|\||;|\r?\n", RegexOptions.CultureInvariant);
    static readonly Regex Reader = new(@"^\s*(?:&\s*)?(?:cat|type|more|less|gc|Get-Content|bat)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex LineLimit = new(@"-(?:TotalCount|Head|Tail|First|Last)\b|\|", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex OmlWrite = new(
        @">{1,2}\s*[""']?[^\s""'|;&>]*\.oml\b" +
        @"|\b(?:Set-Content|Add-Content|Out-File)\b[^|;&]*\.oml\b" +
        @"|\bsed\b[^|;&]*\s-i\b[^|;&]*\.oml\b" +
        @"|\bWriteAll(?:Bytes|Text|Lines)\s*\([^)]*\.oml\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static int Run(string[] args)
    {
        if (args is ["check"]) return Check(Console.In.ReadToEnd());
        if (args is ["--help" or "-h"]) { Console.WriteLine(Usage); return 0; }
        var verb = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "status";
        var rest = args.Length > 0 && !args[0].StartsWith("--") ? args[1..] : args;
        string? dir = null;
        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--dir" && i + 1 < rest.Length) dir = rest[++i];
            else return Fail($"Unknown or incomplete option {rest[i]}. {Usage}");
        }
        dir = Path.GetFullPath(dir ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(dir)) return Fail($"Folder not found: {dir}");
        var settings = Path.Combine(dir, ".claude", "settings.local.json");
        return verb switch
        {
            "status" => Status(settings),
            "install" => Install(settings),
            "remove" => Remove(settings),
            _ => Fail($"Unknown hooks command '{verb}'. {Usage}"),
        };
    }

    static int Fail(string message)
    {
        TerminalOutput.PrintObject(new { error = message });
        return 2;
    }

    // The hook itself: Claude Code sends {tool_name, tool_input, ...} on stdin.
    static int Check(string input)
    {
        string? reason;
        try { reason = JsonNode.Parse(input) is JsonObject o ? Refusal(o["tool_name"]?.GetValue<string>() ?? "", o["tool_input"] as JsonObject ?? []) : null; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { reason = null; }
        if (reason is null) return 0;
        Console.Error.WriteLine("oml hook: " + reason);
        return 2;
    }

    const string TestDataOnly = "The test-data API is called only through oml test data (create, check, cleanup), so every result comes from oml's output and the ledger stays right.";

    internal static string? Refusal(string tool, JsonObject input)
    {
        string? Text(string key) => input[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        int? Number(string key) => input[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
        switch (tool)
        {
            case "Read" when BigDoc(Text("file_path")) is { } doc && Number("limit") is not (> 0 and <= MaxReadLines):
                return $"{doc.File} is a large reference; do not read it whole. Use {doc.Instead}, or Read with a limit of at most {MaxReadLines} lines.";
            case "Grep" when BigDoc(Text("path")) is { } doc && Text("output_mode") == "content" && Number("head_limit") is not (> 0 and <= MaxGrepLines):
                return $"Grep in content mode on {doc.File} returns large parts of it. Use {doc.Instead}, or set head_limit to at most {MaxGrepLines}.";
            case "Edit" or "Write" or "MultiEdit" or "NotebookEdit" when IsOml(Text("file_path") ?? Text("notebook_path")):
                return "rule 9: an .oml file is a binary package. Never edit or write it with a tool; change modules only with oml apply to a new staged file.";
            case "WebFetch" when Text("url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u) && TestDataCommand.AllowedHosts(Environment.CurrentDirectory).Contains(u.Host.ToLowerInvariant()):
                return TestDataOnly;
            case "Bash" or "PowerShell" when Text("command") is { } command:
                var testHosts = TestDataCommand.AllowedHosts(Environment.CurrentDirectory);
                foreach (var part in Separators.Split(command))
                {
                    // The test-data API only through oml test data, so every value an agent reports comes from oml's output.
                    if (testHosts.Any(h => part.Contains(h, StringComparison.OrdinalIgnoreCase)) && !Regex.IsMatch(part, @"^\s*(&\s*)?(""[^""]*\boml(\.exe)?""|\S*\boml(\.exe)?)\s+test\s+data\b", RegexOptions.IgnoreCase))
                        return TestDataOnly;
                    if (OmlWrite.IsMatch(part))
                        return "rule 9: this command writes an .oml file. Change modules only with oml apply to a new staged file; install copies it over the target after the user says yes.";
                    if (Reader.IsMatch(part) && !LineLimit.IsMatch(part) && BigDocs.FirstOrDefault(d => Regex.IsMatch(part, @"reference[\\/]" + Regex.Escape(d.File) + @"\b", RegexOptions.IgnoreCase)) is { File: not null } doc)
                        return $"{doc.File} is a large reference; do not print it whole. Use {doc.Instead}.";
                }
                return null;
        }
        return null;
    }

    // Only the skill's own copies (a path that ends in reference\<file>): another project's spec.md is not refused.
    static (string File, string Instead)? BigDoc(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Replace('\\', '/').TrimEnd('/');
        foreach (var d in BigDocs)
            if (p.EndsWith("/reference/" + d.File, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    static bool IsOml(string? path) => path is not null && path.Trim().EndsWith(".oml", StringComparison.OrdinalIgnoreCase);

    static string Command() => $"\"{Environment.ProcessPath}\"" + Marker;

    static bool Ours(JsonNode? entry) =>
        entry?["hooks"] is JsonArray hooks && hooks.Any(h => h?["command"] is JsonValue v && v.TryGetValue<string>(out var c) && c.EndsWith(Marker, StringComparison.Ordinal) && c.Contains("oml", StringComparison.OrdinalIgnoreCase));

    // The settings file, or null when it is not valid JSON (then nothing is written).
    static JsonObject? Load(string settings, out string? error)
    {
        error = null;
        if (!File.Exists(settings)) return [];
        try
        {
            if (JsonNode.Parse(File.ReadAllText(settings), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o) return o;
            error = $"{settings} is not a JSON object; fix or remove it first. Nothing was changed.";
        }
        catch (JsonException e) { error = $"{settings} is not valid JSON ({e.Message}); fix or remove it first. Nothing was changed."; }
        return null;
    }

    static void Save(string settings, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
    }

    static JsonArray? PreToolUse(JsonObject root) => root["hooks"]?["PreToolUse"] as JsonArray;

    static int Status(string settings)
    {
        if (Load(settings, out var error) is not { } root) return Fail(error!);
        var entry = PreToolUse(root)?.FirstOrDefault(Ours);
        var command = entry?["hooks"]?.AsArray().Select(h => h?["command"]?.GetValue<string>()).FirstOrDefault(c => c?.EndsWith(Marker) == true);
        var exe = command is null ? null : Regex.Match(command, "^\"([^\"]+)\"").Groups[1].Value;
        TerminalOutput.PrintObject(new
        {
            settings, installed = entry is not null, command,
            exeFound = exe is null ? (bool?)null : File.Exists(exe),
            hint = entry is null ? "oml hooks install adds the hook for this project." : exe is not null && !File.Exists(exe) ? "oml.exe moved: run oml hooks install again." : null,
        });
        return 0;
    }

    static int Install(string settings)
    {
        if (Load(settings, out var error) is not { } root) return Fail(error!);
        if (root["hooks"] is not JsonObject hooks) root["hooks"] = hooks = [];
        if (hooks["PreToolUse"] is not JsonArray list) hooks["PreToolUse"] = list = [];
        var replaced = list.Where(Ours).ToList();
        foreach (var old in replaced) list.Remove(old);
        list.Add(new JsonObject
        {
            ["matcher"] = Matcher,
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = Command(), ["timeout"] = 10 }),
        });
        Save(settings, root);
        TerminalOutput.PrintObject(new
        {
            settings, installed = true, replaced = replaced.Count > 0, command = Command(), matcher = Matcher,
            blocks = new[] { "reading skill/reference/commands.md, spec.md or model-api.md whole", "editing or writing .oml files (rule 9)" },
            next = "Claude Code reads the hook when a session starts in this folder. oml hooks remove takes it out.",
        });
        return 0;
    }

    static int Remove(string settings)
    {
        if (!File.Exists(settings)) { TerminalOutput.PrintObject(new { settings, removed = false }); return 0; }
        if (Load(settings, out var error) is not { } root) return Fail(error!);
        var removed = false;
        if (PreToolUse(root) is { } list)
        {
            foreach (var old in list.Where(Ours).ToList()) { list.Remove(old); removed = true; }
            if (list.Count == 0) (root["hooks"] as JsonObject)!.Remove("PreToolUse");
            if (root["hooks"] is JsonObject { Count: 0 }) root.Remove("hooks");
        }
        if (removed) Save(settings, root);
        TerminalOutput.PrintObject(new { settings, removed });
        return 0;
    }
}
