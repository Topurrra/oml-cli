# oml preview --live, end to end with a real browser (DevTools): two modules that differ in one block's style sheet
# (and, in a third, also in a new block), and a local page that carries the old style sheet the way a published app
# does. Checks the rules swapped, the before and after screenshots, what needs a publish, and the refusals.
# Skips when no browser with DevTools is available. Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'live-preview'
$server = $null
try {
$Tmp = [IO.Path]::GetFullPath($Tmp)
function Fail($m) { throw "live-preview: $m" }
function Require($condition, $message) { if (-not $condition) { Fail $message } }
function Call([string[]]$a) {
  $out = & $Oml @a 2>&1
  $text = ($out | ForEach-Object { "$_" }) -join "`n"
  $json = $null; try { $json = $text | ConvertFrom-Json } catch { }
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text; Json = $json }
}
$savedBrowser = $env:OML_BROWSER; $env:OML_LOG = '0'

# 1. The modules: source (red card, plus a rule that goes away), staged (green card), staged2 (green card and a new block).
$oldCss = ".lv-card { color: rgb(200, 0, 0); padding: 20px; }`n.lv-gone { border: 8px solid rgb(0, 0, 200); }"
$newCss = ".lv-card { color: rgb(0, 150, 0); padding: 20px; background: rgb(255, 240, 200); }"
function Spec($name, $blocks) { $p = Join-Path $Tmp $name; Set-Content $p (@{ blocks = $blocks } | ConvertTo-Json -Depth 10) -Encoding utf8; $p }
$block = @{ name = 'LvCard'; styleSheet = $oldCss; widgets = @(@{ type = 'Container'; name = 'CardRoot'; style = '"lv-card"'; children = @(@{ type = 'Text'; text = 'Card' }) }) }
$blank = Join-Path $Tmp 'blank.oml'; $source = Join-Path $Tmp 'source.oml'; $staged = Join-Path $Tmp 'staged.oml'; $staged2 = Join-Path $Tmp 'staged2.oml'
& $Oml new $blank --name LvCheck --kind CrossDevice | Out-Null
$r = Call 'apply', $blank, (Spec 'one.json' @($block)), $source; Require ($r.Json.saved) "apply source: $($r.Text)"
$block.styleSheet = $newCss
$r = Call 'apply', $source, (Spec 'two.json' @($block)), $staged; Require ($r.Json.saved) "apply staged: $($r.Text)"
$other = @{ name = 'LvOther'; widgets = @(@{ type = 'Text'; text = 'Other' }) }
$r = Call 'apply', $source, (Spec 'three.json' @($block, $other)), $staged2; Require ($r.Json.saved) "apply staged2: $($r.Text)"

# describe --css --all lists the block's sheet.
$r = Call 'describe', $staged, '--css', '--all'
Require ($r.Exit -eq 0 -and @($r.Json | Where-Object { $_.element -match 'LvCard' -and $_.styleSheet -eq $newCss }).Count -eq 1) "describe --css --all: $($r.Text)"
$r = Call 'describe', $staged, '--all'
Require ($r.Exit -eq 2 -and $r.Text -match '--all goes with --css') "describe --all without --css: $($r.Text)"

# 2. Refusals before a browser starts.
$r = Call 'preview', '--live', 'not-a-url', $source, $staged
Require ($r.Exit -eq 2 -and $r.Json.error -match 'not an http') "bad url: $($r.Text)"
$r = Call 'preview', '--live', 'http://127.0.0.1:9/', $source
Require ($r.Exit -eq 2 -and $r.Json.error -match 'Usage: oml preview --live') "one file: $($r.Text)"
$r = Call 'preview', '--live', 'http://127.0.0.1:9/', $source, $staged, '--engine', 'uia'
Require ($r.Exit -eq 2 -and $r.Json.error -match 'DevTools') "uia engine: $($r.Text)"
$r = Call 'preview', '--live', 'http://127.0.0.1:9/', $source, $source
Require ($r.Exit -eq 1 -and $r.Json.reason -match 'No style sheet changed') "no change: $($r.Text)"

# 3. A page with the old style sheet, served by a separate process on a free local port. The browser needs DevTools:
# Edge and Chrome under a RemoteDebuggingAllowed=0 policy cannot, so Playwright's Chromium is used when it is there.
if (-not $env:OML_BROWSER) {
  $chromium = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'ms-playwright') -Directory -Filter 'chromium-*' -ErrorAction SilentlyContinue | Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'chrome-win64\chrome.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
  if ($chromium) { $env:OML_BROWSER = $chromium }
}
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$page = "<!doctype html><html><head><style>body { font: 16px sans-serif; } $($oldCss -replace "`n", ' ')</style></head><body><div class=`"lv-card lv-gone`">Card</div></body></html>"
$serverScript = Join-Path $Tmp 'server.ps1'
Set-Content $serverScript @"
`$html = [IO.File]::ReadAllText('$(Join-Path $Tmp 'page.html')')
`$l = [Net.HttpListener]::new(); `$l.Prefixes.Add('http://127.0.0.1:$port/'); `$l.Start()
while (`$true) { `$c = `$l.GetContext(); `$b = [Text.Encoding]::UTF8.GetBytes(`$html); `$c.Response.ContentType = 'text/html; charset=utf-8'; `$c.Response.OutputStream.Write(`$b, 0, `$b.Length); `$c.Response.Close() }
"@
Set-Content (Join-Path $Tmp 'page.html') $page
$server = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $serverScript -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 1500
$url ="http://127.0.0.1:$port/LvCheck/Home"
$out = Join-Path $Tmp 'shots'
$r = Call 'preview', '--live', $url, $source, $staged, '--no-session', '--engine', 'cdp', '--out', $out
if ($r.Exit -eq 2 -and $r.Text -match 'DevTools|browser|Edge|Chrome|policy') { Write-Host "skip live-preview: no browser with DevTools ($($r.Json.error))"; return }
Require ($r.Exit -eq 0 -and $r.Json.live -and $r.Json.engine -eq 'cdp') "live preview: $($r.Text)"
Require ($r.Json.rules.oldRules -eq 2 -and $r.Json.rules.oldRulesFound -eq 2 -and $r.Json.rules.newRules -eq 1) "rules swapped: $($r.Json.rules | ConvertTo-Json -Compress)"
Require ((Test-Path $r.Json.before) -and (Test-Path $r.Json.after) -and (Get-FileHash $r.Json.before).Hash -ne (Get-FileHash $r.Json.after).Hash) 'before and after screenshots exist and differ'
Require (@($r.Json.styleSheets).Count -eq 1 -and $r.Json.styleSheets[0] -match 'LvCard' -and -not $r.Json.needsPublish) "style sheets and nothing to publish: $($r.Text)"

# 4. A new block too: the style sheet is shown, the block is listed as needing a publish.
$r = Call 'preview', '--live', $url, $source, $staged2, '--no-session', '--engine', 'cdp', '--out', (Join-Path $Tmp 'shots2')
Require ($r.Exit -eq 0 -and @($r.Json.needsPublish | Where-Object { $_ -match 'LvOther' }).Count -eq 1 -and $r.Json.next -match 'oml qa page') "needs publish: $($r.Text)"

# 5. A page without those rules: the note says what is published differs.
$r = Call 'preview', '--live', "http://127.0.0.1:$port/", $staged, $source, '--no-session', '--engine', 'cdp', '--out', (Join-Path $Tmp 'shots3')
Require ($r.Exit -eq 0 -and $r.Json.rules.oldRulesFound -eq 0 -and @($r.Json.notes | Where-Object { $_ -match 'not on this page' }).Count -eq 1) "rules not on the page: $($r.Text)"

Write-Host 'ok   live-preview: style sheets swapped in a real page (before/after), new elements listed for a publish, refusals, describe --css --all'
} finally {
  if ($server) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
  $env:OML_BROWSER = $savedBrowser
  Remove-TestWorkFolder $Tmp
}
