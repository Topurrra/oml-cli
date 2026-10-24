using System.Text.Json.Nodes;

namespace OmlCli;

// Decides between JSON (today's machine output) and the human terminal view, and owns the shared state.
// The human view runs only when ALL of these hold: stdout is a console (not redirected), the command is not inside
// `oml run`, and nobody asked for JSON. Otherwise the exact JSON of today is printed, byte for byte.
// `--json` forces JSON; OML_OUTPUT=json|human overrides the default either way.
internal static class TerminalOutput
{
    // True while a step of `oml run`, a dashboard call or another captured run is executing: always JSON there.
    public static bool InRun;

    // The command being run, when known: the renderer uses it for tailored views (validate, apply, check, ...).
    public static string? Command;

    // Set by --json or OML_OUTPUT=json. ForceHuman caches OML_OUTPUT=human.
    internal static bool ForceJson;
    internal static bool? ForceHuman;

    public static bool HumanMode()
    {
        if (JsonOutput.Pretty) return false; // --pretty asks for JSON, exactly as today
        if (ForceJson) return false;
        if (InRun) return false;
        if (ForceHuman == true) return true;
        var env = Environment.GetEnvironmentVariable("OML_OUTPUT");
        if (env == "json") return false;
        if (env == "human") { ForceHuman = true; return true; }
        return !Console.IsOutputRedirected;
    }

    // The text to write to stdout: the exact JSON, or its human rendering (JSON fallback on any render error).
    public static string Render(string json)
    {
        if (!HumanMode()) return json;
        try { return TerminalRender.Human(json); }
        catch { return json; }
    }

    public static void PrintObject(object value) => Console.WriteLine(Render(JsonOutput.Serialize(value)));
    public static void PrintNode(JsonNode node) => Console.WriteLine(Render(JsonOutput.Write(node)));
    public static void PrintText(string json) => Console.WriteLine(Render(json));

    // Machine output no matter what: the `oml run` envelope and debug dumps keep their JSON shape.
    public static void PrintNodeRaw(JsonNode node) => Console.WriteLine(JsonOutput.Write(node));

    // `oml --help` / `oml <command> --help`: grouped by area in the human view, the exact text otherwise.
    public static void PrintHelp(string usage, string? command)
    {
        if (!HumanMode()) { Console.WriteLine(usage); return; }
        Console.WriteLine(TerminalRender.Help(usage, command));
    }

    // Whether `--json` in these arguments is the global force-JSON flag. `check` and `architecture` take `--json`
    // <file> as their own report path; only a standalone `--json` there is the flag.
    public static bool ForceJsonFlag(string[] args)
    {
        var cmd = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "";
        var takesValue = cmd is "check" or "architecture";
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--json" && (!takesValue || i + 1 >= args.Length || args[i + 1].StartsWith("--"))) return true;
        return false;
    }

    // Arguments without the force-JSON `--json` (kept for check/architecture when it has a value).
    public static string[] StripJson(string[] args)
    {
        var cmd = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "";
        var takesValue = cmd is "check" or "architecture";
        return args.Where((a, i) => a != "--json" || (takesValue && i + 1 < args.Length && !args[i + 1].StartsWith("--"))).ToArray();
    }
}

// Progress on stderr for slow commands, only when a person watches a console: an agent's or script's redirected output
// stays clean. `oml library fetch` already prints its own bar; the commands that use this one do the same.
internal static class TerminalProgress
{
    public static bool Active => !TerminalOutput.InRun && !Console.IsErrorRedirected;

    public static void Message(string text)
    {
        if (Active) Console.Error.WriteLine(text);
    }

    // "Label: 3/12 ..." redrawn on each Advance, a line end on Dispose. No output when not active.
    internal sealed class Step(string label, int total = 0) : IDisposable
    {
        readonly string label = label;
        readonly int total = total;
        int done;
        readonly bool shown = Active;
        bool first = true;

        public void Advance()
        {
            if (!shown) return;
            done++;
            if (first) { first = false; Console.Error.Write(label); }
            Console.Error.Write(total > 0 ? $"\r{label}: {done}/{total} " : "\r" + label + ": " + done + " ");
        }

        public void Dispose()
        {
            if (shown) Console.Error.WriteLine();
        }
    }
}
