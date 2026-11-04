# oml qa: the usage text and every refusal that comes before a browser starts (no URL, bad file, bad JSON, no steps,
# unknown engine, bad numbers, unknown option). A live app is not part of the suite: run a QA file by hand against a
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
foreach ($line in 'oml qa page', 'oml qa run', 'oml qa record', 'oml qa start', 'oml qa stop') { Require ($r.Exit -eq 0 -and $r.Text -match $line) "help misses ${line}: $($r.Text)" }

# record: refused before a window opens. (qa stop is not called: it would stop a real session on this machine.)
$r = Call 'qa', 'record', 'not-a-url'
Require ($r.Exit -eq 2 -and $r.Json.error -match 'not an http') "record without URL: $($r.Text)"
$taken = File 'taken.jsonc' '{}'
$r = Call 'qa', 'record', 'https://dev.example.com/App/Home', '--out', $taken
Require ($r.Exit -eq 2 -and $r.Json.error -match 'exists') "record over an existing file: $($r.Text)"
$r = Call 'qa', 'frobnicate', 'x'
Require ($r.Exit -eq 2 -and $r.Json.error -match 'oml qa run') "unknown subcommand: $($r.Text)"

$r = Call @('qa')
Require ($r.Exit -eq 2 -and $r.Json.error -match 'oml qa run') "no arguments: $($r.Text)"

$r = Call 'qa', 'page', 'not-a-url'
Require ($r.Exit -eq 2 -and $r.Json.error -match 'not an http') "page without URL: $($r.Text)"

$r = Call 'qa', 'run', (Join-Path $work 'missing.json')
Require ($r.Exit -eq 2 -and $r.Json.error -match 'No QA file') "missing file: $($r.Text)"

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
} finally { Remove-TestWorkFolder $Tmp }
