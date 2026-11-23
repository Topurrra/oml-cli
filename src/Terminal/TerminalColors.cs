using System.Runtime.InteropServices;

namespace OmlCli;

// ANSI virtual-terminal colors. Enabled only when the console supports them: the output is a console, the terminal
// is not dumb, and NO_COLOR is not set. Everything stays readable without color: color marks, never carries meaning.
internal static class TerminalColors
{
    public static bool Enabled = Detect();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int nStdHandle);
    const int StdOutput = -11;
    [DllImport("kernel32.dll")]
    static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")]
    static extern bool SetConsoleMode(IntPtr handle, uint mode);
    const uint EnableVirtualTerminalProcessing = 0x0004;

    static bool Detect()
    {
        if (Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 }) return false;
        var setting = ColorSetting();
        if (setting == "never") return false;
        if (setting == "always") { if (OperatingSystem.IsWindows()) TurnOnVirtualTerminal(); return true; }
        if (Environment.GetEnvironmentVariable("TERM") == "dumb") return false;
        if (Console.IsOutputRedirected) return false; // a pipe or a file never gets ANSI
        if (!OperatingSystem.IsWindows()) return Environment.GetEnvironmentVariable("TERM") is { Length: > 0 };
        return TurnOnVirtualTerminal();
    }

    static bool TurnOnVirtualTerminal()
    {
        try
        {
            var handle = GetStdHandle(StdOutput);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
            if (!GetConsoleMode(handle, out var mode)) return false;
            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch { return false; }
    }

    // output.color of the project config (.oml/oml-config.json in this folder or above): auto, always or never. Read
    // here, not through ProjectConfig, because this file is also built into Core.
    static string ColorSetting()
    {
        try
        {
            for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
            {
                var file = Path.Combine(dir.FullName, ".oml", "oml-config.json");
                if (!File.Exists(file)) continue;
                var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file), documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return (root?["output"]?["color"] ?? root?["output.color"])?.ToString() ?? "auto";
            }
        }
        catch (Exception) { }
        return "auto";
    }

    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Dim = "\x1b[2m";
    public const string Red = "\x1b[31m";
    public const string Green = "\x1b[32m";
    public const string Yellow = "\x1b[33m";
    public const string Blue = "\x1b[34m";
    public const string Magenta = "\x1b[35m";
    public const string Cyan = "\x1b[36m";

    public static string Paint(string text, string ansi) => Enabled ? ansi + text + Reset : text;
    public static string BoldText(string text) => Enabled ? Bold + text + Reset : text;
}
