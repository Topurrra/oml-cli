# Regression: every fixture must apply with saved=true and no new errors. Run from the repo root after a build.
$ErrorActionPreference = "Stop"
$oml = Join-Path $PSScriptRoot "..\src\bin\Release\net8.0\oml.exe"
$tmp = Join-Path $PSScriptRoot "..\tmp"; New-Item -ItemType Directory -Force $tmp | Out-Null
$env:TEMP = $tmp; $env:TMP = $tmp
$reactive = Join-Path $tmp "r0.oml"; $service = Join-Path $tmp "s0.oml"
Remove-Item "$tmp\*.oml" -ErrorAction SilentlyContinue
& $oml new $reactive --name Reg --kind CrossDevice | Out-Null
& $oml new $service --name RegSvc --kind Service | Out-Null
$web = Join-Path $tmp "w0.oml"; & $oml new $web --name RegWeb --kind WebResponsive | Out-Null
$fail = 0
& $oml --help | Out-Null; if ($LASTEXITCODE -ne 0) { $fail++; Write-Host "FAIL --help exit $LASTEXITCODE" } else { Write-Host "ok   --help" }
try { & (Join-Path $PSScriptRoot 'logic-check.ps1') $oml $tmp }
catch { $fail++; Write-Host "FAIL extended logic checks: $_" }
foreach ($fixture in @('sql.json','delete.json','static.json')) {
  $r = & $oml apply $reactive (Join-Path $PSScriptRoot $fixture) (Join-Path $tmp "out-$fixture.oml") | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0 -or -not $r.saved -or $r.reopenNewErrors.Count -gt 0) { $fail++; Write-Host "FAIL $fixture"; $r | ConvertTo-Json -Depth 8 } else { Write-Host "ok   $fixture" }
  if ($fixture -eq 'delete.json' -and -not ($r.warnings -join ' ' -match 'public entity')) { $fail++; Write-Host 'FAIL public deletion warning' }
}
try { & (Join-Path $PSScriptRoot 'rules-check.ps1') $oml $tmp }
catch { $fail++; Write-Host "FAIL production rules: $_" }
try { & (Join-Path $PSScriptRoot 'sql-delete-check.ps1') $oml $tmp }
catch { $fail++; Write-Host "FAIL SQL/delete checks: $_" }
& $oml new "$tmp\references-producer-blank.oml" --name ReferenceProducer --kind CrossDevice | Out-Null
& $oml apply "$tmp\references-producer-blank.oml" (Join-Path $PSScriptRoot 'references-producer.json') "$tmp\references-producer.oml" | Out-Null
try { & (Join-Path $PSScriptRoot 'names-check.ps1') -Oml $oml -Tmp $tmp; Write-Host 'ok   qualified names, ambiguity, find' }
catch { $fail++; Write-Host "FAIL names checks: $_" }
try { & (Join-Path $PSScriptRoot 'references-check.ps1') $oml $tmp $reactive }
catch { $fail++; Write-Host "FAIL references checks: $_" }
foreach ($case in @(@("logic.json",$reactive),@("block.json",$reactive),@("service.json",$service),@("datamodel.json",$reactive),@("edits.json",(Join-Path $tmp "out-logic.json.oml")),@("queries.json",$reactive),@("scaffold.json",$reactive),@("rest.json",$reactive),@("platform.json",$reactive),@("web.json",$web),@("references.json",$reactive))) {
  $out = Join-Path $tmp ("out-" + $case[0] + ".oml")
  $r = & $oml apply $case[1] (Join-Path $PSScriptRoot $case[0]) $out 2>$null | ConvertFrom-Json
  if (-not $r.saved -or $r.reopenNewErrors.Count -gt 0) { $fail++; Write-Host "FAIL $($case[0])"; $r | ConvertTo-Json -Depth 8 } else { Write-Host "ok   $($case[0])" }
  if ($case[0] -eq 'queries.json' -and $r.saved) {
    try { & (Join-Path $PSScriptRoot 'queries-check.ps1') $oml $out $case[1] }
    catch { $fail++; Write-Host "FAIL queries wiring: $_" }
  }
  if ($case[0] -eq 'rest.json' -and $r.saved) {
    try { & (Join-Path $PSScriptRoot 'rest-check.ps1') $oml $out }
    catch { $fail++; Write-Host "FAIL REST wiring: $_" }
  }
}
try { & (Join-Path $PSScriptRoot "edits-check.ps1") -Oml $oml -Tmp $tmp }
catch { $fail++; Write-Host "FAIL edits checks: $_" }
try { & (Join-Path $PSScriptRoot "describe-check.ps1") -Oml $oml -Tmp $tmp }
catch { $fail++; Write-Host "FAIL describe checks: $_" }
try { & (Join-Path $PSScriptRoot 'content-check.ps1') $oml $tmp $reactive }
catch { $fail++; Write-Host "FAIL content checks: $_" }
try { & (Join-Path $PSScriptRoot "lifetime-check.ps1") -Oml $oml -Tmp $tmp } catch { $fail++; Write-Host "FAIL LifeTime checks: $_" }
try { & (Join-Path $PSScriptRoot 'recipes-check.ps1') -Oml $oml }
catch { $fail++; Write-Host "FAIL recipes checks: $_" }
try { & (Join-Path $PSScriptRoot "packages-check.ps1") -Oml $oml -Tmp $tmp } catch { $fail++; Write-Host "FAIL packages checks: $_" }
try { & (Join-Path $PSScriptRoot 'assets-check.ps1') -Oml $oml -Tmp $tmp } catch { $fail++; Write-Host "FAIL assets checks: $_" }
& $oml selftest | ConvertFrom-Json | ForEach-Object { if (-not $_.passed) { $fail++; Write-Host "FAIL selftest"; $_ | ConvertTo-Json -Depth 8 } else { Write-Host "ok   selftest" } }
exit $fail
