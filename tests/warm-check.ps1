# Checks the warm workers (oml warm, performance.* in .oml/oml-config.json): a command a worker runs prints the same
# bytes on stdout and stderr, with the same exit code, as the same command run cold; the second warm call is far faster
# than a cold one (the numbers are printed); a changed module file loads again; apply through a worker writes the same
# staged file as a cold apply; parallel callers beyond the pool size all succeed (the extra ones run cold);
# performance.warm false runs cold; a killed worker and a stopped pool fall back to cold; oml warm stop stops them.
# The rest of the suite runs cold (OML_WARM=0 in regress.ps1); this check clears that for its own commands and stops
# every worker of this build at the end, so it never leaves a process behind.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Tmp = New-TestFolder $Tmp 'warm'
function Require($condition, $message) { if (-not $condition) { throw $message } }

# One oml process with exact bytes: stdout and stderr as UTF-8 text (oml writes UTF-8 to a pipe), exit code, wall time.
function Invoke-Oml([string[]]$Arguments, [hashtable]$Environment = @{}, [string]$Cwd = $Tmp) {
  $psi = [Diagnostics.ProcessStartInfo]::new($Oml)
  foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
  $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.RedirectStandardInput = $true
  $psi.WorkingDirectory = $Cwd
  foreach ($k in $Environment.Keys) { if ($null -eq $Environment[$k]) { [void]$psi.Environment.Remove($k) } else { $psi.Environment[$k] = [string]$Environment[$k] } }
  $watch = [Diagnostics.Stopwatch]::StartNew()
  $p = [Diagnostics.Process]::Start($psi)
  $p.StandardInput.Close()
  $out = [IO.MemoryStream]::new(); $err = [IO.MemoryStream]::new()
  $o = $p.StandardOutput.BaseStream.CopyToAsync($out); $e = $p.StandardError.BaseStream.CopyToAsync($err)
  $p.WaitForExit(); $o.Wait(); $e.Wait()
  [pscustomobject]@{ Code = $p.ExitCode; Out = [Text.Encoding]::UTF8.GetString($out.ToArray()); Err = [Text.Encoding]::UTF8.GetString($err.ToArray()); Ms = $watch.ElapsedMilliseconds }
}
# What differs between two cold runs too: the time a report was made, how long it took (batch "ms", review
# "elapsedSeconds"), and the order of a flow's edges (it changes from one process to the next). Everything else must match byte for byte.
function Normal([string]$text) {
  $text = $text -creplace '"generatedAt":"[^"]*"', '"generatedAt":""' -creplace '"(elapsedSeconds|seconds)":[0-9.]+', '"$1":0' -creplace '"ms":\d+', '"ms":0'
  # The human view: the same times, and seconds at the end of a line (elapsedSeconds, a module's time in a check table).
  $text = $text -creplace '\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?(Z|[+-]\d\d:\d\d)?', '<time>' -creplace '(?m)\b\d+\.\d{1,3}[ \t]*(?=\r?$)', '<seconds>'
  if ($text -cmatch '^\{"element":.*"edges":\[') {
    $flow = $text | ConvertFrom-Json
    $flow.edges = @($flow.edges | Sort-Object from, to, kind)
    $text = $flow | ConvertTo-Json -Depth 30 -Compress
  }
  $text
}
function Same($a, $b) { $a.Code -eq $b.Code -and (Normal $a.Out) -ceq (Normal $b.Out) -and (Normal $a.Err) -ceq (Normal $b.Err) }
$cold = @{ OML_WARM = '0' }
$warm = @{ OML_WARM = '1' }
function Status { (Invoke-Oml @('warm', 'status') $warm).Out | ConvertFrom-Json }
function Ready-Workers { @((Status).running | Where-Object { $_.thisBuild -and $_.ready }) }
function Wait-Ready([int]$Count = 1) {
  for ($i = 0; $i -lt 150 -and (Ready-Workers).Count -lt $Count; $i++) { Start-Sleep -Milliseconds 200 }
  Require ((Ready-Workers).Count -ge $Count) "warm: no $Count ready worker(s) after 30 s: $((Status) | ConvertTo-Json -Compress -Depth 5)"
}
function Commands { ((Status).running | Where-Object thisBuild | Measure-Object commands -Sum).Sum }

try {
  # Start from no worker of this build (an earlier run may have left one, or the person runs one).
  Invoke-Oml @('warm', 'stop') $warm | Out-Null

  # Modules: a blank one and two built from the fixtures, plus the files the commands read.
  $blank = Join-Path $Tmp 'Blank.oml'; $mod = Join-Path $Tmp 'WarmApp.oml'; $logic = Join-Path $Tmp 'WarmLogic.oml'
  Require ((Invoke-Oml @('new', $blank, '--name', 'WarmApp', '--kind', 'CrossDevice') $cold).Code -eq 0) 'warm: oml new failed'
  Require ((Invoke-Oml @('apply', $blank, (Join-Path $PSScriptRoot 'datamodel.json'), $mod) $cold).Code -eq 0) 'warm: could not build the data model module'
  Require ((Invoke-Oml @('apply', $blank, (Join-Path $PSScriptRoot 'logic.json'), $logic) $cold).Code -eq 0) 'warm: could not build the logic module'
  $steps = Join-Path $Tmp 'steps.json'
  @{ steps = @(@('inspect', $mod, '--own'), @('describe', $logic, 'CreateOrderFor'), @('validate', $logic)) } | ConvertTo-Json -Depth 5 | Set-Content $steps -Encoding utf8
  $spec = Join-Path $Tmp 'spec.json'
  Set-Content $spec '{ "entities": [ { "name": "WarmThing", "attributes": [ { "name": "Label", "type": "Text" } ] } ] }' -Encoding utf8

  # 1. The first warm call starts a worker and runs cold itself; the pool then answers.
  $first = Invoke-Oml @('inspect', $mod, '--own') $warm
  Require ($first.Code -eq 0) "warm: the first call failed: $($first.Out) $($first.Err)"
  Wait-Ready
  Write-Host 'ok   warm: the first call runs cold and starts a worker'

  # 2. Same bytes, warm and cold: stdout, stderr and exit code, for reads, errors, --pretty, the human view and batch.
  $cases = @(
    @('inspect', $mod, '--own'), @('inspect', $mod, '--own', '--pretty'), @('inspect', $logic), @('stats', $logic), @('header', $mod),
    @('describe', $logic, 'CreateOrderFor'), @('describe', $mod, 'NoSuchThing'), @('describe', $logic, 'Customer', 'Order'),
    @('find', $logic, 'Order'), @('tree', $logic, '--depth', '2'), @('flow', $logic, 'CreateOrderFor'), @('export', $logic, 'CreateOrderFor'),
    @('validate', $logic, '--warnings'), @('check', $logic), @('review', $blank, $logic),
    @('query', $mod, 'Root { Entities @filter(condition: "System.IO.File.Exists(\"x\")") { Name } }'),
    @('spec', 'check', $spec, '--module', $mod), @('batch', $steps), @('inspect', (Join-Path $Tmp 'missing.oml')), @('describe', $mod)
  )
  $before = Commands
  $bad = @()
  foreach ($c in $cases) {
    $a = Invoke-Oml $c $cold
    $b = Invoke-Oml $c $warm
    if (-not (Same $a $b)) { $bad += "$($c[0]) $($c[1..($c.Count - 1)] -join ' ' | ForEach-Object { $_.Replace($Tmp, '<tmp>') }): exit $($a.Code)/$($b.Code), stdout $($a.Out.Length)/$($b.Out.Length), stderr $($a.Err.Length)/$($b.Err.Length)" }
  }
  # The human view (OML_OUTPUT=human) and plain JSON with --json.
  foreach ($c in @(@('check', $logic), @('validate', $logic, '--warnings'), @('inspect', $mod, '--own', '--json'))) {
    $a = Invoke-Oml $c ($cold + @{ OML_OUTPUT = 'human'; NO_COLOR = '1' })
    $b = Invoke-Oml $c ($warm + @{ OML_OUTPUT = 'human'; NO_COLOR = '1' })
    if (-not (Same $a $b)) { $bad += "human $($c[0]): exit $($a.Code)/$($b.Code), stdout $($a.Out.Length)/$($b.Out.Length)" }
  }
  Require ($bad.Count -eq 0) "warm: output differs from cold:`n  $($bad -join "`n  ")"
  $ran = (Commands) - $before
  # describe without a name and a missing module print their usage or error before any model loads; every case still went to a worker.
  Require ($ran -ge $cases.Count) "warm: only $ran of the $($cases.Count + 3) warm calls ran in a worker"
  Write-Host "ok   warm: $($cases.Count + 3) commands print the same stdout, stderr and exit code warm and cold ($ran ran in a worker)"

  # 3. Timing: a cold call against the second warm call of the same command.
  $coldMs = (Invoke-Oml @('inspect', $mod, '--own') $cold).Ms
  Invoke-Oml @('describe', $logic, 'CreateOrderFor') $warm | Out-Null
  $warmMs = (Invoke-Oml @('describe', $logic, 'CreateOrderFor') $warm).Ms
  $coldDescribe = (Invoke-Oml @('describe', $logic, 'CreateOrderFor') $cold).Ms
  $timed = (Invoke-Oml @('inspect', $mod, '--own', '--timings') $warm).Out | ConvertFrom-Json
  Require ($null -ne $timed.timings.startup -and $timed.timings.serviceStudioInit -eq 0) "warm: --timings has no startup or a serviceStudioInit other than 0: $($timed.timings | ConvertTo-Json -Compress)"
  Require ($warmMs * 3 -lt $coldDescribe) "warm: the second warm describe ($warmMs ms) is not far faster than a cold one ($coldDescribe ms)"
  Write-Host "ok   warm: cold inspect $coldMs ms, cold describe $coldDescribe ms, second warm describe $warmMs ms; --timings reports serviceStudioInit 0"

  # 4. A changed module file loads again: the worker keeps WarmApp.oml open from the calls above.
  $changing = Join-Path $Tmp 'Changing.oml'
  Copy-Item $mod $changing
  $old = Invoke-Oml @('inspect', $changing, '--own') $warm
  Copy-Item $logic $changing -Force
  $new = Invoke-Oml @('inspect', $changing, '--own') $warm
  $expected = Invoke-Oml @('inspect', $changing, '--own') $cold
  Require ($new.Out -ceq $expected.Out -and $new.Out -cne $old.Out) 'warm: a changed module file was not loaded again'
  Write-Host 'ok   warm: a changed module file loads again'

  # 5. apply through a worker: the same report (but the output path) and the same staged module as a cold apply.
  #    The worker opened WarmApp.oml for the reads above, so apply takes that open module (Model.Take).
  $coldOut = Join-Path $Tmp 'apply-cold.oml'; $warmOut = Join-Path $Tmp 'apply-warm.oml'
  $a = Invoke-Oml @('apply', $mod, $spec, $coldOut) $cold
  $b = Invoke-Oml @('apply', $mod, $spec, $warmOut) $warm
  Require ($a.Code -eq 0 -and $b.Code -eq 0) "warm: apply failed: $($a.Out) / $($b.Out)"
  Require ((Normal $a.Out.Replace('apply-cold.oml', 'apply-warm.oml')) -ceq (Normal $b.Out) -and $a.Err -ceq $b.Err) "warm: the apply report differs:`n$($a.Out)`n$($b.Out)"
  $sameBytes = (Get-FileHash $coldOut).Hash -eq (Get-FileHash $warmOut).Hash
  $sameModule = (Invoke-Oml @('inspect', $coldOut) $cold).Out.Replace('apply-cold', 'apply-warm') -ceq (Invoke-Oml @('inspect', $warmOut) $cold).Out.Replace('apply-cold', 'apply-warm')
  Require ($sameModule) 'warm: the staged module of a warm apply reads differently from a cold one'
  if (-not $sameBytes) {
    # A new element gets a new key in every apply, so two cold applies differ too: review must see the warm one as it
    # sees a second cold one.
    $coldOut2 = Join-Path $Tmp 'apply-warm-cold.oml'
    Require ((Invoke-Oml @('apply', $mod, $spec, $coldOut2) $cold).Code -eq 0) 'warm: the second cold apply failed'
    $versusWarm = Normal (Invoke-Oml @('review', $coldOut, $warmOut) $cold).Out
    $versusCold = Normal (Invoke-Oml @('review', $coldOut, $coldOut2) $cold).Out
    Require ($versusWarm -ceq $versusCold) "warm: review sees the warm staged module differently from a second cold one:`n$versusWarm`n$versusCold"
  }
  $again = Invoke-Oml @('inspect', $mod, '--own') $warm
  Require ($again.Out -ceq (Invoke-Oml @('inspect', $mod, '--own') $cold).Out) 'warm: a read after apply sees the change in the source'
  Write-Host "ok   warm: apply writes the same staged module as cold ($(if ($sameBytes) { 'same bytes' } else { 'review sees it as a second cold apply' })); the source stays as it was"

  # 6. Parallel callers beyond the pool size (2): every one succeeds with the same output; the extra ones ran cold.
  $project = Join-Path $Tmp 'project'
  New-Item -ItemType Directory -Force (Join-Path $project '.oml') | Out-Null
  Set-Content (Join-Path $project '.oml\oml-config.json') '{ "performance": { "warm": true, "warmWorkers": 2, "warmIdleMinutes": 5 } }' -Encoding utf8
  $expectedOut = (Invoke-Oml @('describe', $logic, 'CreateOrderFor') $cold).Out
  Invoke-Oml @('describe', $logic, 'CreateOrderFor') @{ OML_WARM = $null } $project | Out-Null
  # A long batch keeps the one worker busy, so the parallel callers find the pool busy: they run cold, and one of them
  # starts the second worker.
  $long = Join-Path $Tmp 'long.json'
  @{ steps = @(1..40 | ForEach-Object { , @('check', $logic) }) } | ConvertTo-Json -Depth 5 | Set-Content $long -Encoding utf8
  $longJob = Start-ThreadJob -ScriptBlock {
    param($oml, $long, $project)
    $psi = [Diagnostics.ProcessStartInfo]::new($oml); foreach ($a in 'batch', $long) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.WorkingDirectory = $project; [void]$psi.Environment.Remove('OML_WARM')
    $p = [Diagnostics.Process]::Start($psi); $out = $p.StandardOutput.ReadToEnd(); $p.WaitForExit()
    [pscustomobject]@{ Code = $p.ExitCode; Steps = @(($out | ConvertFrom-Json).results).Count }
  } -ArgumentList $Oml, $long, $project
  for ($i = 0; $i -lt 100 -and -not ((Status).running | Where-Object { $_.thisBuild -and $_.busy }); $i++) { Start-Sleep -Milliseconds 100 }
  $jobs = foreach ($i in 1..6) {
    Start-ThreadJob -ScriptBlock {
      param($oml, $logic, $project)
      $psi = [Diagnostics.ProcessStartInfo]::new($oml); foreach ($a in 'describe', $logic, 'CreateOrderFor') { $psi.ArgumentList.Add($a) }
      $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.WorkingDirectory = $project; [void]$psi.Environment.Remove('OML_WARM')
      $p = [Diagnostics.Process]::Start($psi); $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync(); $p.WaitForExit()
      [pscustomobject]@{ Code = $p.ExitCode; Out = $out.Result; Err = $err.Result }
    } -ArgumentList $Oml, $logic, $project
  }
  $results = @($jobs | Wait-Job | Receive-Job); $jobs | Remove-Job
  $longResult = $longJob | Wait-Job | Receive-Job; $longJob | Remove-Job
  Require ($results.Count -eq 6 -and @($results | Where-Object { $_.Code -ne 0 -or $_.Out -cne $expectedOut }).Count -eq 0) "warm: a parallel caller failed or printed something else: $(($results | ForEach-Object { "$($_.Code) $($_.Err)" }) -join '; ')"
  Require ($longResult.Code -eq 0 -and $longResult.Steps -eq 40) "warm: the long batch failed: $($longResult | ConvertTo-Json -Compress)"
  Wait-Ready 2
  Require ((Ready-Workers).Count -le 2) "warm: more workers than performance.warmWorkers: $((Ready-Workers).Count)"
  Write-Host "ok   warm: 6 parallel callers with 2 workers all succeed with the same output; the pool grew to $((Ready-Workers).Count)"

  # 7. performance.warm false in the project: the command runs cold (Service Studio starts in it).
  Set-Content (Join-Path $project '.oml\oml-config.json') '{ "performance": { "warm": false } }' -Encoding utf8
  $off = (Invoke-Oml @('inspect', $mod, '--own', '--timings') @{ OML_WARM = $null } $project).Out | ConvertFrom-Json
  Require ($off.timings.serviceStudioInit -gt 0) "warm: performance.warm false still ran in a worker: $($off.timings | ConvertTo-Json -Compress)"
  Set-Content (Join-Path $project '.oml\oml-config.json') '{ "performance": { "warm": true } }' -Encoding utf8
  $on = (Invoke-Oml @('inspect', $mod, '--own', '--timings') @{ OML_WARM = $null } $project).Out | ConvertFrom-Json
  Require ($on.timings.serviceStudioInit -eq 0) "warm: performance.warm true did not use a worker: $($on.timings | ConvertTo-Json -Compress)"
  $badSetting = (Invoke-Oml @('config', 'set', 'performance.warmWorkers', '99', '--dir', $project) $cold)
  Require ($badSetting.Code -eq 2 -and $badSetting.Out -match 'whole number from 1 to 8') "warm: performance.warmWorkers 99 was not refused: $($badSetting.Out)"
  Write-Host 'ok   warm: performance.warm false runs cold, true uses a worker; warmWorkers takes 1 to 8'

  # 8. A killed worker: the next command runs cold (and starts a new worker); nothing fails.
  foreach ($w in @((Status).running | Where-Object thisBuild)) { Stop-Process -Id $w.pid -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Milliseconds 300
  $afterKill = Invoke-Oml @('inspect', $mod, '--own') $warm
  Require ($afterKill.Code -eq 0 -and $afterKill.Out -ceq (Invoke-Oml @('inspect', $mod, '--own') $cold).Out) 'warm: a command after a killed worker failed or differs'
  Wait-Ready
  Write-Host 'ok   warm: a killed worker falls back to cold, and a new one starts'

  # 8b. oml mcp (and so oml code) sends a call it would run itself to a worker: no Service Studio start per program.
  Wait-Ready 1
  $before = Commands
  $requests = @(
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"warm-check","version":"1"}}}',
    '{"jsonrpc":"2.0","method":"notifications/initialized"}',
    ('{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"read","arguments":{"args":["inspect",' + (ConvertTo-Json $mod) + ',"--own"]}}}')
  ) -join "`n"
  $psi = [Diagnostics.ProcessStartInfo]::new($Oml); $psi.ArgumentList.Add('mcp')
  $psi.UseShellExecute = $false; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.WorkingDirectory = $Tmp
  $psi.Environment['OML_WARM'] = '1'
  $p = [Diagnostics.Process]::Start($psi)
  $p.StandardInput.WriteLine($requests); $p.StandardInput.Close()
  $mcpOut = $p.StandardOutput.ReadToEnd(); [void]$p.StandardError.ReadToEnd(); $p.WaitForExit()
  Require ($mcpOut -match '"id":2' -and $mcpOut -notmatch '"isError":true') "warm: oml mcp call failed: $mcpOut"
  Require ((Commands) -gt $before) "warm: oml mcp ran the call itself, not in a worker ($before before, $(Commands) after)"
  Write-Host 'ok   warm: oml mcp (oml code) sends its calls to a worker'

  # 9. oml warm stop: no worker of this build runs after it, and the next command runs cold.
  $stop = (Invoke-Oml @('warm', 'stop') $warm).Out | ConvertFrom-Json
  Require ($stop.stopped -ge 1 -and (Ready-Workers).Count -eq 0 -and @((Status).running | Where-Object thisBuild).Count -eq 0) "warm: oml warm stop left a worker: $((Status) | ConvertTo-Json -Compress -Depth 5)"
  $afterStop = (Invoke-Oml @('inspect', $mod, '--own', '--timings') $warm).Out | ConvertFrom-Json
  Require ($afterStop.timings.serviceStudioInit -gt 0) 'warm: a command right after oml warm stop did not run cold'
  Write-Host "ok   warm: oml warm stop stopped $($stop.stopped) worker(s); the next command runs cold"
}
finally {
  # Never leave a worker behind (the last command above started one). A worker's last argument is the folder of the
  # oml it serves: whatever of this build is still there after oml warm stop is ended.
  Invoke-Oml @('warm', 'stop') $warm | Out-Null
  $bin = Split-Path ([IO.Path]::GetFullPath($Oml)) -Parent
  for ($i = 0; $i -lt 10; $i++) {
    $left = @(Get-CimInstance Win32_Process -Filter "Name = 'oml.exe'" | Where-Object { $_.CommandLine -match ' warm serve ' -and $_.CommandLine.IndexOf($bin, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
    if ($left.Count -eq 0) { break }
    Invoke-Oml @('warm', 'stop') $warm | Out-Null
    if ($i -ge 5) { $left | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } }
    Start-Sleep -Milliseconds 500
  }
  Remove-TestWorkFolder $Tmp
}
