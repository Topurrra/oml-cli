using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace OmlCli.Qa;

// Left clicks for `oml qa record` with the UI Automation engine. A low-level mouse hook sees every click on the
// desktop; it only queues the point, and the recorder keeps the ones inside its own browser window (WindowOf). The
// hook needs a thread with a message loop, so it runs on one of its own until Dispose.
sealed class MouseHook : IDisposable
{
    const int WhMouseLl = 14, WmLButtonDown = 0x0201, WmQuit = 0x0012, GaRoot = 2;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MouseInfo { public Point Pt; public int MouseData, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct Msg { public IntPtr Hwnd; public int Message; public IntPtr W, L; public int Time; public Point Pt; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, int thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out Msg msg, IntPtr hwnd, int min, int max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(int thread, int msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern int GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point pt);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    readonly BlockingCollection<(int X, int Y)> clicks = [];
    readonly ManualResetEventSlim ready = new();
    readonly HookProc proc;
    readonly Thread thread;
    int threadId;
    string? error;

    public MouseHook()
    {
        proc = OnMouse; // kept in a field: the native hook calls it until Dispose
        thread = new Thread(Loop) { IsBackground = true, Name = "oml qa mouse hook" };
        thread.Start();
        ready.Wait();
        if (error is not null) throw new QaUsage(error);
    }

    // Hook points are per-monitor aware; UI Automation answers in the same coordinates only when this process is too.
    public static void PerMonitorDpi() => SetProcessDpiAwarenessContext(new IntPtr(-4));

    void Loop()
    {
        threadId = GetCurrentThreadId();
        var hook = SetWindowsHookEx(WhMouseLl, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) error = $"Could not watch the mouse (error {Marshal.GetLastWin32Error()}).";
        ready.Set();
        if (hook == IntPtr.Zero) return;
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        UnhookWindowsHookEx(hook);
    }

    // Must return fast: Windows drops a hook that keeps the mouse waiting.
    IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == WmLButtonDown)
        {
            var info = Marshal.PtrToStructure<MouseInfo>(lParam);
            clicks.Add((info.Pt.X, info.Pt.Y));
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public bool Next(out (int X, int Y) point, int waitMs) => clicks.TryTake(out point, waitMs);

    // The top-level window under a screen point.
    public static IntPtr WindowOf(int x, int y) => GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), GaRoot);

    public void Dispose()
    {
        PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        thread.Join(2_000);
        clicks.Dispose();
    }
}
