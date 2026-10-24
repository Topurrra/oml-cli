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
        if (Environment.GetEnvironmentVariable("TERM") == "dumb") return false;
        if (Console.IsOutputRedirected) return false; // a pipe or a file never gets ANSI
        if (!OperatingSystem.IsWindows()) return Environment.GetEnvironmentVariable("TERM") is { Length: > 0 };
        try
        {
            var handle = GetStdHandle(StdOutput);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
            if (!GetConsoleMode(handle, out var mode)) return false;
            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch { return false; }
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
