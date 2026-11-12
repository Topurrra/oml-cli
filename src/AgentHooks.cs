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
// The project profile (.oml/config.json, see ProjectConfig) turns the guards on or off (hooks.*), adds the browser
// guard (browser = agent) and is given to the agent by the session-start hook (`oml hooks session`).
// The command line: Claude Code runs a hook in bash, Copilot on Windows in PowerShell, where a line that starts with a
// quoted path is a string, not a command, so the hook fails and Copilot denies every tool call ("hook errored"). So the
// path is written without quotes and with forward slashes, which bash, PowerShell and cmd all run; a path that needs
// quotes keeps them and gets a `powershell` line (& "path") for Copilot.
static class AgentHooks
{
    const string Usage = "Usage: oml hooks [status|install|remove] [--dir <project folder>] [--agent claude|codex|gemini|all]   (oml hooks check is what the hook runs)";

    sealed record Target(string Agent, string File, string Event, string Matcher, string For, string StopEvent, string SessionEvent = "SessionStart");
    static readonly Target[] Targets =
    [
        new("claude", Path.Combine(".claude", "settings.local.json"), "PreToolUse", "Read|Grep|Edit|Write|MultiEdit|NotebookEdit|Bash|PowerShell|Shell|StrReplace|Delete|WebFetch", "Claude Code, GitHub Copilot (VS Code, app, CLI), Cursor", "Stop"),
        new("codex", Path.Combine(".codex", "hooks.json"), "PreToolUse", "Bash|apply_patch|Edit|Write", "Codex", "Stop"),
        new("gemini", Path.Combine(".gemini", "settings.json"), "BeforeTool", "run_shell_command|write_file|replace|read_file|web_fetch", "Gemini CLI", "AfterAgent"),
    ];
    const string Marker = " hooks check", StopMarker = " hooks stop", SessionMarker = " hooks session";
    const int MaxReadLines = 400, MaxGrepLines = 100;

    static readonly (string File, string Instead)[] BigDocs =
    [
        ("commands.md", "oml doc commands#<section> (oml doc commands lists the sections), or oml <command> --help"),
        ("spec.md", "oml spec <key>, or oml spec --search \"<words>\""),
        ("model-api.md", "a Read with offset and limit, or Grep with head_limit"),
    ];
    // The shell patterns sit in their own class, so a Read, Edit or Grep call does not pay for building them.
    static class Rx
    {
        internal static readonly Regex Separators = new(@"&&|\|\||;|\r?\n", RegexOptions.CultureInvariant);
        internal static readonly Regex Reader = new(@"^\s*(?:&\s*)?(?:cat|type|more|less|gc|Get-Content|bat)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        internal static readonly Regex LineLimit = new(@"-(?:TotalCount|Head|Tail|First|Last)\b|\|", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // A shell command that starts a browser, and one that is oml itself (oml qa starts its own browser).
        internal static readonly Regex Browser = new(@"(?<![\w-])(?:msedge|chrome|chromium|firefox)(?:\.exe)?(?![\w-])|\b(?:start|Start-Process|xdg-open|open)\s+[""']?https?://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        internal static readonly Regex OmlCommand = new(@"^\s*(?:&\s*)?(?:""[^""]*\boml(?:\.exe)?""|\S*\boml(?:\.exe)?)\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        internal static readonly Regex OmlWrite = new(
            @">{1,2}\s*[""']?[^\s""'|;&>]*\.oml\b" +
            @"|\b(?:Set-Content|Add-Content|Out-File)\b[^|;&]*\.oml\b" +
            @"|\bsed\b[^|;&]*\s-i\b[^|;&]*\.oml\b" +
            @"|\bWriteAll(?:Bytes|Text|Lines)\s*\([^)]*\.oml\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static int Run(string[] args)
    {
        if (args is ["check"]) return Check(Console.In.ReadToEnd());
        if (args is ["stop"]) return Stop(Console.In.ReadToEnd());
        if (args is ["session"]) return Session(Console.In.ReadToEnd());
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
            reminds = verb == "install" ? "once, when the agent finishes a session that changed a module or ran qa but saved nothing to the project memory: to save what the next agent should know, or say there is nothing (OML_MEMORY_REMINDER=0 turns it off)" : null,
            next = verb switch
            {
                "install" => "Each agent reads its hook when a session starts in this folder (Codex: trust the project first). oml hooks remove takes them out. oml config sets what the guards and the session profile do.",
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

    // The end-of-turn hook (Stop; Gemini CLI AfterAgent): a reminder to keep what the session learned, given at most once
    // and only when it is likely worth it, so it never costs a turn on a session that only read or answered. It speaks
    // only when all of these hold:
    //   - OML_MEMORY_REMINDER is not 0, and the agent is not already continuing because of a stop hook (stop_hook_active);
    //   - the project folder (cwd) has a memory store (.oml/memory/memory.log);
    //   - since the session started (its transcript file's creation time; else the last 2 hours) the usage log shows a
    //     command that changed something or tested the app (ChangeCommands) and no memory add or retract.
    // The reason says that finishing without a record is fine when nothing is worth keeping, so it never forces filler.
    // The decision goes on stdout with exit 0: "block" for Claude Code, Codex, Copilot and Cursor, "deny" for Gemini CLI;
    // each sends the reason back to the agent as the next prompt. Anything it cannot read lets the agent stop.
    static readonly HashSet<string> ChangeCommands = new(StringComparer.Ordinal)
        { "apply", "install", "new", "publish", "references", "studio close", "extension new", "app pack", "lifetime deploy", "lifetime tag", "qa" };

    static int Stop(string input)
    {
        string? reason = null;
        var gemini = false;
        try
        {
            if (JsonNode.Parse(input) is JsonObject o)
            {
                gemini = Str(o, "hook_event_name") == "AfterAgent";
                reason = MemoryReminder(o, DateTime.UtcNow);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException or ArgumentException) { reason = null; }
        if (reason is null) return 0;
        Console.Out.WriteLine(new JsonObject { ["decision"] = gemini ? "deny" : "block", ["reason"] = reason }.ToJsonString());
        return 0;
    }

    // The session-start hook: the project profile as context for the agent, so it needs no command (and no turn) to
    // learn what this project skips. Claude Code and Codex read hookSpecificOutput.additionalContext, Copilot the
    // top-level additionalContext; both are printed. Nothing is printed without a config file.
    static int Session(string input)
    {
        string? text = null;
        try
        {
            var cwd = JsonNode.Parse(input) is JsonObject o ? Str(o, "cwd") : null;
            text = ProjectConfig.SessionText(cwd ?? Environment.CurrentDirectory);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException or ArgumentException) { text = null; }
        if (text is null) return 0;
        Console.Out.WriteLine(new JsonObject
        {
            ["additionalContext"] = text,
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "SessionStart", ["additionalContext"] = text },
        }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return 0;
    }

    static string? Str(JsonObject o, params string[] keys) =>
        keys.Select(k => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(s => !string.IsNullOrEmpty(s));

    internal static string? MemoryReminder(JsonObject o, DateTime now)
    {
        if (Environment.GetEnvironmentVariable("OML_MEMORY_REMINDER")?.Trim() is "0" or "off" or "false") return null;
        if (o["stop_hook_active"] is JsonValue a && a.TryGetValue<bool>(out var active) && active) return null;
        var cwd = Str(o, "cwd") ?? Environment.CurrentDirectory;
        var config = ProjectConfig.Load(cwd);
        if (!config.On("memory.reminder") || !config.On("memory.write")) return null;
        if (!File.Exists(MemStore.LogPathOf(cwd))) return null;
        var log = UsageLog.FilePath;
        if (log is null || !File.Exists(log)) return null;
        var start = now.AddHours(-2);
        if (Str(o, "transcript_path", "transcriptPath") is { } transcript && File.Exists(transcript)) start = File.GetCreationTimeUtc(transcript);
        var rows = UsageLog.Read(log, maxBytes: 2 * 1024 * 1024).Where(r => r.Ts >= start && !r.InRun).ToList();
        if (rows.Any(r => r.Cmd is "memory add" or "memory retract" && r.Exit == 0)) return null;
        var changed = rows.Where(r => r.Exit == 0 && ChangeCommands.Contains(r.Cmd)).GroupBy(r => r.Cmd).Select(g => $"{g.Key} {g.Count()}x").ToList();
        if (changed.Count == 0) return null;
        return $"oml: this session ran {string.Join(", ", changed)} and saved nothing to the project memory. Before you finish: if it found something the next agent should know " +
            "(a decision and why, a trap or error and its fix, what the user wants, how to test or log in to the app), save one to three short records: " +
            "oml memory add \"<one fact>\" --kind decision|trap|fact|pref --module <Name> --why \"<reason>\" (run oml memory recall \"<topic>\" first, and use --supersedes <id> to correct an old one). " +
            "Do not save what the code or the module already shows. If nothing is worth keeping, say so in one line and finish.";
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
            "bash" or "powershell" or "shell" or "run_shell_command" or "run_in_terminal" or "runinterminal" or "execute" => Kind.Shell,
            "edit" or "write" or "multiedit" or "notebookedit" or "strreplace" or "str_replace_editor" or "delete" or "write_file" or "replace" or "create" or "apply_patch"
                // the edit tools of VS Code's Local harness
                or "replace_string_in_file" or "multi_replace_string_in_file" or "create_file" or "insert_edit_into_file" or "edit_file" or "edit_notebook_file" or "editfiles" or "createfile" => Kind.Edit,
            "read" or "read_file" or "readfile" or "view" => Kind.Read,
            "grep" or "search_file_content" => Kind.Grep,
            "webfetch" or "web_fetch" => Kind.Web,
            _ => Kind.Other,
        };
        return (kind, tool, input);
    }

    internal static string? Refusal(JsonObject payload)
    {
        var (kind, tool, input) = Payload(payload);
        if (kind == Kind.Other) return null; // a tool with nothing to check costs no file read
        var cwd = Str(payload, "cwd") ?? Environment.CurrentDirectory;
        var config = ProjectConfig.Load(cwd);
        bool bigDocs = config.On("hooks.bigDocs"), testApi = config.On("hooks.testDataApi");
        var noBrowser = config.Get("browser") == "agent" && config.On("hooks.externalBrowser");
        string? Text(params string[] keys) => keys.Select(k => input[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).FirstOrDefault(s => s is not null);
        int? Number(string key) => input[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
        var path = Text("file_path", "path", "absolute_path", "notebook_path", "filePath");
        switch (kind)
        {
            case Kind.Read when bigDocs && BigDoc(path) is { } doc && !Ranged(input):
                return $"{doc.File} is a large reference; do not read it whole. Use {doc.Instead}, or read at most {MaxReadLines} lines at a time (a limit, a view_range, or a start and end line).";
            case Kind.Grep when bigDocs && BigDoc(path) is { } doc && Text("output_mode") == "content" && Number("head_limit") is not (> 0 and <= MaxGrepLines):
                return $"Grep in content mode on {doc.File} returns large parts of it. Use {doc.Instead}, or set head_limit to at most {MaxGrepLines}.";
            // A patch (Codex apply_patch) names its files in "*** Update File: <path>" lines.
            case Kind.Edit when IsOml(path) || Regex.IsMatch(Text("command", "patch", "input") ?? "", @"^\*\*\* (Add|Update|Delete) File: .*\.oml\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase):
                return "rule 9: an .oml file is a binary package. Never edit or write it with a tool; change modules only with oml apply to a new staged file.";
            case Kind.Web when testApi && Text("url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u) && TestDataCommand.AllowedHosts(Environment.CurrentDirectory).Contains(u.Host.ToLowerInvariant()):
                return TestDataOnly;
            case Kind.Shell when Text("command", "command_line", "cmd") is { } command:
                var testHosts = testApi ? TestDataCommand.AllowedHosts(Environment.CurrentDirectory) : [];
                foreach (var part in Rx.Separators.Split(command))
                {
                    // The test-data API only through oml test data, so every value an agent reports comes from oml's output.
                    if (testHosts.Any(h => part.Contains(h, StringComparison.OrdinalIgnoreCase)) && !Regex.IsMatch(part, @"^\s*(&\s*)?(""[^""]*\boml(\.exe)?""|\S*\boml(\.exe)?)\s+test\s+data\b", RegexOptions.IgnoreCase))
                        return TestDataOnly;
                    if (noBrowser && Rx.Browser.IsMatch(part) && !Rx.OmlCommand.IsMatch(part))
                        return "this project sets browser = agent (.oml/config.json): look at the app with your own built-in browser tool, not Edge, Chrome or Firefox started from a shell.";
                    if (Rx.OmlWrite.IsMatch(part))
                        return "rule 9: this command writes an .oml file. Change modules only with oml apply to a new staged file; install copies it over the target after the user says yes.";
                    if (bigDocs && Rx.Reader.IsMatch(part) && !Rx.LineLimit.IsMatch(part) && BigDocs.FirstOrDefault(d => Regex.IsMatch(part, @"reference[\\/]" + Regex.Escape(d.File) + @"\b", RegexOptions.IgnoreCase)) is { File: not null } doc)
                        return $"{doc.File} is a large reference; do not print it whole. Use {doc.Instead}.";
                }
                return null;
        }
        return null;
    }
    // A read of part of a file. Each agent names the range its own way: limit (Claude Code, Cursor, Gemini CLI),
    // view_range [first, last] (Copilot's view), startLine/endLine or start_line/end_line (VS Code's read_file).
    internal static bool Ranged(JsonObject input)
    {
        int? Int(params string[] keys) => keys.Select(k => input[k] is JsonValue v && v.TryGetValue<int>(out var n) ? n : (int?)null).FirstOrDefault(n => n is not null);
        if (Int("limit") is > 0 and <= MaxReadLines) return true;
        if ((input["view_range"] ?? input["viewRange"]) is JsonArray { Count: 2 } range
            && range[0] is JsonValue a && a.TryGetValue<int>(out var first) && range[1] is JsonValue b && b.TryGetValue<int>(out var last))
            return last >= first && last - first < MaxReadLines;
        if (Int("endLine", "end_line", "endLineNumberBaseZero", "end") is { } end)
            return end - (Int("startLine", "start_line", "startLineNumberBaseZero", "start", "offset") ?? 1) is >= 0 and < MaxReadLines;
        return false;
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

    // The exe path as every shell runs it: forward slashes and no quotes when it has only plain characters.
    static string Exe => Environment.ProcessPath ?? "oml";
    internal static bool NeedsQuotes => !Exe.All(c => char.IsAsciiLetterOrDigit(c) || "_./:~+-\\".Contains(c));
    static string Command(string marker = Marker) => NeedsQuotes ? $"\"{Exe}\"" + marker : Exe.Replace('\\', '/') + marker;

    static bool Ours(JsonNode? entry) =>
        entry?["hooks"] is JsonArray hooks && hooks.Any(h => h?["command"] is JsonValue v && v.TryGetValue<string>(out var c)
            && (c.EndsWith(Marker, StringComparison.Ordinal) || c.EndsWith(StopMarker, StringComparison.Ordinal) || c.EndsWith(SessionMarker, StringComparison.Ordinal)) && c.Contains("oml", StringComparison.OrdinalIgnoreCase));

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

    static JsonArray? Entries(JsonObject root, Target t, bool stop = false) => root["hooks"]?[stop ? t.StopEvent : t.Event] as JsonArray;

    static object Status(Target t, string file, JsonObject root)
    {
        var entry = Entries(root, t)?.FirstOrDefault(Ours);
        var command = entry?["hooks"]?.AsArray().Select(h => h?["command"] is JsonValue v && v.TryGetValue<string>(out var c) ? c : null).FirstOrDefault(c => c?.EndsWith(Marker) == true);
        var exe = command is null ? null : Regex.Match(command, "^\"([^\"]+)\"") is { Success: true } quoted ? quoted.Groups[1].Value : command[..^Marker.Length];
        var old = command is not null && command.StartsWith('"') && !NeedsQuotes;
        return new
        {
            agent = t.Agent, @for = t.For, file, installed = entry is not null,
            memoryReminder = Entries(root, t, stop: true)?.Any(Ours) == true,
            exeFound = exe is null ? (bool?)null : File.Exists(exe),
            hint = exe is not null && !File.Exists(exe) ? "oml.exe moved: run oml hooks install again."
                : old ? "The hook command starts with a quoted path, which fails in GitHub Copilot on Windows (PowerShell): run oml hooks install again."
                : entry is not null && root["hooks"]?[t.SessionEvent] is not JsonArray { Count: > 0 } ? "No session profile hook yet (added in 1.22): run oml hooks install again."
                : entry is not null && Entries(root, t, stop: true)?.Any(Ours) != true ? "No memory reminder yet (added in 1.17): run oml hooks install again." : null,
        };
    }

    static object Install(Target t, string file, JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) root["hooks"] = hooks = [];
        var replaced = false;
        foreach (var (eventName, matcher, marker) in new[] { (t.Event, t.Matcher, Marker), (t.StopEvent, "", StopMarker), (t.SessionEvent, "", SessionMarker) })
        {
            if (hooks[eventName] is not JsonArray list) hooks[eventName] = list = [];
            foreach (var old in list.Where(Ours).ToList()) { list.Remove(old); replaced = true; }
            list.Add(new JsonObject
            {
                // The end-of-turn events take no matcher, but Cursor skips a Claude Code hook entry that has none.
                ["matcher"] = matcher,
                // Claude Code and Codex count the timeout in seconds, Gemini CLI in milliseconds.
                ["hooks"] = new JsonArray(HookCommand(t, marker)),
            });
        }
        Save(file, root);
        return new
        {
            agent = t.Agent, @for = t.For, file, installed = true, memoryReminder = true, sessionProfile = true, replaced,
            note = NeedsQuotes && t.Agent == "claude" ? "The path of oml has a space or a special character, so the hook command is quoted. GitHub Copilot on Windows reads the powershell line beside it; if its tool calls are denied with 'hook errored', put oml in a folder without spaces and install again." : null,
        };
    }

    static JsonObject HookCommand(Target t, string marker)
    {
        var hook = new JsonObject { ["type"] = "command", ["command"] = Command(marker), ["timeout"] = t.Agent == "gemini" ? 10000 : 10 };
        // Copilot takes `powershell` over `command` on Windows; the call operator makes a quoted path a command there.
        if (NeedsQuotes && t.Agent == "claude" && OperatingSystem.IsWindows()) hook["powershell"] = $"& \"{Exe}\"" + marker;
        return hook;
    }

    static object Remove(Target t, string file, JsonObject root)
    {
        var removed = false;
        foreach (var eventName in new[] { t.Event, t.StopEvent, t.SessionEvent })
            if (root["hooks"]?[eventName] is JsonArray list)
            {
                foreach (var old in list.Where(Ours).ToList()) { list.Remove(old); removed = true; }
                if (list.Count == 0) (root["hooks"] as JsonObject)!.Remove(eventName);
            }
        if (root["hooks"] is JsonObject { Count: 0 }) root.Remove("hooks");
        // A file that held only oml's hook goes; a file with the user's own settings stays.
        if (removed && root.Count == 0) File.Delete(file);
        else if (removed) Save(file, root);
        return new { agent = t.Agent, file, removed };
    }
}