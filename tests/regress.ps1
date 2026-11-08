# Regression: every fixture must apply with saved=true and no new errors. Run from the repo root.
# Needs PowerShell 7. Usage: pwsh -NoProfile -File tests\regress.ps1 [-Throttle N] [-Smoke]
#
# How it runs. It builds src first (incremental), so it never tests a stale build. Each check is a "unit". A unit runs in
# its own pwsh process (Start-Job) with its own work folder tmp\<unit> and its own TEMP, so units cannot clash on files or
# on process-wide environment variables.
#   1. Setup (here, one at a time): the blank modules and the producer module that most units start from, in tmp\base.
#   2. Units (up to -Throttle at once; default half the logical processors, 4 to 16), longest first by the times of the
#      last full run (%LOCALAPPDATA%\oml-cli\regress-times.json). Every unit starts from a copy of tmp\base. A unit with
#      -After waits for the units whose outputs it reads; a unit with -Publish moves the out-*.oml files it made into
#      tmp\base. A unit with -Group shares something with the others of its group, which run at most $groupLimit at a time:
#        browser   (3)  headless Edge/Chrome with time windows: preview, test-scaffold, architecture-page, check-page,
#                       review-page, image (also a 5 s trace limit), dashboard (a local port), qa-replay (a local page)
#        usagelog  (1)  log-check and aliases-check append to and trim the person's default usage log (OML_LOG=1)
#        selftest  (1)  selftest records the build as checked in %LOCALAPPDATA%; doctor-check reads that record
#        hkcu      (1)  setup-check writes a throwaway HKCU key and compares the real HKCU\Software\Classes keys
#        lifetime  (1)  lifetime-check starts a mock LifeTime server
# Output is printed per unit in the order of the list below, not in finish order.
# A full run that passes on a clean tree records its commit in %LOCALAPPDATA%\oml-cli\regress-passed.json; release.ps1
# releases only a commit with that record. Most of the time is Service Studio's start-up (about 1.5 s per model command).
# To add a check: see CONTRIBUTING.md ("Adding a regression check").
# -Smoke runs only the quick units (see $smokeUnits below), including the real applications of corpus-check.
param([int]$Throttle = [math]::Min(16, [math]::Max(4, [int]([Environment]::ProcessorCount / 2))), [switch]$Smoke)
$ErrorActionPreference = "Stop"
$watch = [Diagnostics.Stopwatch]::StartNew()
$tests = $PSScriptRoot
$oml = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\src\bin\Release\net10.0\oml.exe"))
$tmp = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\tmp"))
. (Join-Path $tests 'lib.ps1')
New-Item -ItemType Directory -Force $tmp | Out-Null
# Start from nothing: a leftover from an earlier or interrupted run (a junction, an old r0.oml) must not matter. Links are
# removed as links. release.ps1 keeps its own tmp\release-* folder while this runs, so that one stays.
foreach ($child in @(Get-ChildItem -LiteralPath $tmp -Force | Where-Object { $_.Name -notlike 'release-*' })) { Remove-TestFolder $child.FullName }
$base = Join-Path $tmp "base"; New-Item -ItemType Directory -Force $base | Out-Null
$env:TEMP = $tmp; $env:TMP = $tmp
$env:OML_LOG = '0' # the suite must not write to a person's usage log (it is on by default)
# Test what the sources say, never a stale build: an incremental build takes a few seconds when nothing changed.
$repo = Split-Path $tests -Parent
& dotnet build (Join-Path $repo 'src\OmlCli.csproj') -c Release --nologo -v q -clp:ErrorsOnly | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Build failed; nothing was tested.' }
# A full run that passes on a clean tree records its commit; release.ps1 releases only a commit with such a record.
function TreeState { $c = (& git -C $repo rev-parse HEAD 2>$null); $d = @(& git -C $repo status --porcelain --untracked-files=no 2>$null); [pscustomobject]@{ Commit = $c; Clean = ($LASTEXITCODE -eq 0 -and $d.Count -eq 0) } }
$startState = TreeState
$fail = 0

# --- Setup: the blank modules every unit starts from. ---
& $oml new (Join-Path $base "r0.oml") --name Reg --kind CrossDevice | Out-Null
& $oml new (Join-Path $base "s0.oml") --name RegSvc --kind Service | Out-Null
& $oml new (Join-Path $base "w0.oml") --name RegWeb --kind WebResponsive | Out-Null
& $oml new (Join-Path $base "references-producer-blank.oml") --name ReferenceProducer --kind CrossDevice | Out-Null
& $oml apply (Join-Path $base "references-producer-blank.oml") (Join-Path $tests 'references-producer.json') (Join-Path $base "references-producer.oml") | Out-Null
# references.json names this producer as tmp/references-producer.oml, relative to the repo root.
Copy-Item (Join-Path $base "references-producer.oml") $tmp
# Every fixture the units copy must exist now; a failed `oml new` or `apply` stops here with a clear message.
foreach ($fixture in 'r0.oml', 's0.oml', 'w0.oml', 'references-producer-blank.oml', 'references-producer.oml') {
  if (-not (Test-Path -LiteralPath (Join-Path $base $fixture) -PathType Leaf)) { throw "Setup did not build fixture $fixture in $base (oml new / apply failed). Build first: dotnet build src/OmlCli.csproj -c Release" }
}
& $oml --help | Out-Null; if ($LASTEXITCODE -ne 0) { $fail++; Write-Host "FAIL --help exit $LASTEXITCODE" } else { Write-Host "ok   --help" }
# `oml <command> --help` prints that command's usage, not the whole list.
$helpBad = @(foreach ($c in 'check', 'doc', 'explain-warning', 'apply', 'lifetime', 'font', 'doctor', 'explorer-menu', 'image', 'dashboard') { $h = (& $oml $c --help) -join "`n"; if ($LASTEXITCODE -ne 0 -or $h -notmatch "oml $c" -or $h -match 'oml new ') { $c } })
if ($helpBad) { $fail++; Write-Host "FAIL command --help prints the global usage: $($helpBad -join ', ')" } else { Write-Host "ok   command --help" }
# A known command with missing arguments prints its own usage on stderr, with exit 2.
$usageBad = @(foreach ($c in 'describe', 'tree', 'apply', 'install', 'find') { $h = (& $oml $c 2>&1) -join "`n"; if ($LASTEXITCODE -ne 2 -or $h -notmatch "oml $c" -or $h -match 'oml new ') { $c } })
if ($usageBad) { $fail++; Write-Host "FAIL command with missing arguments prints the global usage: $($usageBad -join ', ')" } else { Write-Host "ok   command usage on missing arguments" }

# --- Code every unit gets before its body: helpers and the paths of its own work folder. ---
$preamble = @'
$reactive = Join-Path $tmp "r0.oml"; $service = Join-Path $tmp "s0.oml"; $web = Join-Path $tmp "w0.oml"
# sql/delete/static fixtures: exit code must be 0 too.
function Check-Fixture($fixture, $source) {
  $r = & $oml apply $source (Join-Path $tests $fixture) (Join-Path $tmp "out-$fixture.oml") | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0 -or -not $r.saved -or $r.reopenNewErrors.Count -gt 0) { $global:fail++; Write-Host "FAIL $fixture"; Write-Host ($r | ConvertTo-Json -Depth 8) } else { Write-Host "ok   $fixture" }
  return $r
}
function Check-Case($fixture, $source) {
  $r = & $oml apply $source (Join-Path $tests $fixture) (Join-Path $tmp "out-$fixture.oml") 2>$null | ConvertFrom-Json
  if (-not $r.saved -or $r.reopenNewErrors.Count -gt 0) { $global:fail++; Write-Host "FAIL $fixture"; Write-Host ($r | ConvertTo-Json -Depth 8) } else { Write-Host "ok   $fixture" }
  return $r
}
'@

# --- What runs in each unit's process. ---
$runner = {
  param($name, $oml, $tests, $base, $work, $preamble, $bodyText, $publish)
  $ErrorActionPreference = 'Stop'
  Set-Location (Split-Path $tests -Parent)
  . (Join-Path $tests 'lib.ps1')
  Remove-TestFolder $work
  New-Item -ItemType Directory -Force $work | Out-Null
  Copy-Item (Join-Path $base '*.oml') $work
  $env:TEMP = $work; $env:TMP = $work
  $tmp = $work
  $global:fail = 0
  $before = @(Get-ChildItem $work -Filter 'out-*.oml' | ForEach-Object Name)
  $lines = [System.Collections.Generic.List[object]]::new()
  try {
    . ([scriptblock]::Create($preamble))
    . ([scriptblock]::Create($bodyText)) *>&1 | ForEach-Object { $lines.Add($_) }
  } catch { $global:fail++; $lines.Add("FAIL ${name}: $_") }
  if ($publish) {
    Get-ChildItem $work -Filter 'out-*.oml' | Where-Object { $before -notcontains $_.Name } |
      ForEach-Object { Move-Item $_.FullName $base -Force }
  }
  [pscustomobject]@{ Name = $name; Fail = $global:fail; Text = ($lines | Out-String).TrimEnd() }
}

# --- The units, in the order their output is printed. ---
$units = [System.Collections.Generic.List[object]]::new()
# -Group: units of one group share something (a browser, the usage log, the self-test record) and run at most
# $groupLimit[group] at a time; all other units run side by side, up to -Throttle at once.
function Unit([string]$Name, [scriptblock]$Body, [string[]]$After = @(), [switch]$Publish, [string]$Group) {
  $units.Add([pscustomobject]@{ Name = $Name; Body = $Body.ToString(); After = $After; Publish = [bool]$Publish; Group = $Group; Job = $null; Started = $false })
}
$groupLimit = @{ browser = 3; usagelog = 1; selftest = 1; hkcu = 1; lifetime = 1 }

# Real applications (OML_CORPUS or corpus.txt): no module may be skipped and no command may crash on them.
Unit 'corpus-check' {
  try { & (Join-Path $tests 'corpus-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL real applications: $_" }
}
Unit 'logic-check' {
  try { & (Join-Path $tests 'logic-check.ps1') $oml $tmp }
  catch { $global:fail++; Write-Host "FAIL extended logic checks: $_" }
}
Unit 'sql.json' -Publish { Check-Fixture 'sql.json' $reactive | Out-Null }
Unit 'delete.json' -Publish {
  $r = Check-Fixture 'delete.json' $reactive
  if (-not ($r.warnings -join ' ' -match 'public entity')) { $global:fail++; Write-Host 'FAIL public deletion warning' }
}
Unit 'static.json' -Publish { Check-Fixture 'static.json' $reactive | Out-Null }
# rules-check has about 100 applies in a row, the longest check: four units each run every 4th case.
$rulesBody = {
  try { & (Join-Path $tests 'rules-check.ps1') $oml $tmp -Part __PART__ -Parts 4 }
  catch { $global:fail++; Write-Host "FAIL production rules: $_" }
}
foreach ($part in 0..3) { Unit "rules-check-$($part + 1)" ([scriptblock]::Create($rulesBody.ToString().Replace('__PART__', $part))) }
Unit 'sql-delete-check' -After 'sql.json','delete.json' {
  try { & (Join-Path $tests 'sql-delete-check.ps1') $oml $tmp }
  catch { $global:fail++; Write-Host "FAIL SQL/delete checks: $_" }
}
Unit 'names-check' {
  try { & (Join-Path $tests 'names-check.ps1') -Oml $oml -Tmp $tmp; Write-Host 'ok   qualified names, ambiguity, find' }
  catch { $global:fail++; Write-Host "FAIL names checks: $_" }
}
Unit 'references-check' {
  try { & (Join-Path $tests 'references-check.ps1') $oml $tmp $reactive }
  catch { $global:fail++; Write-Host "FAIL references checks: $_" }
}
Unit 'logic.json' -Publish { Check-Case 'logic.json' $reactive | Out-Null }
Unit 'block.json' -Publish { Check-Case 'block.json' $reactive | Out-Null }
Unit 'service.json' -Publish { Check-Case 'service.json' $service | Out-Null }
Unit 'datamodel.json' -Publish { Check-Case 'datamodel.json' $reactive | Out-Null }
Unit 'edits.json' -Publish -After 'logic.json' { Check-Case 'edits.json' (Join-Path $tmp 'out-logic.json.oml') | Out-Null }
Unit 'queries.json' -Publish { Check-Case 'queries.json' $reactive | Out-Null }
Unit 'queries-check' -After 'queries.json' {
  $out = Join-Path $tmp 'out-queries.json.oml'
  if (Test-Path $out) {
    try { & (Join-Path $tests 'queries-check.ps1') $oml $out $reactive }
    catch { $global:fail++; Write-Host "FAIL queries wiring: $_" }
  }
}
Unit 'reapply-check' {
  try { & (Join-Path $tests 'reapply-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL re-apply checks: $_" }
}
Unit 'reuse-check' {
  try { & (Join-Path $tests 'reuse-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL reuse checks: $_" }
}
Unit 'public-check' {
  try { & (Join-Path $tests 'public-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL public setting checks: $_" }
}
Unit 'clash-check' {
  try { & (Join-Path $tests 'clash-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL name clash checks: $_" }
}
Unit 'api-check' {
  try { & (Join-Path $tests 'api-check.ps1') -Oml $oml }
  catch { $global:fail++; Write-Host "FAIL api checks: $_" }
}
Unit 'ui-update-check' {
  try { & (Join-Path $tests 'ui-update-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL ui update checks: $_" }
}
Unit 'data-check' {
  try { & (Join-Path $tests 'data-check.ps1') $oml $tmp }
  catch { $global:fail++; Write-Host "FAIL data checks: $_" }
}
Unit 'scaffold.json' -Publish { Check-Case 'scaffold.json' $reactive | Out-Null }
Unit 'rest.json' -Publish { Check-Case 'rest.json' $reactive | Out-Null }
Unit 'rest-check' -After 'rest.json' {
  $out = Join-Path $tmp 'out-rest.json.oml'
  if (Test-Path $out) {
    try { & (Join-Path $tests 'rest-check.ps1') $oml $out }
    catch { $global:fail++; Write-Host "FAIL REST wiring: $_" }
  }
}
Unit 'openapi-check' -After 'rest.json' {
  $out = Join-Path $tmp 'out-rest.json.oml'
  if (Test-Path $out) {
    try { & (Join-Path $tests 'openapi-check.ps1') $oml $out }
    catch { $global:fail++; Write-Host "FAIL OpenAPI: $_" }
  }
}
Unit 'platform.json' -Publish { Check-Case 'platform.json' $reactive | Out-Null }
Unit 'web.json' -Publish { Check-Case 'web.json' $web | Out-Null }
Unit 'references.json' -Publish { Check-Case 'references.json' $reactive | Out-Null }
Unit 'edits-check' -After 'logic.json','edits.json' {
  try { & (Join-Path $tests "edits-check.ps1") -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL edits checks: $_" }
}
Unit 'flowlayout-check' -After 'logic.json','service.json','platform.json','web.json' {
  try { & (Join-Path $tests 'flowlayout-check.ps1') $oml $tmp }
  catch { $global:fail++; Write-Host "FAIL flow layout checks: $_" }
}
Unit 'inspect-check' {
  try { & (Join-Path $tests "inspect-check.ps1") -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL inspect checks: $_" }
}
Unit 'describe-check' {
  try { & (Join-Path $tests "describe-check.ps1") -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL describe checks: $_" }
}
Unit 'paths-check' {
  try { & (Join-Path $tests "paths-check.ps1") -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL element paths: $_" }
}
Unit 'readers-check' {
  try { & (Join-Path $tests "readers-check.ps1") -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL readers: $_" }
}
# Every fixture output reads without unreadable parts (recipe outputs: in recipes-check).
Unit 'unreadable-check' -After 'sql.json','delete.json','static.json','logic.json','block.json','service.json','datamodel.json','edits.json','queries.json','scaffold.json','rest.json','platform.json','web.json','references.json' {
  try { & (Join-Path $tests "no-unreadable.ps1") -Oml $oml -Modules @(Get-ChildItem $tmp -Filter 'out-*.oml' | ForEach-Object FullName) -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL unreadable parts in fixture outputs: $_" }
}
Unit 'history-check' {
  try { & (Join-Path $tests 'history-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL history checks: $_" }
}
Unit 'turns-check' {
  try { & (Join-Path $tests 'turns-check.ps1') $oml $tmp }
  catch { $global:fail++; Write-Host "FAIL turns checks: $_" }
}
Unit 'run-check' {
  try { & (Join-Path $tests 'run-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL run checks: $_" }
}
Unit 'batch-check' {
  try { & (Join-Path $tests 'batch-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL batch checks: $_" }
}
Unit 'timings-check' {
  try { & (Join-Path $tests 'timings-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL timings checks: $_" }
}
Unit 'encoding-check' {
  try { & (Join-Path $tests 'encoding-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL encoding checks: $_" }
}
Unit 'content-check' {
  try { & (Join-Path $tests 'content-check.ps1') $oml $tmp $reactive }
  catch { $global:fail++; Write-Host "FAIL content checks: $_" }
}
Unit 'lifetime-check' -Group lifetime {
  try { & (Join-Path $tests "lifetime-check.ps1") -Oml $oml -Tmp $tmp } catch { $global:fail++; Write-Host "FAIL LifeTime checks: $_" }
}
# qa-replay-check drives a real browser window against a local page; one browser at a time.
Unit 'qa-replay-check' -Group browser {
  try { & (Join-Path $tests 'qa-replay-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL qa replay checks: $_" }
}
# log-check also appends to and trims the person's default usage log (OML_LOG=1), so it runs alone.
Unit 'aliases-check' -Group usagelog {
  try { & (Join-Path $tests 'aliases-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL aliases checks: $_" }
}
Unit 'log-check' -Group usagelog {
  try { & (Join-Path $tests 'log-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL log checks: $_" }
}
Unit 'pull-check' {
  try { & (Join-Path $tests "pull-check.ps1") -Oml $oml -Tmp $tmp } catch { $global:fail++; Write-Host "FAIL pull checks: $_" }
}
Unit 'validate-check' {
  try { & (Join-Path $tests 'validate-check.ps1') -Oml $oml -Tmp $tmp } catch { $global:fail++; Write-Host "FAIL validate checks: $_" }
}
Unit 'doctor-check' -Group selftest {
  try { & (Join-Path $tests 'doctor-check.ps1') -Oml $oml } catch { $global:fail++; Write-Host "FAIL doctor checks: $_" }
}
Unit 'recipes-check' {
  try { & (Join-Path $tests 'recipes-check.ps1') -Oml $oml }
  catch { $global:fail++; Write-Host "FAIL recipes checks: $_" }
}
Unit 'architect-check' {
  try { & (Join-Path $tests 'architect-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL architect skill checks: $_" }
}
Unit 'spec-check' {
  try { & (Join-Path $tests 'spec-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL spec checks: $_" }
}
Unit 'doc-check' {
  try { & (Join-Path $tests 'doc-check.ps1') -Oml $oml -Tmp $tmp -Module $reactive }
  catch { $global:fail++; Write-Host "FAIL doc checks: $_" }
}
Unit 'search-check' {
  try { & (Join-Path $tests 'search-check.ps1') -Oml $oml -Tmp $tmp -Module $reactive }
  catch { $global:fail++; Write-Host "FAIL search checks: $_" }
}
Unit 'hooks-check' {
  try { & (Join-Path $tests 'hooks-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL hooks checks: $_" }
}
Unit 'memory-check' {
  try { & (Join-Path $tests 'memory-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL memory checks: $_" }
}
Unit 'create-check' {
  try { & (Join-Path $tests 'create-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL create checks: $_" }
}
Unit 'testdata-check' {
  try { & (Join-Path $tests 'testdata-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL test data checks: $_" }
}
Unit 'scenarios-check' {
  try { & (Join-Path $tests 'scenarios-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL scenarios checks: $_" }
}
Unit 'map-check' {
  try { & (Join-Path $tests 'map-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL map checks: $_" }
}
Unit 'evolve-check' {
  try { & (Join-Path $tests 'evolve-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL evolve checks: $_" }
}
Unit 'library-check' {
  try { & (Join-Path $tests 'library-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL library checks: $_" }
}
Unit 'skills-check' {
  try { & (Join-Path $tests 'skills-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL skills checks: $_" }
}
Unit 'qa-check' {
  try { & (Join-Path $tests 'qa-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL qa checks: $_" }
}
Unit 'terminal-check' {
  try { & (Join-Path $tests 'terminal-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL terminal checks: $_" }
}
Unit 'parsers-check' {
  try { & (Join-Path $tests 'parsers-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL parsers checks: $_" }
}
Unit 'packages-check' {
  try { & (Join-Path $tests "packages-check.ps1") -Oml $oml -Tmp $tmp } catch { $global:fail++; Write-Host "FAIL packages checks: $_" }
}
Unit 'assets-check' {
  try { & (Join-Path $tests 'assets-check.ps1') -Oml $oml -Tmp $tmp } catch { $global:fail++; Write-Host "FAIL assets checks: $_" }
}
Unit 'image-check' -Group browser {
  try { & (Join-Path $tests 'image-check.ps1') -Oml $oml -Tmp $tmp } catch { $global:fail++; Write-Host "FAIL image checks: $_" }
}
Unit 'architecture-check' {
  try { & (Join-Path $tests 'architecture-check.ps1') -Oml $oml -Tmp $tmp } catch { $global:fail++; Write-Host "FAIL architecture checks: $_" }
}
Unit 'check-check' {
  try { & (Join-Path $tests 'check-check.ps1') -Oml $oml -Tmp $tmp } catch { $global:fail++; Write-Host "FAIL check checks: $_" }
}
Unit 'review-check' {
  try { & (Join-Path $tests 'review-check.ps1') -Oml $oml -Tmp $tmp } catch { $global:fail++; Write-Host "FAIL review checks: $_" }
}
Unit 'live-preview-check' -Group browser {
  try { & (Join-Path $tests 'live-preview-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL live preview checks: $_" }
}
Unit 'style-conflicts-check' -Group browser {
  try { & (Join-Path $tests 'style-conflicts-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL style conflicts checks: $_" }
}
Unit 'preview-check' -Group browser {
  try { & (Join-Path $tests 'preview-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL preview checks: $_" }
}
Unit 'test-scaffold-check' -Group browser {
  try { & (Join-Path $tests 'test-scaffold-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL test bdd: $_" }
}
Unit 'architecture-page-check' -Group browser {
  try { & (Join-Path $tests 'architecture-page-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL architecture page: $_" }
}
Unit 'check-page-check' -Group browser {
  try { & (Join-Path $tests 'check-page-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL check page: $_" }
}
Unit 'review-page-check' -Group browser {
  try { & (Join-Path $tests 'review-check.ps1') -Oml $oml -Tmp $tmp -Page }
  catch { $global:fail++; Write-Host "FAIL review page: $_" }
}
Unit 'badinput-check' { try { & (Join-Path $tests 'badinput-check.ps1') -Oml $oml -Tmp $tmp } catch { $global:fail++; Write-Host "FAIL badinput checks: $_" } }
Unit 'dashboard-check' -Group browser {
  try { & (Join-Path $tests 'dashboard-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL dashboard checks: $_" }
}
Unit 'setup-check' -Group hkcu -After 'rest.json' {
  try { & (Join-Path $tests 'setup-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL setup checks: $_" }
}
Unit 'selftest' -Group selftest {
  try { & (Join-Path $tests 'selftest-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL self-test on an unrecorded build: $_" }
  & $oml selftest | ConvertFrom-Json | ForEach-Object { if (-not $_.passed) { $global:fail++; Write-Host "FAIL selftest"; Write-Host ($_ | ConvertTo-Json -Depth 8) } else { Write-Host "ok   selftest" } }
}

# --- Smoke tier: -Smoke keeps only the quick units. The fixtures (tmp\base) are built once above, as for the full run. ---
# Chosen for speed and breadth: text and parser checks, the spec and doc tooling, hooks, the usage log, encoding, and a few apply fixtures.
$smokeUnits = 'corpus-check', 'map-check', 'scenarios-check', 'logic.json', 'service.json', 'web.json', 'datamodel.json', 'edits.json', 'edits-check', 'names-check', 'validate-check', 'parsers-check', 'encoding-check',
  'hooks-check', 'spec-check', 'doc-check', 'evolve-check', 'inspect-check', 'turns-check', 'log-check', 'aliases-check'
if ($Smoke) {
  $unknown = @($smokeUnits | Where-Object { $n = $_; -not ($units | Where-Object { $_.Name -eq $n }) })
  if ($unknown) { throw "-Smoke names units that do not exist: $($unknown -join ', ')" }
  $units = [System.Collections.Generic.List[object]]@($units | Where-Object { $smokeUnits -contains $_.Name })
  $missing = @($units | ForEach-Object { $_.After } | Where-Object { $_ } | Where-Object { $a = $_; -not ($units | Where-Object { $_.Name -eq $a }) } | Select-Object -Unique)
  if ($missing) { throw "-Smoke units wait for units that are not in the smoke tier: $($missing -join ', ')" }
  Write-Host ("smoke tier: {0} of the units" -f $units.Count)
}
# --- Scheduler. ---
$done = @{}
$nextPrint = 0
function Flush-Output {
  while ($script:nextPrint -lt $units.Count -and $done.ContainsKey($units[$script:nextPrint].Name)) {
    $r = $done[$units[$script:nextPrint].Name]
    Write-Host ("-- {0} ({1:N1}s)" -f $r.Name, $r.Seconds)
    if ($r.Text) { Write-Host $r.Text }
    $script:nextPrint++
  }
}
# Longest units first (times of the last run on this machine), so the long ones do not start last and set the total.
$timesFile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'oml-cli\regress-times.json'
$lastTimes = @{}
try { (Get-Content -Raw $timesFile | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $lastTimes[$_.Name] = [double]$_.Value } } catch { }
$order = @($units | Sort-Object @{ Expression = { if ($lastTimes.ContainsKey($_.Name)) { $lastTimes[$_.Name] } else { 1000 } }; Descending = $true })
$running = @{}
while ($true) {
  while ($running.Count -lt $Throttle) {
    $busy = @{}; foreach ($r in $running.Values) { if ($r.Group) { $busy[$r.Group] = 1 + [int]$busy[$r.Group] } }
    $u = $order | Where-Object { -not $_.Started -and @($_.After | Where-Object { -not $done.ContainsKey($_) }).Count -eq 0 -and
      (-not $_.Group -or [int]$busy[$_.Group] -lt $groupLimit[$_.Group]) } | Select-Object -First 1
    if (-not $u) { break }
    $u.Started = $true
    $u.Job = Start-Job -ScriptBlock $runner -ArgumentList $u.Name, $oml, $tests, $base, (Join-Path $tmp $u.Name), $preamble, $u.Body, $u.Publish
    $running[$u.Job.Id] = $u
  }
  if ($running.Count -eq 0) { break }
  $finished = Wait-Job -Job @($running.Values | ForEach-Object Job) -Any -Timeout 5
  foreach ($job in @($finished)) {
    $u = $running[$job.Id]; $running.Remove($job.Id)
    $res = Receive-Job $job -ErrorAction SilentlyContinue | Where-Object { $_ -is [pscustomobject] -and $_.PSObject.Properties['Fail'] } | Select-Object -Last 1
    if (-not $res) { $res = [pscustomobject]@{ Name = $u.Name; Fail = 1; Text = "FAIL $($u.Name): unit process ended without a result ($($job.State)) $($job.ChildJobs[0].JobStateInfo.Reason)" } }
    $res | Add-Member Seconds (($job.PSEndTime - $job.PSBeginTime).TotalSeconds) -Force
    $script:fail += $res.Fail
    $done[$u.Name] = $res
    Remove-Job $job -Force
  }
  Flush-Output
}
foreach ($u in @($units | Where-Object { -not $_.Started })) {
  $fail++
  $done[$u.Name] = [pscustomobject]@{ Name = $u.Name; Fail = 1; Text = "FAIL $($u.Name): never started, waiting for $($u.After -join ', ')"; Seconds = 0 }
}
if (-not $Smoke -and $fail -eq 0) {
  $endState = TreeState
  $record = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'oml-cli\regress-passed.json'
  if ($startState.Clean -and $endState.Clean -and $startState.Commit -eq $endState.Commit) {
    try { New-Item -ItemType Directory -Force (Split-Path $record) | Out-Null; [ordered]@{ commit = $endState.Commit; at = (Get-Date).ToString('o'); seconds = [int]$watch.Elapsed.TotalSeconds } | ConvertTo-Json | Set-Content $record; Write-Host "full suite passed on $($endState.Commit.Substring(0, 7)): recorded for release.ps1" } catch { }
  } else { Write-Host 'full suite passed, but the tree had uncommitted changes (or the commit changed): not recorded for a release' }
}
if (-not $Smoke) {
  try { New-Item -ItemType Directory -Force (Split-Path $timesFile) | Out-Null; $t = [ordered]@{}; foreach ($k in $done.Keys) { $t[$k] = [math]::Round($done[$k].Seconds, 1) }; $t | ConvertTo-Json | Set-Content $timesFile } catch { }
}Flush-Output
Write-Host ("{0}total {1:N0}s ({2:N1} min), {3} failures" -f $(if ($Smoke) { 'smoke tier: ' } else { '' }), $watch.Elapsed.TotalSeconds, $watch.Elapsed.TotalMinutes, $fail)
exit $fail
