using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// `oml hooks`: a pre-tool hook that turns skill instructions into checks the agent cannot skip, at no token cost. It
// refuses (exit 2, the reason on stderr):
//   - reading a large skill reference whole (commands.md about 47k tokens, spec.md about 19k, model-api.md about 14k):
//     a read without a limit of at most 400 lines, a Grep in content mode without a head_limit of at most 100, or a
//     cat / type / Get-Content of the file without a pipe or a line limit;
//   - changing an .oml file with a tool, a patch or a shell (rule 9);
//   - calling the test-data API other than through oml test data.
// Anything it cannot read is allowed: a broken hook must never block the agent's work.
// One check serves every agent that runs command hooks before a tool. `install` writes it where each one looks:
//   claude  .claude\settings.local.json  Claude Code, and also GitHub Copilot (VS Code agent mode, the Copilot app and
//           CLI) and Cursor, which read Claude Code's settings files;
//   codex   .codex\hooks.json            Codex (the project must be trusted in Codex);
//   gemini  .gemini\settings.json        Gemini CLI (BeforeTool).
// The files hold this machine's oml.exe path, so they are personal and not committed. `remove` takes out only oml's
// entries; `status` says where it is installed.
static class AgentHooks
{
    const string Usage = "Usage: oml hooks [status|install|remove] [--dir <project folder>] [--agent claude|codex|gemini|all]   (oml hooks check is what the hook runs)";

    sealed record Target(string Agent, string File, string Event, string Matcher, string For);
    static readonly Target[] Targets =
    [
        new("claude", Path.Combine(".claude", "settings.local.json"), "PreToolUse", "Read|Grep|Edit|Write|MultiEdit|NotebookEdit|Bash|PowerShell|Shell|StrReplace|Delete|WebFetch", "Claude Code, GitHub Copilot (VS Code, app, CLI), Cursor"),
        new("codex", Path.Combine(".codex", "hooks.json"), "PreToolUse", "Bash|apply_patch|Edit|Write", "Codex"),
        new("gemini", Path.Combine(".gemini", "settings.json"), "BeforeTool", "run_shell_command|write_file|replace|read_file|web_fetch", "Gemini CLI"),
    ];
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
        string? dir = null, agent = "all";
        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--dir" && i + 1 < rest.Length) dir = rest[++i];
            else if (rest[i] == "--agent" && i + 1 < rest.Length) agent = rest[++i].ToLowerInvariant();
            else return Fail($"Unknown or incomplete option {rest[i]}. {Usage}");
        }
        var targets = agent == "all" ? Targets : Targets.Where(t => t.Agent == agent).ToArray();
        if (targets.Length == 0) return Fail($"Unknown agent '{agent}'. {Usage}");
        dir = Path.GetFullPath(dir ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(dir)) return Fail($"Folder not found: {dir}");
        if (verb is not ("status" or "install" or "remove")) return Fail($"Unknown hooks command '{verb}'. {Usage}");
        // Check every file first, so a broken one changes nothing anywhere.
        var loaded = new List<(Target T, string File, JsonObject Root)>();
        foreach (var t in targets)
        {
            var file = Path.Combine(dir, t.File);
            if (Load(file, out var error) is not { } root) return Fail(error!);
            loaded.Add((t, file, root));
        }
        var results = loaded.Select(x => verb switch
        {
            "install" => Install(x.T, x.File, x.Root),
            "remove" => Remove(x.T, x.File, x.Root),
            _ => Status(x.T, x.File, x.Root),
        }).ToList();
        TerminalOutput.PrintObject(new
        {
            folder = dir, agents = results,
            blocks = verb == "install" ? new[] { "reading skill/reference/commands.md, spec.md or model-api.md whole", "editing or writing .oml files (rule 9)", "calling the test-data API other than through oml test data" } : null,
            next = verb switch
            {
                "install" => "Each agent reads its hook when a session starts in this folder (Codex: trust the project first). oml hooks remove takes them out.",
                "status" when loaded.Any(x => Entries(x.Root, x.T)?.Any(Ours) != true) => "oml hooks install adds the hook for every agent (--agent to choose one).",
                _ => null,
            },
        });
        return 0;
    }
    static int Fail(string message)
    {
        TerminalOutput.PrintObject(new { error = message });
        return 2;
    }

    // The hook itself. Each agent sends its own JSON on stdin; Payload reads them all. A refusal exits 2 with the reason on
    // stderr (Claude Code, Codex, Gemini CLI, Cursor and Copilot all block on exit 2) and also prints a deny decision on
    // stdout in the shapes Copilot, Cursor and Codex read, so it holds whichever one the agent looks at.
    static int Check(string input)
    {
        string? reason;
        try { reason = JsonNode.Parse(input) is JsonObject o ? Refusal(o) : null; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { reason = null; }
        if (reason is null) return 0;
        Console.Error.WriteLine("oml hook: " + reason);
        Console.Out.WriteLine(new JsonObject
        {
            ["permissionDecision"] = "deny", ["permissionDecisionReason"] = "oml: " + reason,
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PreToolUse", ["permissionDecision"] = "deny", ["permissionDecisionReason"] = "oml: " + reason },
            ["decision"] = "deny", ["reason"] = "oml: " + reason,
        }.ToJsonString());
        return 2;
    }

    const string TestDataOnly = "The test-data API is called only through oml test data (create, update, check, cleanup), so every result comes from oml's output.";

    enum Kind { Other, Shell, Edit, Read, Grep, Web }

    // The tool and its arguments, from any of the payloads: tool_name/tool_input (Claude Code, Codex, Gemini CLI, Cursor,
    // Copilot with PascalCase PreToolUse) or toolName/toolArgs (Copilot's own preToolUse; toolArgs may be a JSON string).
    static (Kind Kind, string Tool, JsonObject Input) Payload(JsonObject o)
    {
        var tool = (o["tool_name"] ?? o["toolName"]) is JsonValue t && t.TryGetValue<string>(out var name) ? name : "";
        var raw = o["tool_input"] ?? o["toolArgs"];
        if (raw is JsonValue v && v.TryGetValue<string>(out var text))
            try { raw = JsonNode.Parse(text); } catch (JsonException) { raw = new JsonObject { ["command"] = text }; }
        var input = raw as JsonObject ?? [];
        var kind = tool.ToLowerInvariant() switch
        {
            "bash" or "powershell" or "shell" or "run_shell_command" or "run_in_terminal" or "runinterminal" => Kind.Shell,
            "edit" or "write" or "multiedit" or "notebookedit" or "strreplace" or "str_replace_editor" or "delete" or "write_file" or "replace" or "create" or "apply_patch" => Kind.Edit,
            "read" or "read_file" or "view" => Kind.Read,
            "grep" or "search_file_content" => Kind.Grep,
            "webfetch" or "web_fetch" => Kind.Web,
            _ => Kind.Other,
        };
        return (kind, tool, input);
    }

    internal static string? Refusal(JsonObject payload)
    {
        var (kind, tool, input) = Payload(payload);
        string? Text(params string[] keys) => keys.Select(k => input[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(s => s is not null);
        int? Number(string key) => input[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
        var path = Text("file_path", "path", "absolute_path", "notebook_path", "filePath");
        switch (kind)
        {
            case Kind.Read when BigDoc(path) is { } doc && Number("limit") is not (> 0 and <= MaxReadLines):
                return $"{doc.File} is a large reference; do not read it whole. Use {doc.Instead}, or Read with a limit of at most {MaxReadLines} lines.";
            case Kind.Grep when BigDoc(path) is { } doc && Text("output_mode") == "content" && Number("head_limit") is not (> 0 and <= MaxGrepLines):
                return $"Grep in content mode on {doc.File} returns large parts of it. Use {doc.Instead}, or set head_limit to at most {MaxGrepLines}.";
            // A patch (Codex apply_patch) names its files in "*** Update File: <path>" lines.
            case Kind.Edit when IsOml(path) || Regex.IsMatch(Text("command", "patch", "input") ?? "", @"^\*\*\* (Add|Update|Delete) File: .*\.oml\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase):
                return "rule 9: an .oml file is a binary package. Never edit or write it with a tool; change modules only with oml apply to a new staged file.";
            case Kind.Web when Text("url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u) && TestDataCommand.AllowedHosts(Environment.CurrentDirectory).Contains(u.Host.ToLowerInvariant()):
                return TestDataOnly;
            case Kind.Shell when Text("command", "command_line", "cmd") is { } command:
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

    static JsonArray? Entries(JsonObject root, Target t) => root["hooks"]?[t.Event] as JsonArray;

    static object Status(Target t, string file, JsonObject root)
    {
        var entry = Entries(root, t)?.FirstOrDefault(Ours);
        var command = entry?["hooks"]?.AsArray().Select(h => h?["command"] is JsonValue v && v.TryGetValue<string>(out var c) ? c : null).FirstOrDefault(c => c?.EndsWith(Marker) == true);
        var exe = command is null ? null : Regex.Match(command, "^\"([^\"]+)\"").Groups[1].Value;
        return new
        {
            agent = t.Agent, @for = t.For, file, installed = entry is not null,
            exeFound = exe is null ? (bool?)null : File.Exists(exe),
            hint = exe is not null && !File.Exists(exe) ? "oml.exe moved: run oml hooks install again." : null,
        };
    }

    static object Install(Target t, string file, JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) root["hooks"] = hooks = [];
        if (hooks[t.Event] is not JsonArray list) hooks[t.Event] = list = [];
        var replaced = list.Where(Ours).ToList();
        foreach (var old in replaced) list.Remove(old);
        list.Add(new JsonObject
        {
            ["matcher"] = t.Matcher,
            // Claude Code and Codex count the timeout in seconds, Gemini CLI in milliseconds.
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = Command(), ["timeout"] = t.Agent == "gemini" ? 10000 : 10 }),
        });
        Save(file, root);
        return new { agent = t.Agent, @for = t.For, file, installed = true, replaced = replaced.Count > 0 };
    }

    static object Remove(Target t, string file, JsonObject root)
    {
        var removed = false;
        if (Entries(root, t) is { } list)
        {
            foreach (var old in list.Where(Ours).ToList()) { list.Remove(old); removed = true; }
            if (list.Count == 0) (root["hooks"] as JsonObject)!.Remove(t.Event);
            if (root["hooks"] is JsonObject { Count: 0 }) root.Remove("hooks");
        }
        // A file that held only oml's hook goes; a file with the user's own settings stays.
        if (removed && root.Count == 0) File.Delete(file);
        else if (removed) Save(file, root);
        return new { agent = t.Agent, file, removed };
    }
}