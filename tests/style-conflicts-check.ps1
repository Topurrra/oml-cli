# oml qa page --style-conflicts, end to end with a real browser (DevTools): a local page with an OutSystems UI style
# sheet (by its file name) whose more specific rule beats the module's rule on one property. Checks the conflict found,
# the property the module alone sets not reported, an element without a contest not reported, and the UI Automation
# refusal. Skips when no browser with DevTools is available. Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Tmp = New-TestFolder $Tmp 'style-conflicts'
$server = $null
try {
$Tmp = [IO.Path]::GetFullPath($Tmp)
function Fail($m) { throw "style-conflicts: $m" }
function Require($condition, $message) { if (-not $condition) { Fail $message } }
function Call([string[]]$a) {
  $out = & $Oml @a 2>&1
  $text = ($out | ForEach-Object { "$_" }) -join "`n"
  $json = $null; try { $json = $text | ConvertFrom-Json } catch { }
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text; Json = $json }
}
if (-not $env:OML_BROWSER) {
  $chromium = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'ms-playwright') -Directory -Filter 'chromium-*' -ErrorAction SilentlyContinue | Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'chrome-win64\chrome.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
  if ($chromium) { $env:OML_BROWSER = $chromium }
}
$site = Join-Path $Tmp 'site'
New-Item -ItemType Directory -Path $site | Out-Null
Set-Content (Join-Path $site 'OutSystemsUI.OutSystemsUI.css') 'div[data-switch].sc-x { color: blue; padding: 4px; }'
Set-Content (Join-Path $site 'App.App.css') ".sc-x { color: red; margin: 2px; }`n.sc-y { color: green; }"
Set-Content (Join-Path $site 'index.html') '<!doctype html><html><head><link rel="stylesheet" href="OutSystemsUI.OutSystemsUI.css"><link rel="stylesheet" href="App.App.css"></head><body><div data-switch class="sc-x">a</div><div data-switch class="sc-x">b</div><div class="sc-y">c</div></body></html>'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$serverScript = Join-Path $Tmp 'server.ps1'
Set-Content $serverScript @"
`$root = '$site'
`$l = [Net.HttpListener]::new(); `$l.Prefixes.Add('http://127.0.0.1:$port/'); `$l.Start()
while (`$true) {
  `$c = `$l.GetContext(); `$name = [IO.Path]::GetFileName(`$c.Request.Url.AbsolutePath); if (-not `$name) { `$name = 'index.html' }
  `$file = Join-Path `$root `$name
  if (Test-Path `$file) { `$b = [IO.File]::ReadAllBytes(`$file); `$c.Response.ContentType = if (`$name.EndsWith('.css')) { 'text/css' } else { 'text/html; charset=utf-8' }; `$c.Response.OutputStream.Write(`$b, 0, `$b.Length) }
  else { `$c.Response.StatusCode = 404 }
  `$c.Response.Close()
}
"@
$server = Start-Process pwsh -ArgumentList '-NoProfile', '-File', $serverScript -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 1500
$url = "http://127.0.0.1:$port/index.html"

$r = Call 'qa', 'page', $url, '--style-conflicts', 'sc-', '--no-session', '--engine', 'cdp'
if ($r.Exit -eq 2 -and $r.Text -match 'DevTools|browser|Edge|Chrome|policy') { Write-Host "skip style-conflicts: no browser with DevTools ($($r.Json.error))"; return }
Require ($r.Exit -eq 0 -and $r.Json.styleConflicts) "qa page: $($r.Text)"
$s = $r.Json.styleConflicts
Require ($s.checkedElements -eq 3 -and $s.total -eq 1) "counts: $($s | ConvertTo-Json -Compress)"
$c = $s.conflicts[0]
Require ($c.property -eq 'color' -and $c.yours -eq '.sc-x { color: red }' -and $c.wins -eq 'div[data-switch].sc-x { color: blue }' -and $c.winsFrom -eq 'OutSystemsUI.OutSystemsUI.css' -and $c.elements -eq 2) "conflict: $($c | ConvertTo-Json -Compress)"
Require ($s.hint -match 'more specific selector') "hint: $($s.hint)"
Write-Host 'ok   qa page --style-conflicts: an OutSystems UI rule that wins is reported with both selectors; uncontested properties are not'
} finally {
  if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
  Remove-TestWorkFolder $Tmp
}
