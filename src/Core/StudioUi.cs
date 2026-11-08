using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// Service Studio's window through UI Automation: `oml studio tabs`, `close`, `focus` and `oml publish <file> --close`.
// Service Studio's MCP server has no tool for this. The automation runs in Windows PowerShell, which is part of Windows,
// so oml needs no new dependency. A tab's label is a Text element inside the tab's bounds ("* MCP_Core (OUIAI)": "* " means
// unsaved, the part in brackets is the application). The decisions are pure (StudioTabs), so the self-test runs them.
static class StudioTabs
{
    internal sealed record Tab(long Window, int Index, string? Label);

    // "* MCP_API (OUIAI)" -> ("MCP_API", "OUIAI", unsaved: true).
    internal static (string module, string? app, bool unsaved) ParseLabel(string label)
    {
        var text = label.Trim();
        var unsaved = text.StartsWith("* ", StringComparison.Ordinal);
        if (unsaved) text = text[2..].Trim();
        var bracket = Regex.Match(text, @"^(.+?)\s+\(([^()]+)\)$");
        return bracket.Success ? (bracket.Groups[1].Value, bracket.Groups[2].Value, unsaved) : (text, null, unsaved);
    }

    // The one tab of a module, or why oml will not touch any tab. The match is exact: a near match is another module.
    internal static (Tab? tab, string? refusal) Pick(IReadOnlyList<Tab> tabs, string module)
    {
        var hits = tabs.Where(t => t.Label is not null && ParseLabel(t.Label).module == module).ToList();
        if (hits.Count > 1) return (null, $"{hits.Count} tabs are labelled '{module}'. oml cannot tell them apart, so it closed nothing. Close the one you mean in Service Studio.");
        if (hits.Count == 0) return (null, $"No tab has the label '{module}' (labels read: {string.Join(", ", tabs.Select(t => t.Label ?? "(none)"))}). oml closed nothing.");
        return ParseLabel(hits[0].Label!).unsaved
            ? (null, $"'{module}' has unsaved changes (its tab is marked \"* \"). Closing it shows a save prompt that oml cannot answer, so oml closed nothing. Save or publish it in Service Studio first.")
            : (hits[0], null);
    }

    // After the close: the module must be gone and every other module still open.
    internal static string? CheckAfter(string module, IReadOnlyCollection<string> before, IReadOnlyCollection<string> after)
    {
        var lost = before.Where(n => n != module && !after.Contains(n)).ToList();
        if (lost.Count > 0) return $"Other modules also closed: {string.Join(", ", lost)}. Open them again in Service Studio.";
        return after.Contains(module) ? $"'{module}' is still open in Service Studio. Look at Service Studio: it may show a message." : null;
    }

    internal static List<Tab> Read(JsonNode? scan) =>
        (scan?["windows"] as JsonArray ?? new()).SelectMany(w => (w?["tabs"] as JsonArray ?? new()).Select(t =>
            new Tab(w!["window"]!.GetValue<long>(), t!["index"]!.GetValue<int>(), t["label"]?.GetValue<string>()))).ToList();
}

static class StudioUi
{
    public const string OpenBlocked = "oml cannot open a module from the server. Service Studio's MCP server has no open tool, and the environment page's Recent modules list opens a module by clicks only. " +
        "Open the module in Service Studio, or pass its .oml file to oml publish. A new module is made with Add Module on the application page.";

    const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        Add-Type 'using System; using System.Runtime.InteropServices; public static class OmlWin { [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c); [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h); [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); }'
        $request = $env:OML_UI_REQUEST | ConvertFrom-Json
        $Root = [System.Windows.Automation.AutomationElement]::RootElement
        $Scope = [System.Windows.Automation.TreeScope]; $All = [System.Windows.Automation.Condition]::TrueCondition
        function Kind($e) { $e.Current.ControlType.ProgrammaticName }
        function Scan($w) {
            $items = @($w.FindAll($Scope::Descendants, $All))
            $tabs = @($items | Where-Object { (Kind $_) -eq 'ControlType.TabItem' -and $_.Current.Name -like '*AggregatorView' } | Sort-Object { $_.Current.BoundingRectangle.X })
            $texts = @($items | Where-Object { (Kind $_) -eq 'ControlType.Text' })
            $buttons = @($items | Where-Object { (Kind $_) -eq 'ControlType.Button' -and $_.Current.Name -like '*Shapes.Path' })
            $rows = @(); $i = 0
            foreach ($t in $tabs) {
                $r = $t.Current.BoundingRectangle
                $inside = @($texts | Where-Object { $b = $_.Current.BoundingRectangle; $b.Width -gt 0 -and $b.X -ge $r.X -and $b.Right -le ($r.Right + 1) -and $b.Y -ge ($r.Y - 1) -and $b.Bottom -le ($r.Bottom + 1) })
                $close = @($buttons | Where-Object { $b = $_.Current.BoundingRectangle; $b.X -ge $r.X -and $b.X -lt $r.Right })
                $label = $null; if ($inside.Count -eq 1) { $label = $inside[0].Current.Name }
                $rows += [pscustomobject]@{ index = $i; label = $label; element = $t; close = $close }
                $i++
            }
            return ,$rows
        }
        function Windows {
            $found = @()
            foreach ($w in @($Root.FindAll($Scope::Children, $All) | Where-Object { $_.Current.Name -eq 'Service Studio' })) {
                $h = [IntPtr]$w.Current.NativeWindowHandle
                $restored = $false
                $rows = Scan $w
                if ($rows.Count -gt 0 -and @($rows | Where-Object { $_.label }).Count -eq 0 -and [OmlWin]::IsIconic($h)) {
                    [void][OmlWin]::ShowWindow($h, 4); $restored = $true; Start-Sleep -Milliseconds 400; $rows = Scan $w
                }
                if ($rows.Count -gt 0) { $found += [pscustomobject]@{ window = $w; handle = [long]$h; rows = $rows; restored = $restored } }
                elseif ($restored) { [void][OmlWin]::ShowWindow($h, 7) }
            }
            return ,$found
        }
        function Done($value) { $value | ConvertTo-Json -Compress -Depth 6; exit 0 }
        function Hide($w) { if ($w.restored) { [void][OmlWin]::ShowWindow([IntPtr]$w.handle, 7) } }
        $windows = Windows
        switch ($request.action) {
            'tabs' {
                $out = @($windows | ForEach-Object { [pscustomobject]@{ window = $_.handle; tabs = @($_.rows | ForEach-Object { [pscustomobject]@{ index = $_.index; label = $_.label } }) } })
                $windows | ForEach-Object { Hide $_ }
                Done ([pscustomobject]@{ windows = $out })
            }
            'close' {
                $w = @($windows | Where-Object { $_.handle -eq [long]$request.window })[0]
                if (-not $w) { Done ([pscustomobject]@{ closed = $false; error = 'the Service Studio window is gone' }) }
                $hits = @($w.rows | Where-Object { $_.label -ceq $request.label })
                if ($hits.Count -ne 1) { Hide $w; Done ([pscustomobject]@{ closed = $false; error = 'the tab label changed before the close' }) }
                if ($hits[0].close.Count -ne 1) { Hide $w; Done ([pscustomobject]@{ closed = $false; error = "found $($hits[0].close.Count) close buttons on the tab" }) }
                $hits[0].close[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Hide $w
                Done ([pscustomobject]@{ closed = $true })
            }
            'focus' {
                $w = @($windows)[0]
                if (-not $w) { Done ([pscustomobject]@{ focused = $false; error = 'no Service Studio window has module tabs' }) }
                $h = [IntPtr]$w.handle
                if ([OmlWin]::IsIconic($h)) { [void][OmlWin]::ShowWindow($h, 9) }
                [void][OmlWin]::SetForegroundWindow($h)
                Start-Sleep -Milliseconds 200
                Done ([pscustomobject]@{ focused = ([OmlWin]::GetForegroundWindow() -eq $h); window = $w.handle })
            }
        }
        """;

    static JsonNode Run(object request)
    {
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(shell)) throw new CliError($"Windows PowerShell was not found at {shell}. Do this in Service Studio.", 2);
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(Script));
        var start = new ProcessStartInfo(shell, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        start.Environment["OML_UI_REQUEST"] = JsonSerializer.Serialize(request);
        using var process = Process.Start(start) ?? throw new CliError("Windows PowerShell did not start.", 2);
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(60000))
        {
            process.Kill(true);
            throw new CliError("Reading Service Studio's window took more than 60 seconds. oml changed nothing it can report; check Service Studio.", 2);
        }
        var text = output.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0 || text.Length == 0)
            throw new CliError($"UI Automation of Service Studio failed: {error.GetAwaiter().GetResult().Trim()}", 2);
        return JsonNode.Parse(text) ?? throw new CliError("UI Automation of Service Studio returned nothing.", 2);
    }

    public static object Tabs()
    {
        var scan = Run(new { action = "tabs" });
        var tabs = StudioTabs.Read(scan);
        if (tabs.Count == 0) throw new CliError("No Service Studio window with module tabs was found.", 2);
        return new
        {
            tabs = tabs.Select(t =>
            {
                var (module, app, unsaved) = t.Label is null ? ("", null, false) : StudioTabs.ParseLabel(t.Label);
                return new { index = t.Index, label = t.Label, module, application = app, unsaved };
            }).ToArray(),
            windows = tabs.Select(t => t.Window).Distinct().Count(),
        };
    }

    public static int Focus()
    {
        var r = Run(new { action = "focus" });
        var focused = r["focused"]?.GetValue<bool>() == true;
        Cli.Emit(new { focused, window = r["window"]?.GetValue<long>(), error = r["error"]?.GetValue<string>(),
            note = focused ? null : "Windows may refuse to bring a window to the front from a background process. Click Service Studio's taskbar button." });
        return focused ? 0 : 1;
    }

    // Closes a module's tab. Throws CliError (exit 2) when oml will not touch a tab, or when the close did not do exactly that.
    public static void Close(string module, IReadOnlyCollection<string> openBefore, Func<IReadOnlyCollection<string>> openNow)
    {
        var tabs = StudioTabs.Read(Run(new { action = "tabs" }));
        if (tabs.Count == 0) throw new CliError("No Service Studio window with module tabs was found, so oml closed nothing.", 2);
        var (tab, refusal) = StudioTabs.Pick(tabs, module);
        if (refusal is not null) throw new CliError(refusal, 2);
        var result = Run(new { action = "close", window = tab!.Window, label = tab.Label });
        if (result["closed"]?.GetValue<bool>() != true) throw new CliError($"oml did not close '{module}': {result["error"]?.GetValue<string>()}.", 2);
        string? problem = null;
        for (var waited = 0; waited < 30; waited++)
        {
            Thread.Sleep(1000);
            problem = StudioTabs.CheckAfter(module, openBefore, openNow());
            if (problem is null) return;
        }
        throw new CliError(problem!, 2);
    }
}
