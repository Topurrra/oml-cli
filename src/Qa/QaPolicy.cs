using Microsoft.Win32;

namespace OmlCli.Qa;

// Whether a browser policy turns DevTools off. Company PCs often set RemoteDebuggingAllowed=0 (machine or user policy)
// for Edge and Chrome; the browser then refuses --remote-debugging-port, and oml qa uses UI Automation instead.
// oml only reads the policy, never changes it.
static class QaPolicy
{
    public static string? BlocksDevTools()
    {
        if (!OperatingSystem.IsWindows() || Locations.BrowserPath() is not { } exe) return null;
        var (key, name) = exe.Contains("Edge", StringComparison.OrdinalIgnoreCase)
            ? (@"SOFTWARE\Policies\Microsoft\Edge", "Edge") : (@"SOFTWARE\Policies\Google\Chrome", "Chrome");
        foreach (var (hive, label) in new[] { (Registry.LocalMachine, "HKLM"), (Registry.CurrentUser, "HKCU") })
        {
            using var policies = hive.OpenSubKey(key);
            if (policies?.GetValue("RemoteDebuggingAllowed") is 0)
                return $"{name} remote debugging is turned off by policy ({label}\\{key}\\RemoteDebuggingAllowed = 0)";
        }
        return null;
    }
}
