# oml meta, oml expr (and its corpus check), expression problems in oml spec check, oml warn --catalog, and oml app check.
# The corpus check parses every expression of modules built from tests/check-clean.json and check-bad.json: they were
# saved by Service Studio, so any failure is a parser gap. Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$work = Join-Path $Tmp ('parsers-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Force $work | Out-Null
$tests = $PSScriptRoot
function Fail($m) { throw "parsers: $m" }
function Require($condition, $message) { if (-not $condition) { Fail $message } }
function Call([string[]]$a) {
  $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try { $out = & $Oml @a 2>&1 } finally { $ErrorActionPreference = $prev }
  $text = ($out | ForEach-Object { "$_" }) -join "`n"
  $json = $null; try { $json = $text | ConvertFrom-Json } catch { }
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text; Json = $json }
}
function Fixture([string]$name, [string]$spec) {
  $blank = Join-Path $work "$name-blank.oml"; $out = Join-Path $work "$name.oml"
  $r = Call 'new', $blank, '--name', $name, '--kind', 'CrossDevice'; Require ($r.Exit -eq 0) "new $name`: $($r.Text)"
  $r = Call 'apply', $blank, (Join-Path $tests $spec), $out; Require ($r.Json.saved) "apply $spec`: $($r.Text)"
  @($blank, $out)
}

# ---- meta ----
$clean = Fixture 'ParseClean' 'check-clean.json'
$bad = Fixture 'ParseBad' 'check-bad.json'
$r = Call 'meta', $clean[1]
Require ($r.Exit -eq 0 -and $r.Json.info.Name -eq 'ParseClean' -and $r.Json.info.ESpaceType -eq 'CrossDevice' -and $r.Json.info.Key.Length -gt 0) "meta: $($r.Text)"
$r = Call 'meta', $clean[0], $clean[1], $bad[0], $bad[1]
Require ($r.Exit -eq 0 -and @($r.Json).Count -eq 4) "meta with 4 files: $($r.Text)"
$r = Call 'meta', $clean[1], (Join-Path $work 'missing.oml')
Require ($r.Exit -eq 1 -and @($r.Json | Where-Object error).Count -eq 1) "meta with a missing file: $($r.Text)"
$r = Call 'meta', '--diff', $clean[1], $clean[1]
Require ($r.Exit -eq 0 -and $r.Json.same -eq $true) "diff of one file with itself: $($r.Text)"
$r = Call 'meta', '--diff', $clean[0], $clean[1]
Require ($r.Exit -eq 0 -and -not $r.Json.same -and @($r.Json.differing | Where-Object field -eq 'contentHash').Count -eq 1) "diff blank vs built: $($r.Text)"
$r = Call 'meta', '--diff', $clean[1], (Join-Path $work 'missing.oml')
Require ($r.Exit -eq 1 -and $r.Json.error) "diff with a missing file: $($r.Text)"

# ---- expr ----
$r = Call 'expr', 'NOT a AND b'
Require ($r.Exit -eq 0 -and $r.Json.ok -and $r.Json.canonical -eq 'not a and b') "expr canonical: $($r.Text)"
$r = Call 'expr', 'a +'
Require ($r.Exit -eq 1 -and -not $r.Json.ok -and $r.Json.error.Id) "expr invalid: $($r.Text)"
foreach ($m in $clean[1], $bad[1]) {
  $r = Call 'expr', '--corpus', $m
  Require ($r.Exit -eq 0 -and $r.Json.expressions -gt 10 -and $r.Json.failed -eq 0) "corpus of $(Split-Path $m -Leaf): $($r.Text)"
}

# ---- spec check parses expressions ----
$spec = Join-Path $work 'bad-expr.json'
Set-Content $spec '{ "serverActions": [ { "name": "Calc", "flow": [ { "kind": "if", "name": "Check", "condition": "a >", "true": "End", "false": "End" }, { "kind": "end", "name": "End" } ] } ] }'
$r = Call 'spec', 'check', $spec
Require ($r.Exit -eq 1 -and $r.Text -match 'condition' -and $r.Text -match 'invalid expression') "spec check bad expression: $($r.Text)"
Set-Content $spec '{ "serverActions": [ { "name": "Calc", "flow": [ { "kind": "if", "name": "Check", "condition": "Length(\"a\") > 0 and not False", "true": "End", "false": "End" }, { "kind": "end", "name": "End" } ] } ] }'
$r = Call 'spec', 'check', $spec
Require ($r.Exit -eq 0) "spec check good expression: $($r.Text)"

# ---- warn ----
$r = Call 'warn', '--catalog', 'Entity_'
Require ($r.Exit -eq 0 -and $r.Text -match 'Entity_' -and $r.Text -notmatch '(?m)^InvalidFlow_') "warn --catalog Entity_: $($r.Text)"
$r = Call 'warn'
Require ($r.Exit -eq 2) "warn without arguments must be a usage error, not a crash: exit $($r.Exit)"
$r = Call 'warn', '--nosuch'
Require ($r.Exit -eq 2) "warn with an unknown option: exit $($r.Exit)"

# ---- app check ----
$pack = Join-Path $work 'app.oap'
$r = Call 'app', 'pack', $pack, '--name', 'ParseApp', $clean[1]; Require ($r.Exit -eq 0) "app pack: $($r.Text)"
$r = Call 'app', 'check', $pack
Require ($r.Exit -eq 2) "app check without --producers: exit $($r.Exit) $($r.Text)"
$r = Call 'app', 'check', $pack, '--deep'
Require ($r.Exit -eq 2 -and $r.Text -match '--producers') "app check --deep without --producers: $($r.Text)"
$r = Call 'app', 'check', $pack, '--producers', $work
Require ($r.Exit -eq 0 -and $r.Json.note -match 'no other application' -and $r.Json.summary.missingProducers -eq 0) "app check: $($r.Text)"
$r = Call 'app', 'list', $pack
Require ($r.Exit -eq 0 -and @($r.Json.modules | Where-Object name -eq 'ParseClean').Count -eq 1) "app list: $($r.Text)"
$out = Join-Path $work 'extracted'
$r = Call 'app', 'extract', $pack, $out
Require ($r.Exit -eq 0 -and (Test-Path (Join-Path $out 'ParseClean.oml'))) "app extract: $($r.Text)"

Write-Host 'ok   parsers: meta (files, missing, --diff), expr and corpus (0 parse failures), spec check expressions, warn --catalog and usage, app check, list, extract'
