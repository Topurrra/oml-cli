namespace OmlCli;

// What oml knows about the console of the command's caller: whether stdout and stderr go to a console window, how wide
// it is and whether it shows Unicode. A normal oml process reads its own console. A warm worker (WarmWorker) runs the
// command of another oml process and has no console of its own that matters, so it sets the caller's facts here before
// each command; the human view, colors, progress lines and --pretty then come out exactly as in the caller.
// Compiled into Core as well (the Terminal folder is linked), so the worker sets both copies.
internal static class ConsoleFacts
{
    static bool? outputRedirected, errorRedirected, unicode;
    static int? width;

    public static bool OutputRedirected => outputRedirected ?? Console.IsOutputRedirected;
    public static bool ErrorRedirected => errorRedirected ?? Console.IsErrorRedirected;
    // The width the human view uses: the console's (fallback 100), or the caller's.
    public static int Width => width ?? OwnWidth();
    public static bool? Unicode => unicode;

    public static int OwnWidth()
    {
        try { return Console.WindowWidth > 40 ? Console.WindowWidth : 100; }
        catch { return 100; }
    }

    public static bool OwnUnicode()
    {
        try { return !Console.OutputEncoding.IsSingleByte; }
        catch { return true; }
    }

    public static void Set(bool stdoutRedirected, bool stderrRedirected, int columns, bool showsUnicode)
    {
        (outputRedirected, errorRedirected, width, unicode) = (stdoutRedirected, stderrRedirected, columns, showsUnicode);
    }
}
