# Checks `oml run <steps.json>`: result shape, same output as separate commands, module cache, refusals,
# stopping on exit code 2, --continue, stdin input, and the time of 5 separate processes against one run.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($condition, $message) { if (-not $condition) { throw $message } }
function Same($a, $b) { ($a | ConvertTo-Json -Depth 50 -Compress) -ceq ($b | ConvertTo-Json -Depth 50 -Compress) }
function Run-Steps($steps, [string[]]$Options = @(), $Stdin = $false) {
  $file = Join-Path $dir ('steps-' + [guid]::NewGuid().ToString('N') + '.json')
  $json = @{ steps = $steps } | ConvertTo-Json -Depth 10 -Compress
  if ($Stdin) { $text = $json | & $Oml run - @Options } else { Set-Content $file $json -Encoding utf8; $text = & $Oml run $file @Options }
  $code = $LASTEXITCODE
  [pscustomobject]@{ Code = $code; Json = (($text -join "`n") | ConvertFrom-Json) }
}
$dir = Join-Path $Tmp ('run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $dir | Out-Null

# A module built here: the data model fixture on a blank module, plus a spec that adds one entity.
$blank = Join-Path $dir 'blank.oml'; $mod = Join-Path $dir 'RunApp.oml'
& $Oml new $blank --name RunApp --kind CrossDevice | Out-Null
$built = & $Oml apply $blank (Join-Path $PSScriptRoot 'datamodel.json') $mod | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $built.saved) 'run-check: could not build the test module'
$spec = Join-Path $dir 'widget.json'
Set-Content $spec '{ "entities": [ { "name": "Widget", "attributes": [ { "name": "Label", "type": "Text" } ] } ] }' -Encoding utf8

# 5 steps as one run.
$staged = Join-Path $dir 'Staged.oml'
$steps = @(
  @('inspect', $mod, '--own', '--only', 'entities'),
  @('describe', $mod, 'FetchOrder'),
  @('apply', $mod, $spec, $staged, '--describe', 'Widget'),
  @('validate', $staged, '--warnings'),
  @('describe', $staged, 'Widget')
)
$clock = [Diagnostics.Stopwatch]::StartNew()
$run = Run-Steps $steps
$runWall = $clock.ElapsedMilliseconds
Require ($run.Code -eq 0) "run: exit code $($run.Code), expected 0"
$r = $run.Json.results
Require ($r.Count -eq 5 -and $run.Json.ms -ge 0) 'run: results shape (count, total ms)'
for ($i = 0; $i -lt 5; $i++) {
  Require ($r[$i].step -eq $i + 1 -and (Same @($r[$i].args) $steps[$i]) -and $r[$i].exitCode -eq 0 -and $r[$i].ms -ge 0 -and $null -ne $r[$i].output) "run: step $($i + 1) shape"
}
Require ($r[2].output.saved -eq $true -and $r[2].output.describe.Widget) 'run: apply step output (saved, describe)'
Write-Host "ok   run: results shape for 5 steps (inspect, describe, apply, validate, describe)"

# The same output as separate commands, on the file the run wrote.
$separate = [Diagnostics.Stopwatch]::StartNew()
$one = & $Oml inspect $mod --own --only entities | ConvertFrom-Json
$two = & $Oml describe $mod FetchOrder | ConvertFrom-Json
$four = & $Oml validate $staged --warnings | ConvertFrom-Json
$five = & $Oml describe $staged Widget | ConvertFrom-Json
# inspect lists entities in a different order on each process start (also without run), so compare it as a set.
Require ((Same ($r[0].output.entities.name | Sort-Object) ($one.entities.name | Sort-Object)) -and (Same $r[1].output $two) -and (Same $r[3].output $four) -and (Same $r[4].output $five)) 'run: output differs from the separate command'
Write-Host 'ok   run: step output equals the separate command output'

# Timing: 5 separate processes against one run (each separate process loads Service Studio's model again).
$staged2 = Join-Path $dir 'Staged2.oml'
$separate.Restart()
& $Oml inspect $mod --own --only entities | Out-Null
& $Oml describe $mod FetchOrder | Out-Null
& $Oml apply $mod $spec $staged2 --describe Widget | Out-Null
& $Oml validate $staged2 --warnings | Out-Null
& $Oml describe $staged2 Widget | Out-Null
$separateWall = $separate.ElapsedMilliseconds
Require ($runWall -lt $separateWall) "run: one run ($runWall ms) was not faster than 5 processes ($separateWall ms)"
Write-Host "ok   run: 5 steps in one run $runWall ms, as 5 processes $separateWall ms"

# Cache: the second describe of the same module skips the load. The first step also starts Service Studio's model.
$cache = Run-Steps @(@('describe', $mod, 'FetchOrder'), @('describe', $mod, 'FetchOrder'), @('describe', $mod, 'FetchOrder'))
Require ($cache.Code -eq 0 -and $cache.Json.results[1].ms -lt $cache.Json.results[0].ms -and $cache.Json.results[2].ms -lt $cache.Json.results[0].ms) `
  "run: cached describe was not faster ($($cache.Json.results.ms -join ', ') ms)"
Require (Same $cache.Json.results[1].output $cache.Json.results[0].output) 'run: cached describe changed its output'
Write-Host "ok   run: module cache (step ms $($cache.Json.results.ms -join ', '))"

# A module that a step writes is new to the next step: a missing file fails, then apply makes it, then it loads.
$later = Join-Path $dir 'Later.oml'
$fresh = Run-Steps @(@('describe', $later, 'Widget'), @('apply', $mod, $spec, $later), @('describe', $later, 'Widget')) @('--continue')
Require ($fresh.Json.results[0].exitCode -eq 2 -and $fresh.Json.results[1].exitCode -eq 0 -and $fresh.Json.results[2].exitCode -eq 0) 'run: a file written by apply was not loaded by the next step'
Write-Host 'ok   run: a module written by apply is loaded fresh by the next step'

# Refusals: commands that need a person's confirmation, and run itself.
$refused = Run-Steps @(@('install', 'a.oml', 'b.oml'), @('publish', 'X'), @('references', 'X'), @('pull'), @('lt', 'environments'), @('run', 'x.json'), @('architecture', '--team', 'T'), @('preview', 'x.oml', 'B', '--serve'), @('new', 'x.oml')) @('--continue')
Require ($refused.Code -eq 2 -and $refused.Json.results.Count -eq 9) 'run: refusals shape'
foreach ($x in $refused.Json.results) { Require ($x.exitCode -eq 2 -and $x.output.error -match 'inside .oml run.') "run: step '$($x.args[0])' was not refused clearly: $($x.output.error)" }
Require ($refused.Json.results[0].output.error -match "'install' is not allowed inside 'oml run'") 'run: install refusal text'
Write-Host 'ok   run: install, publish, references, pull, lt, run, architecture --team, preview --serve refused'

# Exit code 2 stops the run; exit code 1 does not; --continue goes on after 2; the run exits with the highest code.
$stop = Run-Steps @(@('install', 'a.oml', 'b.oml'), @('inspect', $mod, '--own'))
Require ($stop.Code -eq 2 -and $stop.Json.results.Count -eq 1 -and $stop.Json.stoppedAfterStep -eq 1) 'run: exit code 2 did not stop the run'
$go = Run-Steps @(@('install', 'a.oml', 'b.oml'), @('inspect', $mod, '--own')) @('--continue')
Require ($go.Code -eq 2 -and $go.Json.results.Count -eq 2 -and $go.Json.results[1].exitCode -eq 0) 'run: --continue did not run the step after exit code 2'
$soft = Run-Steps @(@('describe', $mod, 'FetchOrder', 'NoSuchElement'), @('inspect', $mod, '--own'))
Require ($soft.Code -eq 1 -and $soft.Json.results.Count -eq 2 -and $soft.Json.results[0].exitCode -eq 1 -and $soft.Json.results[1].exitCode -eq 0) 'run: exit code 1 stopped the run or the run exit code is not the highest'
Write-Host 'ok   run: stop on exit 2, --continue, exit 1 does not stop, highest exit code'

# spec runs in the launcher; its text output comes back as a string, its JSON as JSON.
$specStep = Run-Steps @(@('spec', 'check', (Join-Path $PSScriptRoot 'datamodel.json')), @('spec', 'entities'))
Require ($specStep.Code -eq 0 -and $specStep.Json.results[0].output.ok -eq $true -and $specStep.Json.results[1].output -is [string]) 'run: spec steps'
Write-Host 'ok   run: spec steps'

# stdin, and bad input.
$stdin = Run-Steps @(@('inspect', $mod, '--own'), @('validate', $mod)) @() $true
Require ($stdin.Code -eq 0 -and $stdin.Json.results.Count -eq 2 -and $stdin.Json.results[0].output.name -eq 'RunApp') 'run: steps from stdin'
Write-Host 'ok   run: steps from stdin ("-")'
foreach ($bad in @('{}', '{ "steps": [] }', '{ "steps": [ "inspect" ] }', '{ "steps": [ [] ] }', '{ "steps": [ ["a", 1] ] }', 'not json')) {
  $file = Join-Path $dir 'bad.json'; Set-Content $file $bad -Encoding utf8
  $text = & $Oml run $file; $code = $LASTEXITCODE
  Require ($code -eq 2 -and ($text -join '') -match '"error"') "run: bad steps file was not refused with exit 2: $bad"
}
$text = & $Oml run (Join-Path $dir 'missing.json'); Require ($LASTEXITCODE -eq 2 -and ($text -join '') -match 'Cannot read') 'run: missing steps file'
Write-Host 'ok   run: bad steps files refused with exit code 2'
