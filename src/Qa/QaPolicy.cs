using Microsoft.Win32;

namespace OmlCli.Qa;

// Whether a browser policy turns DevTools off. Company PCs often set RemoteDebuggingAllowed=0 (machine or user policy)
// for Edge and Chrome; the browser then refuses --remote-debugging-port, and oml browser uses UI Automation instead.
// oml only reads the policy, never changes it.
static class QaPolicy
{
    public static string? BlocksDevTools() => Locations.BrowserPath() is { } exe ? BlocksDevTools(exe) : null;

    public static string? BlocksDevTools(string exe) => Locations.DevToolsBlockedBy(exe);
}
