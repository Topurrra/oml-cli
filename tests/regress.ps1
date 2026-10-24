# Regression: every fixture must apply with saved=true and no new errors. Run from the repo root after a build.
# Needs PowerShell 7. Usage: pwsh -NoProfile -File tests\regress.ps1 [-Throttle 4]
#
# How it runs. Each check is a "unit". A unit runs in its own pwsh process (Start-Job) with its own work folder
# tmp\<unit> and its own TEMP, so units cannot clash on files or on process-wide environment variables.
#   1. Setup (here, one at a time): the blank modules and the producer module that most units start from, in tmp\base.
#   2. Parallel lane (up to -Throttle units at once): every unit starts from a copy of tmp\base. A unit with
#      -After waits for the units whose outputs it reads. A unit with -Publish moves the out-*.oml files it made into tmp\base.
#   3. Alone lane (one at a time, after the parallel lane): units that must not share the machine with others:
#        lifetime-check      binds a local port, starts a mock server job and sets OML_LIFETIME_* and OML_SERVICE_STUDIO
#        doctor-check        sets OML_* variables and reads machine-wide state (the Service Studio folder, the self-test record)
#        preview-check       starts headless Edge/Chrome with timeouts; CPU load from other units would make it flaky
#        test-scaffold-check same: one headless browser run (a preview of the generated test screen)
#        architecture-page-check  same: several headless browser runs, each with a 30 to 60 s window
#        check-page-check    same: headless browser with a 20 s window, for the oml check HTML report
#        review-page-check   same, for the oml review HTML report
#        image-check         headless Chrome with a 30 s window, and a timing limit (a 1024 px trace in under 5 s)
#        log-check           appends to and trims the person's default usage log (OML_LOG=1)
#        dashboard-check     binds a local port for oml dashboard and starts headless Edge/Chrome for screenshots
#        setup-check         writes to HKCU (only under a throwaway HKCU\Software\oml-setup-test-<id> key) and compares real
#                            HKCU\Software\Classes keys before and after, so nothing else may change them meanwhile
#        selftest            long, CPU heavy, and it records the build as checked in %LOCALAPPDATA%; selftest-check
#                            uses its own record (OML_SELFTEST_RECORD) and starts oml dashboard on a free port
#      Processes are already separate, so most would be safe in parallel; they stay alone for load and shared-state reasons.
# Output is printed per unit in the order of the list below (the order of the old script), not in finish order.
# To add a check: see CONTRIBUTING.md ("Adding a regression check").
param([int]$Throttle = 4)
$ErrorActionPreference = "Stop"
$watch = [Diagnostics.Stopwatch]::StartNew()
$tests = $PSScriptRoot
$oml = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\src\bin\Release\net8.0\oml.exe"))
$tmp = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\tmp"))
New-Item -ItemType Directory -Force $tmp | Out-Null
Get-ChildItem $tmp -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
$base = Join-Path $tmp "base"; New-Item -ItemType Directory -Force $base | Out-Null
$env:TEMP = $tmp; $env:TMP = $tmp
$env:OML_LOG = $null # the suite must not write to a person's usage log
$fail = 0

# --- Setup: the blank modules every unit starts from. ---
& $oml new (Join-Path $base "r0.oml") --name Reg --kind CrossDevice | Out-Null
& $oml new (Join-Path $base "s0.oml") --name RegSvc --kind Service | Out-Null
& $oml new (Join-Path $base "w0.oml") --name RegWeb --kind WebResponsive | Out-Null
& $oml new (Join-Path $base "references-producer-blank.oml") --name ReferenceProducer --kind CrossDevice | Out-Null
& $oml apply (Join-Path $base "references-producer-blank.oml") (Join-Path $tests 'references-producer.json') (Join-Path $base "references-producer.oml") | Out-Null
# references.json names this producer as tmp/references-producer.oml, relative to the repo root.
Copy-Item (Join-Path $base "references-producer.oml") $tmp
& $oml --help | Out-Null; if ($LASTEXITCODE -ne 0) { $fail++; Write-Host "FAIL --help exit $LASTEXITCODE" } else { Write-Host "ok   --help" }
# `oml <command> --help` prints that command's usage, not the whole list.
$helpBad = @(foreach ($c in 'check', 'doc', 'warn', 'apply', 'lt', 'font', 'doctor', 'setup', 'image', 'dashboard') { $h = (& $oml $c --help) -join "`n"; if ($LASTEXITCODE -ne 0 -or $h -notmatch "oml $c" -or $h -match 'oml new ') { $c } })
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
  Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
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
function Unit([string]$Name, [scriptblock]$Body, [string[]]$After = @(), [switch]$Publish, [switch]$Alone) {
  $units.Add([pscustomobject]@{ Name = $Name; Body = $Body.ToString(); After = $After; Publish = [bool]$Publish; Alone = [bool]$Alone; Job = $null; Started = $false })
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
Unit 'lifetime-check' -Alone {
  try { & (Join-Path $tests "lifetime-check.ps1") -Oml $oml -Tmp $tmp } catch { $global:fail++; Write-Host "FAIL LifeTime checks: $_" }
}
# log-check also appends to and trims the person's default usage log (OML_LOG=1), so it runs alone.
Unit 'log-check' -Alone {
  try { & (Join-Path $tests 'log-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL log checks: $_" }
}
Unit 'pull-check' {
  try { & (Join-Path $tests "pull-check.ps1") -Oml $oml -Tmp $tmp } catch { $global:fail++; Write-Host "FAIL pull checks: $_" }
}
Unit 'validate-check' {
  try { & (Join-Path $tests 'validate-check.ps1') -Oml $oml -Tmp $tmp } catch { $global:fail++; Write-Host "FAIL validate checks: $_" }
}
Unit 'doctor-check' -Alone {
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
Unit 'memory-check' {
  try { & (Join-Path $tests 'memory-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL memory checks: $_" }
}
Unit 'library-check' {
  try { & (Join-Path $tests 'library-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL library checks: $_" }
}
Unit 'skills-check' {
  try { & (Join-Path $tests 'skills-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL skills checks: $_" }
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
Unit 'image-check' -Alone {
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
Unit 'preview-check' -Alone {
  try { & (Join-Path $tests 'preview-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL preview checks: $_" }
}
Unit 'test-scaffold-check' -Alone {
  try { & (Join-Path $tests 'test-scaffold-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL test scaffold: $_" }
}
Unit 'architecture-page-check' -Alone {
  try { & (Join-Path $tests 'architecture-page-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL architecture page: $_" }
}
Unit 'check-page-check' -Alone {
  try { & (Join-Path $tests 'check-page-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL check page: $_" }
}
Unit 'review-page-check' -Alone {
  try { & (Join-Path $tests 'review-check.ps1') -Oml $oml -Tmp $tmp -Page }
  catch { $global:fail++; Write-Host "FAIL review page: $_" }
}
Unit 'dashboard-check' -Alone {
  try { & (Join-Path $tests 'dashboard-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL dashboard checks: $_" }
}
Unit 'setup-check' -Alone -After 'rest.json' {
  try { & (Join-Path $tests 'setup-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL setup checks: $_" }
}
Unit 'selftest' -Alone {
  try { & (Join-Path $tests 'selftest-check.ps1') -Oml $oml -Tmp $tmp }
  catch { $global:fail++; Write-Host "FAIL self-test on an unrecorded build: $_" }
  & $oml selftest | ConvertFrom-Json | ForEach-Object { if (-not $_.passed) { $global:fail++; Write-Host "FAIL selftest"; Write-Host ($_ | ConvertTo-Json -Depth 8) } else { Write-Host "ok   selftest" } }
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
function Run-Lane($lane, [int]$limit) {
  $running = @{}
  while ($true) {
    while ($running.Count -lt $limit) {
      $u = $lane | Where-Object { -not $_.Started -and @($_.After | Where-Object { -not $done.ContainsKey($_) }).Count -eq 0 } | Select-Object -First 1
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
  foreach ($u in @($lane | Where-Object { -not $_.Started })) {
    $script:fail++
    $done[$u.Name] = [pscustomobject]@{ Name = $u.Name; Fail = 1; Text = "FAIL $($u.Name): never started, waiting for $($u.After -join ', ')"; Seconds = 0 }
  }
}
Run-Lane @($units | Where-Object { -not $_.Alone }) $Throttle
Run-Lane @($units | Where-Object { $_.Alone }) 1
Flush-Output
Write-Host ("total {0:N0}s, {1} failures" -f $watch.Elapsed.TotalSeconds, $fail)
exit $fail
