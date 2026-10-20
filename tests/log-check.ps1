# Checks the opt-in usage log (OML_LOG) and `oml log summary`: nothing written without the variable, one line per
# command and per run step with it, no folder, token or argument contents in the file, and the summary numbers.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($condition, $message) { if (-not $condition) { throw $message } }
$dir = Join-Path $Tmp ('log-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $dir | Out-Null
$default = Join-Path $env:LOCALAPPDATA 'oml-cli\usage.jsonl'
$saved = @{}; foreach ($name in 'OML_LOG', 'OML_LIFETIME_TOKEN', 'OML_LIFETIME_HOST') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
function Lines($file) { if (Test-Path $file) { @(Get-Content $file | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }) } else { @() } }
try {
  $mod = Join-Path $dir 'LogApp.oml'
  $env:OML_LOG = $null
  & $Oml new $mod --name LogApp --kind CrossDevice | Out-Null
  Require ($LASTEXITCODE -eq 0) 'log-check: could not build the test module'

  # 1. No variable: nothing is written (not to the default file, not anywhere).
  $before = if (Test-Path $default) { (Get-Item $default).Length } else { -1 }
  & $Oml version | Out-Null; & $Oml inspect $mod --own | Out-Null; & $Oml spec check (Join-Path $PSScriptRoot 'datamodel.json') | Out-Null
  $after = if (Test-Path $default) { (Get-Item $default).Length } else { -1 }
  Require ($before -eq $after) 'log: a file was written without OML_LOG'
  foreach ($off in @('0', '')) {
    $env:OML_LOG = $off; & $Oml version | Out-Null
    $after = if (Test-Path $default) { (Get-Item $default).Length } else { -1 }
    Require ($before -eq $after) "log: OML_LOG='$off' wrote the default file"
  }
  Write-Host 'ok   log: nothing is written without OML_LOG (unset, 0, empty)'

  # 2. A file path: one line per command, one per run step.
  $log = Join-Path $dir 'nested\folder\usage.jsonl'
  $secretFolder = Join-Path $dir 'secret folder with spaces'
  $token = 'fake-token-' + [guid]::NewGuid().ToString('N')
  $env:OML_LOG = $log; $env:OML_LIFETIME_TOKEN = $token; $env:OML_LIFETIME_HOST = ''
  $version = & $Oml version
  & $Oml inspect $mod --own --only entities | Out-Null
  & $Oml describe $mod 'NoSuchThing' | Out-Null; Require ($LASTEXITCODE -eq 2) 'log-check: describe should fail'
  & $Oml inspect (Join-Path $secretFolder 'Missing.oml') | Out-Null
  & $Oml spec check (Join-Path $PSScriptRoot 'datamodel.json') | Out-Null
  & $Oml lt environments | Out-Null; Require ($LASTEXITCODE -eq 2) 'log-check: lt should fail without a host'
  & $Oml bogus-command 2>$null | Out-Null
  $steps = Join-Path $dir 'steps.json'
  Set-Content $steps (@{ steps = @(@('inspect', $mod, '--own'), @('install', 'a.oml', 'b.oml'), @('validate', $mod)) } | ConvertTo-Json -Depth 5 -Compress) -Encoding utf8
  & $Oml run $steps --continue | Out-Null
  $rows = Lines $log
  # version, inspect, describe, inspect (missing), spec check, lt environments, bogus-command, run + 3 steps = 11
  Require ($rows.Count -eq 11) "log: expected 11 lines, got $($rows.Count): $(($rows.cmd) -join ', ')"
  Require ((($rows.cmd) -join ',') -ceq 'version,inspect,describe,inspect,spec check,lt environments,bogus-command,inspect,install,validate,run') `
    "log: command names are $(($rows.cmd) -join ',')"
  foreach ($row in $rows) {
    Require ($row.ts -and $row.exit -ge 0 -and $row.ms -ge 0 -and $row.bytes -ge 0) "log: line shape: $($row | ConvertTo-Json -Compress)"
  }
  Require ($rows[0].exit -eq 0 -and -not $rows[0].error -and $rows[0].bytes -ge ([Text.Encoding]::UTF8.GetByteCount($version) - 2)) 'log: version line (exit, bytes)'
  Require ($rows[1].module -ceq 'LogApp.oml' -and $rows[1].bytes -ge 3) 'log: module file name and output size'
  Require ($rows[2].exit -eq 2 -and $rows[2].error -match 'NoSuchThing' -and $rows[2].error.Length -le 200) 'log: failure message'
  Require ($rows[3].error -ceq 'File not found: Missing.oml' -and $rows[3].module -ceq 'Missing.oml') "log: folders must be removed from messages: '$($rows[3].error)'"
  Require ($rows[5].exit -eq 2 -and $rows[5].error -match 'OML_LIFETIME_HOST') 'log: lt failure'
  Require ($rows[6].exit -eq 2 -and $rows[6].error) 'log: unknown command failure'
  Require (($rows[7..9] | Where-Object { $_.run -eq $true }).Count -eq 3 -and -not $rows[10].run) 'log: run steps are marked, the run line is not'
  Require ($rows[8].exit -eq 2 -and $rows[8].error -match 'not allowed') 'log: refused run step'
  Require (@(Get-Content $log | Where-Object { $_ -notmatch '"ts":"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z"' }).Count -eq 0) 'log: ts is not an ISO UTC time'
  Write-Host 'ok   log: one line per command and per run step, file name only, failure message'

  # 3. Nothing private in the file: no folder, no token, no environment value, no argument contents.
  $text = Get-Content $log -Raw
  foreach ($private in @($dir, $secretFolder, 'secret folder', $token, $env:USERPROFILE, $env:LOCALAPPDATA, 'datamodel.json')) {
    Require (-not $text.Contains($private)) "log: the file contains '$private'"
  }
  Write-Host 'ok   log: no folder, token, environment value or argument content in the file'

  # 4. A bad log path never breaks the command.
  $env:OML_LOG = Join-Path $dir 'file.txt\inside.jsonl'; Set-Content (Join-Path $dir 'file.txt') 'x'
  $v = & $Oml version | ConvertFrom-Json
  Require ($LASTEXITCODE -eq 0 -and $v.oml) 'log: an unusable log path broke the command'
  Write-Host 'ok   log: an unusable log path does not break the command'

  # 5. Summary.
  $env:OML_LOG = $null
  $summary = & $Oml log summary --file $log | ConvertFrom-Json
  Require ($LASTEXITCODE -eq 0 -and $summary.lines -eq 11) 'log summary: line count'
  $inspect = $summary.commands | Where-Object command -eq 'inspect'
  Require ($inspect.count -eq 3 -and $inspect.failures -eq 1 -and $inspect.medianMs -ge 0) 'log summary: inspect count, failures, median'
  Require ($inspect.topFailures[0].message -ceq 'File not found: Missing.oml' -and $inspect.topFailures[0].count -eq 1) 'log summary: top failure message'
  Require ($summary.commands[0].command -ceq 'inspect') 'log summary: most used command first'
  # Most common failures first, at most 5.
  $many = Join-Path $dir 'many.jsonl'
  $generated = foreach ($i in 1..20) { $m = 'problem ' + [char](97 + ($i % 7)); '{"ts":"2026-01-01T00:00:00.000Z","cmd":"apply","exit":1,"ms":' + ($i * 10) + ',"bytes":5,"error":"' + $m + '"}' }
  $generated | Set-Content $many; 'not json' | Add-Content $many
  $s2 = & $Oml log summary --file $many | ConvertFrom-Json
  $apply = $s2.commands[0]
  Require ($apply.count -eq 20 -and $apply.failures -eq 20 -and $apply.medianMs -eq 105 -and $apply.topFailures.Count -eq 5) 'log summary: median of 20, 5 top failures'
  Require ($apply.topFailures[0].count -ge $apply.topFailures[4].count) 'log summary: failures are sorted by count'
  $missing = & $Oml log summary --file (Join-Path $dir 'none.jsonl'); Require ($LASTEXITCODE -eq 2 -and ($missing -join '') -match 'No usage log') 'log summary: missing file'
  Write-Host 'ok   log summary: count, failures, median, top 5 failures'

  # 6. OML_LOG=1 writes the default file. Only the line added here is removed again.
  $env:OML_LOG = '1'
  $length = if (Test-Path $default) { (Get-Item $default).Length } else { 0 }
  try {
    & $Oml version | Out-Null
    $last = Get-Content $default -Tail 1 | ConvertFrom-Json
    Require ($last.cmd -ceq 'version' -and (Get-Item $default).Length -gt $length) 'log: OML_LOG=1 did not write the default file'
  } finally {
    if (Test-Path $default) { $stream = [IO.File]::Open($default, 'Open', 'Write', 'ReadWrite'); try { $stream.SetLength($length) } finally { $stream.Dispose() } }
    if ($length -eq 0 -and (Test-Path $default)) { Remove-Item $default }
  }
  Write-Host 'ok   log: OML_LOG=1 writes %LOCALAPPDATA%\oml-cli\usage.jsonl'
} finally {
  foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}
