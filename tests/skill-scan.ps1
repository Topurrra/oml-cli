#requires -Version 7.0
# Release step: scan skill/ with NVIDIA SkillSpector (https://github.com/NVIDIA/skillspector), offline.
# Runs `skillspector scan skill --no-llm --format json --baseline tests/skill-scan-baseline.yaml`. No LLM: file contents stay on
# this machine. The skill declares no packages, so SkillSpector's OSV.dev lookup has nothing to send; the proxy variables below
# point at a closed local port, so any request it tried would fail instead of leaving the machine.
# Fails when the score after the baseline is above -MaxScore (20 = SAFE) or the scan does not finish; lists every finding that
# the baseline does not cover. Skips with exit 0 when SkillSpector is not installed: set OML_SKILLSPECTOR to skillspector.exe,
# or put skillspector on PATH. Not part of tests/regress.ps1; release.ps1 runs it.
param([int]$MaxScore = 20, [string]$Skill, [string]$Baseline)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Skill) { $Skill = Join-Path $repo 'skill' }
if (-not $Baseline) { $Baseline = Join-Path $PSScriptRoot 'skill-scan-baseline.yaml' }

$exe = $env:OML_SKILLSPECTOR
if (-not $exe) { $exe = (Get-Command skillspector -ErrorAction SilentlyContinue)?.Source }
if (-not $exe -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
  Write-Host 'skill-scan: skipped, SkillSpector is not installed. Install it in a venv (Python 3.12+): pip install git+https://github.com/NVIDIA/skillspector, then set OML_SKILLSPECTOR to its skillspector.exe.'
  exit 0
}

$names = 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY', 'LANGSMITH_TRACING', 'LANGCHAIN_TRACING_V2', 'PYTHONUTF8', 'PYTHONIOENCODING'
$saved = @{}; foreach ($n in $names) { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
$err = [IO.Path]::GetTempFileName()
$prevEncoding = [Console]::OutputEncoding
try {
  foreach ($n in 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY') { [Environment]::SetEnvironmentVariable($n, 'http://127.0.0.1:9') }
  $env:NO_PROXY = ''; $env:LANGSMITH_TRACING = 'false'; $env:LANGCHAIN_TRACING_V2 = 'false'
  $env:PYTHONUTF8 = '1'; $env:PYTHONIOENCODING = 'utf-8'
  [Console]::OutputEncoding = [Text.Encoding]::UTF8
  $version = ((& $exe --version 2>$null) -join ' ').Trim()
  # SkillSpector refuses --output on Windows; the report comes on stdout. Exit 1 only means "findings"; the score decides here.
  $out = & $exe scan $Skill --no-llm --format json --baseline $Baseline 2>$err
  $code = $LASTEXITCODE
} finally {
  [Console]::OutputEncoding = $prevEncoding
  foreach ($n in $names) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
}
$stderr = (Get-Content -Raw -LiteralPath $err -ErrorAction SilentlyContinue); Remove-Item -LiteralPath $err -ErrorAction SilentlyContinue
if ($code -ge 2) { Write-Host "skill-scan: FAILED, SkillSpector stopped with exit $code.`n$stderr"; exit 1 }
try { $report = ($out -join "`n") | ConvertFrom-Json } catch { Write-Host "skill-scan: FAILED, the report is not JSON.`n$stderr"; exit 1 }
if (-not $report.execution_successful) { Write-Host 'skill-scan: FAILED, the scan did not finish (execution_successful is false).'; exit 1 }

$risk = $report.risk_assessment
$open = @($report.issues)
$cov = $report.analysis_completeness
Write-Host ("skill-scan: {0}, --no-llm: score {1}/100, {2} after the baseline; {3} reviewed findings suppressed, {4} open; coverage {5}% ({6} of {7} files whole)." -f `
  $version, $risk.score, $risk.recommendation, $report.suppressed_count, $open.Count, $cov.coverage_percent, $cov.fully_inspected_files, $cov.total_components)
foreach ($i in $open) {
  Write-Host ("  {0} {1} {2}:{3} '{4}' - {5}" -f $i.severity, $i.id, $i.location.file, $i.location.start_line, $i.finding, $i.pattern)
}
if ($risk.score -gt $MaxScore) {
  Write-Host "skill-scan: FAILED, score $($risk.score) is above $MaxScore. Fix a true finding in skill/, or review a false one and add a narrow rule (id, path, message, reason) to tests/skill-scan-baseline.yaml."
  exit 1
}
if ($open.Count) { Write-Host 'skill-scan: passed with open findings above; review them before the release.' }
exit 0
