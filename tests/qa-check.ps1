# oml browser: the usage text and every refusal that comes before a browser starts (no URL, bad file, bad JSON, no steps,
# unknown engine, bad numbers, unknown option, browser raw and --attach refusals). A live app is not part of the suite: run a browser script by hand against a
# development environment. Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'qa'
try {
$work = Join-Path $Tmp ('qa-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Force $work | Out-Null
function Fail($m) { throw "qa: $m" }
function Require($condition, $message) { if (-not $condition) { Fail $message } }
function Call([string[]]$a) {
  $out = & $Oml @a 2>&1
  $text = ($out | ForEach-Object { "$_" }) -join "`n"
  $json = $null; try { $json = $text | ConvertFrom-Json } catch { }
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text; Json = $json }
}
function File([string]$name, [string]$text) { $p = Join-Path $work $name; Set-Content $p $text; $p }

$r = Call 'qa', '--help'
foreach ($line in 'oml browser page', 'oml browser run', 'oml browser record', 'oml browser start', 'oml browser stop', 'oml browser raw') { Require ($r.Exit -eq 0 -and $r.Text -match $line) "help misses ${line}: $($r.Text)" }

# record: refused before a window opens. (browser stop is not called: it would stop a real session on this machine.)
$r = Call 'qa', 'record', 'not-a-url'
Require ($r.Exit -eq 2 -and $r.Json.error -match 'not an http') "record without URL: $($r.Text)"
$taken = File 'taken.jsonc' '{}'
$r = Call 'qa', 'record', 'https://dev.example.com/App/Home', '--out', $taken
Require ($r.Exit -eq 2 -and $r.Json.error -match 'exists') "record over an existing file: $($r.Text)"
$r = Call 'qa', 'frobnicate', 'x'
Require ($r.Exit -eq 2 -and $r.Json.error -match 'oml browser run') "unknown subcommand: $($r.Text)"

$r = Call @('qa')
Require ($r.Exit -eq 2 -and $r.Json.error -match 'oml browser run') "no arguments: $($r.Text)"

$r = Call 'qa', 'page', 'not-a-url'
Require ($r.Exit -eq 2 -and $r.Json.error -match 'not an http') "page without URL: $($r.Text)"

$r = Call 'qa', 'run', (Join-Path $work 'missing.json')
Require ($r.Exit -eq 2 -and $r.Json.error -match 'No browser script') "missing file: $($r.Text)"

$bad = File 'bad.json' '{ "steps": [ '
$r = Call 'qa', 'run', $bad
Require ($r.Exit -eq 2 -and $r.Json.error -match 'not valid JSON') "bad JSON: $($r.Text)"

$nosteps = File 'nosteps.json' '{ "baseUrl": "https://dev.example.com/App/" }'
$r = Call 'qa', 'run', $nosteps
Require ($r.Exit -eq 2 -and $r.Json.error -match 'steps') "no steps: $($r.Text)"

# JSONC comments and trailing commas are read; these refusals still come before any browser starts.
$good = File 'good.jsonc' "{`n  // a comment`n  `"baseUrl`": `"https://dev.example.com/App/`",`n  `"steps`": [ { `"open`": `"Home`" }, ],`n}"
$r = Call 'qa', 'run', $good, '--engine', 'bogus'
Require ($r.Exit -eq 2 -and $r.Json.error -match 'auto, cdp or uia') "unknown engine: $($r.Text)"
$r = Call 'qa', 'run', $good, '--width', '0'
Require ($r.Exit -eq 2 -and $r.Json.error -match 'positive number') "zero width: $($r.Text)"
$r = Call 'qa', 'run', $good, '--frobnicate'
Require ($r.Exit -eq 2 -and $r.Json.error -match 'Unknown or incomplete option') "unknown option: $($r.Text)"
$r = Call 'qa', 'run', $good, '--base-url', 'relative/path'
Require ($r.Exit -eq 2 -and $r.Json.error -match 'not an absolute URL') "relative base URL: $($r.Text)"

# browser raw and --attach: refused before any browser or session. The session state file is the test's own, so a
# session the user runs on this machine is neither used nor touched.
$savedSession = $env:OML_BROWSER_SESSION
$env:OML_BROWSER_SESSION = Join-Path $work 'browser-session.json'
try {
  $r = Call 'browser', 'raw', 'Browser.close'
  Require ($r.Exit -eq 2 -and $r.Json.error -match 'whole browser') "raw Browser.close: $($r.Text)"
  $r = Call 'browser', 'raw', 'not-a-method'
  Require ($r.Exit -eq 2 -and $r.Json.error -match 'not a DevTools method') "raw bad method: $($r.Text)"
  $r = Call 'browser', 'raw', 'Runtime.evaluate'
  Require ($r.Exit -eq 2 -and $r.Json.error -match 'oml browser start') "raw without a session: $($r.Text)"
  $r = Call 'browser', 'start', '--attach', 'ws://example.com:9222/devtools/browser/x'
  Require ($r.Exit -eq 2 -and $r.Json.error -match 'not this computer') "attach to another host: $($r.Text)"
  $r = Call 'browser', 'start', '--attach', '70000'
  Require ($r.Exit -eq 2 -and $r.Json.error -match 'not a port') "attach to a bad port: $($r.Text)"
  $r = Call 'browser', 'run', $good, '--attach', '9222', '--engine', 'uia'
  Require ($r.Exit -eq 2 -and $r.Json.error -match 'engine uia') "attach with uia: $($r.Text)"
} finally { $env:OML_BROWSER_SESSION = $savedSession }
} finally { Remove-TestWorkFolder $Tmp }
