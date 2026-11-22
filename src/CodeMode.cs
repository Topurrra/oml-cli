using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// oml code: code mode for oml. A thin launcher for the code-mode runtime that ships in codemode/ (a copy of the
// author's standalone code-mode project). It finds Python 3.9+, writes the config (oml's own connector and
// policy, with the project's .oml/codemode.json merged over it) and runs
//   python -I -X utf8 <codemode>/oml_code.py --config <project>/.oml/codemode/config.json --home <project>/.oml/codemode <args>
// with stdin, stdout and stderr inherited, so `oml code serve` is a plain stdio MCP server, and the exit code passed back.
internal static class CodeMode
{
    internal const string InstallHint = "Install Python 3.9 or newer (winget install Python.Python.3.12, or python.org), or set OML_PYTHON to the full path of python.exe.";

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            TerminalOutput.PrintHelp(CliUsage.For("code") + Environment.NewLine + "One subcommand in detail: oml code <subcommand> --help. Guide: oml doc commands#code-mode", "code");
            return 0;
        }
        var folder = Folder();
        if (folder is null)
            return Fail("CodeModeMissing", $"The codemode folder that ships with oml was not found next to {AppContext.BaseDirectory} or above it.",
                "Unzip the oml release again and keep its folders together (bin, codemode, skill).");
        var python = FindPython(out var problem);
        if (python is null) return Fail("PythonNotFound", problem, InstallHint);
        var root = ProjectRoot(Environment.CurrentDirectory);
        var home = Path.Combine(root, ".oml", "codemode");
        string config;
        // init, demo and --help read no config, so they also run while there is no connector yet.
        var needsConnector = args.FirstOrDefault(a => !a.StartsWith('-')) is not ("init" or "demo") && !args.Contains("--help") && !args.Contains("-h");
        try { config = WriteConfig(root, home, folder, needsConnector, out var note); if (note is not null) Console.Error.WriteLine("oml code: " + note); }
        catch (CodeModeConfigError e) { return Fail("ConfigError", e.Message, e.Hint); }

        var start = new ProcessStartInfo(python) { UseShellExecute = false };
        foreach (var a in new[] { "-I", "-X", "utf8", Path.Combine(folder, "oml_code.py"), "--config", config, "--home", home }) start.ArgumentList.Add(a);
        foreach (var a in args) start.ArgumentList.Add(a);
        start.Environment["OML_EXE"] = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "oml.exe");
        start.Environment["OML_CODE_PROJECT"] = root;
        // Ctrl+C reaches Python too, which stops its program and exits 130; oml waits for that exit code.
        ConsoleCancelEventHandler keep = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += keep;
        try
        {
            using var process = Process.Start(start)!;
            process.WaitForExit();
            return process.ExitCode;
        }
        finally { Console.CancelKeyPress -= keep; }
    }

    // The arguments after the command word, as typed: --json goes on to the runtime; oml's --pretty before it does not.
    internal static string[] AfterCommand(string[] args)
    {
        var at = Array.IndexOf(args, "code");
        return args.Take(at).Where(a => a != "--pretty").Concat(args.Skip(at + 1)).ToArray();
    }

    // The same shape as the runtime's own errors with --json, so a caller reads one kind of answer from oml code.
    static int Fail(string type, string message, string hint)
    {
        TerminalOutput.PrintNode(new JsonObject { ["status"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = message, ["hint"] = hint } });
        return 2;
    }

    // codemode/ beside bin/ in a release, or the repository's codemode/ for a build from source.
    internal static string? Folder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "codemode");
            if (File.Exists(Path.Combine(candidate, "oml_code.py")) && File.Exists(Path.Combine(candidate, "codemode_runtime", "cli.py"))) return candidate;
        }
        return null;
    }

    // OML_PYTHON, else `py -3`, else python and python3 on PATH; the first that is Python 3.9 or newer. Returns the
    // interpreter's own path (sys.executable), so the runtime starts its sandbox with the same one.
    internal static string? FindPython(out string problem)
    {
        var tried = new List<string>();
        var chosen = Environment.GetEnvironmentVariable("OML_PYTHON");
        var candidates = string.IsNullOrWhiteSpace(chosen)
            ? new (string Exe, string[] Prefix, string Label)[] { ("py", ["-3"], "py -3"), ("python", [], "python"), ("python3", [], "python3") }
            : [(chosen.Trim().Trim('"'), [], "OML_PYTHON=" + chosen.Trim())];
        foreach (var (exe, prefix, label) in candidates)
        {
            var (found, version, why) = Probe(exe, prefix);
            if (found is not null) { problem = ""; return found; }
            tried.Add($"{label}: {(version is null ? why : $"Python {version}, too old")}");
        }
        problem = (string.IsNullOrWhiteSpace(chosen) ? "oml code needs Python 3.9 or newer and found none. " : "OML_PYTHON does not name Python 3.9 or newer. ")
            + "Tried " + string.Join("; ", tried) + ".";
        return null;
    }

    static (string? Exe, string? Version, string Why) Probe(string exe, string[] prefix)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in prefix) start.ArgumentList.Add(a);
        foreach (var a in new[] { "-I", "-c", "import sys; print('%d.%d.%d' % sys.version_info[:3]); print(sys.executable)" }) start.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(start)!;
            var output = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return (null, null, "no answer in 15 s"); }
            var lines = output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (p.ExitCode != 0 || lines.Length < 2 || !Version.TryParse(lines[0], out var version)) return (null, null, $"not a working Python (exit {p.ExitCode})");
            return version >= new Version(3, 9) && File.Exists(lines[1]) ? (lines[1], lines[0], "") : (null, lines[0], "");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (null, null, File.Exists(exe) || !Path.IsPathRooted(exe) ? "not found" : $"{exe} does not exist");
        }
    }

    // The project root: the nearest folder from here upwards with an oml project profile or a .oml/codemode.json;
    // else the current folder.
    internal static string ProjectRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            foreach (var name in new[] { "oml-config.json", "config.json", "codemode.json" })
                if (File.Exists(Path.Combine(dir.FullName, ".oml", name))) return dir.FullName;
        return start;
    }

    // Is there an `oml mcp` server in this oml? The command table or the usage text says so.
    internal static bool McpAvailable() =>
        CommandTable.All.Any(c => c.Name == "mcp") || CliUsage.Text.Split('\n').Select(l => l.Trim()).Any(l => l == "oml mcp" || l.StartsWith("oml mcp "));

    static readonly string[] TopKeys = ["connectors", "policy", "approval", "limits", "executor", "llm", "agent", "helpers", "retention", "work_dir", "guidance"];
    static readonly string[] HostVariables = ["ProgramFiles(x86)", "CommonProgramFiles", "CommonProgramFiles(x86)", "ProgramW6432", "ALLUSERSPROFILE",
        "PUBLIC", "HOMEDRIVE", "HOMEPATH", "COMPUTERNAME", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS"];
    static readonly Regex SecretName = new("TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIAL|AUTH|COOKIE|BEARER|API[_-]?KEY|PRIVATE[_-]?KEY|ACCESS[_-]?KEY", RegexOptions.IgnoreCase);
    static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$");

    // The runtime's config: oml's defaults with .oml/codemode.json merged over them (rules in
    // skill/reference/commands.md, Code mode). Written only when it changed. Returns its path.
    internal static string WriteConfig(string root, string home, string folder, bool needsConnector, out string? note)
    {
        note = null;
        var config = Defaults(root, folder, McpAvailable());
        var own = Path.Combine(root, ".oml", "codemode.json");
        if (File.Exists(own)) Merge(config, ReadProjectConfig(own));
        var connectors = config["connectors"]!.AsObject();
        if (connectors["oml"] is JsonObject oml && oml["command"] is null)
        {
            connectors.Remove("oml");
            note = "this oml has no `oml mcp` server yet, so the oml connector is left out. Give its command in .oml/codemode.json ({\"connectors\": {\"oml\": {\"command\": [...]}}}) or update oml.";
        }
        if (connectors.Count == 0 && needsConnector)
            throw new CodeModeConfigError(note ?? ".oml/codemode.json removes every connector.", "Add a connector in .oml/codemode.json, or update oml to a build with `oml mcp`.");
        var policy = config["policy"]!.AsObject();
        var replace = policy["replace"] is JsonValue flag && flag.TryGetValue<bool>(out var yes) && yes;
        if (connectors["oml"] is not null && !replace)
        {
            Prepend(policy, "allow", "oml.read", "oml.write");
            Prepend(policy, "ask", "oml.gated");
        }
        policy.Remove("replace");
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "config.json");
        var text = config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (!File.Exists(path) || File.ReadAllText(path) != text)
        {
            var temp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        return path;
    }

    static JsonObject Defaults(string root, string folder, bool mcp)
    {
        // The runtime starts a server with a short list of host variables. oml needs its own settings (OML_*: Service
        // Studio's folder and port, the platform server, the usage log), .NET's (DOTNET_*) and the Windows folders.
        // A variable is passed as a ${NAME} reference: its value is read when the server starts and never written here.
        var env = new JsonObject { ["OML_EXE"] = "${OML_EXE}" };
        var secrets = new JsonArray();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = (string)entry.Key;
            var upper = name.ToUpperInvariant();
            // OML_OUTPUT=human would turn the JSON a program reads into text for people.
            var own = (upper.StartsWith("OML_") && upper is not "OML_OUTPUT" and not "OML_EXE") || upper.StartsWith("DOTNET_");
            if (!own && !HostVariables.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            env[Identifier.IsMatch(name) ? upper : name] = Identifier.IsMatch(name) ? "${" + upper + "}" : (string?)entry.Value;
            if (SecretName.IsMatch(name)) secrets.Add(upper);
        }
        var oml = new JsonObject { ["type"] = "mcp-stdio" };
        if (mcp) oml["command"] = new JsonArray(Slash(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "oml.exe")), "mcp");
        oml["cwd"] = Slash(root);
        oml["env"] = env;
        if (secrets.Count > 0) oml["secrets"] = secrets;
        oml["startup_timeout_s"] = 60;
        oml["max_concurrency"] = 4;
        return new JsonObject
        {
            ["connectors"] = new JsonObject { ["oml"] = oml },
            ["policy"] = new JsonObject { ["mode"] = "enforce", ["default"] = "ask", ["allow"] = new JsonArray(), ["ask"] = new JsonArray(), ["deny"] = new JsonArray() },
            ["limits"] = new JsonObject { ["timeout_s"] = 1800, ["call_timeout_s"] = 300, ["call_timeouts"] = new JsonObject { ["oml.gated"] = 1800 }, ["startup_timeout_s"] = 60 },
            ["helpers"] = Slash(Path.Combine(folder, "oml_helpers")),
            ["work_dir"] = ".oml/code",
        };
    }

    static JsonObject ReadProjectConfig(string path)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { throw new CodeModeConfigError($"{path} is not valid JSON: {e.Message}", "Fix the file, or remove it to use oml's defaults."); }
        catch (IOException e) { throw new CodeModeConfigError($"{path} cannot be read: {e.Message}", "Close the program that holds it and try again."); }
        if (node is not JsonObject obj) throw new CodeModeConfigError($"{path} must hold a JSON object.", "For example {\"connectors\": {}, \"policy\": {\"allow\": []}}.");
        var unknown = obj.Select(p => p.Key).Where(k => !TopKeys.Contains(k)).ToList();
        if (unknown.Count > 0) throw new CodeModeConfigError($"{path}: unknown key(s) {string.Join(", ", unknown)}.", "Valid keys: " + string.Join(", ", TopKeys) + ".");
        return obj;
    }

    // connectors: by name; a connector of the same name is merged key by key (yours win), null removes it.
    // policy: allow, ask and deny add to oml's rules; mode and default replace; "replace": true drops oml's rules.
    // limits, approval, retention, executor, llm: key by key. helpers, agent, work_dir and guidance: replaced.
    internal static void Merge(JsonObject config, JsonObject own)
    {
        foreach (var (key, value) in own.ToList())
        {
            var mine = value?.DeepClone();
            if (key == "connectors" && mine is JsonObject connectors)
            {
                var target = config["connectors"]!.AsObject();
                foreach (var (name, spec) in connectors.ToList())
                {
                    connectors.Remove(name);
                    if (spec is null) target.Remove(name);
                    else if (target[name] is JsonObject existing && spec is JsonObject add) foreach (var (k, v) in add.ToList()) { add.Remove(k); existing[k] = v; }
                    else target[name] = spec;
                }
            }
            else if (key == "policy" && mine is JsonObject policy)
            {
                var target = config["policy"]!.AsObject();
                if (policy["replace"] is JsonValue r && r.TryGetValue<bool>(out var replace) && replace)
                    foreach (var list in new[] { "allow", "ask", "deny" }) target[list] = new JsonArray();
                foreach (var (k, v) in policy.ToList())
                {
                    policy.Remove(k);
                    if (k is "allow" or "ask" or "deny" && v is JsonArray rules && target[k] is JsonArray existing)
                        foreach (var rule in rules.ToList()) { rules.Remove(rule); if (!existing.Any(e => JsonNode.DeepEquals(e, rule))) existing.Add(rule); }
                    else target[k] = v;
                }
            }
            else if (key is "limits" or "approval" or "retention" or "executor" or "llm" && mine is JsonObject section && config[key] is JsonObject existing)
                foreach (var (k, v) in section.ToList()) { section.Remove(k); existing[k] = v; }
            else config[key] = mine;
        }
    }

    // oml's own rules go first, so a person reads them first in `oml code config`.
    static void Prepend(JsonObject policy, string list, params string[] rules)
    {
        var existing = policy[list] as JsonArray ?? new JsonArray();
        var merged = new JsonArray(rules.Select(r => (JsonNode?)r).ToArray());
        foreach (var rule in existing.ToList())
        {
            existing.Remove(rule);
            if (!(rule is JsonValue v && v.TryGetValue<string>(out var text) && rules.Contains(text))) merged.Add(rule);
        }
        policy[list] = merged;
    }

    static string Slash(string path) => path.Replace('\\', '/');
}

internal sealed class CodeModeConfigError(string message, string hint) : Exception(message)
{
    public string Hint { get; } = hint;
}
