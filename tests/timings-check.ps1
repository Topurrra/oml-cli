# Checks --timings (phases per command, output otherwise unchanged), the skipped scratch reload for a spec with only
# logic keys, install (hash check against the validated bytes, backup), and an `oml run` apply that takes the module
# a read step left open (same report as a separate apply; a later read of the source sees the file, not the change).
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($condition, $message) { if (-not $condition) { throw $message } }
function Same($a, $b) { ($a | ConvertTo-Json -Depth 50 -Compress) -ceq ($b | ConvertTo-Json -Depth 50 -Compress) }
function Without($o, $name) { $copy = $o | ConvertTo-Json -Depth 50 | ConvertFrom-Json; $copy.PSObject.Properties.Remove($name); $copy }
function Hash($path) { (Get-FileHash $path -Algorithm SHA256).Hash }
$dir = Join-Path $Tmp ('timings-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $dir | Out-Null

$blank = Join-Path $dir 'blank.oml'; $mod = Join-Path $dir 'TimeApp.oml'
& $Oml new $blank --name TimeApp --kind CrossDevice | Out-Null
$built = & $Oml apply $blank (Join-Path $PSScriptRoot 'datamodel.json') $mod --timings | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $built.saved) 'timings: could not build the test module'
# datamodel.json has logic and data keys: the data phase ran, so the scratch reload ran.
$t = $built.timings
foreach ($phase in 'startup', 'spec', 'serviceStudioInit', 'load', 'rules snapshot', 'build', 'save scratch', 'reload scratch', 'build logic', 'save', 'reopen', 'json', 'other', 'total') {
  Require ($null -ne $t.$phase -and $t.$phase -ge 0) "timings: apply has no '$phase' phase: $($t | ConvertTo-Json -Compress)"
}
$sum = ($t.PSObject.Properties | Where-Object Name -ne 'total' | Measure-Object Value -Sum).Sum
# Each phase is cut to whole milliseconds, so the sum may be short by up to 1 ms per phase.
Require ($sum -le $t.total -and $t.total - $sum -le @($t.PSObject.Properties).Count) "timings: phases ($sum ms) do not add up to total ($($t.total) ms)"
Write-Host "ok   timings: apply phases add up to the total ($($t.total) ms)"

# Without --timings the output has no timings and is otherwise the same.
$plain = & $Oml validate $mod --warnings | ConvertFrom-Json
$timed = & $Oml validate $mod --warnings --timings | ConvertFrom-Json
Require ($null -eq $plain.timings -and $null -ne $timed.timings.load -and (Same $plain (Without $timed 'timings'))) 'timings: validate output changed with --timings'
$plain = & $Oml inspect $mod --own --only actions | ConvertFrom-Json
$timed = & $Oml inspect $mod --own --only actions --timings | ConvertFrom-Json
Require ((Same $plain (Without $timed 'timings')) -and $null -ne $timed.timings.inspect) 'timings: inspect output changed with --timings'
# A list (describe with several names) keeps its shape; the timings go to stderr.
$err = Join-Path $dir 'stderr.txt'
$list = & $Oml describe $mod FetchOrder Customer --timings 2> $err | ConvertFrom-Json
$stderr = (Get-Content $err -Raw) | ConvertFrom-Json
Require ($list.Count -eq 2 -and $null -ne $stderr.timings.describe) 'timings: describe with several names'
Write-Host 'ok   timings: validate, inspect and describe output unchanged; a list gets its timings on stderr'

# A spec with only logic keys skips the scratch save and reload. The generated CRUD action of an entity that is
# already in the file still resolves, and the result is the same as with the reload (forced by an empty data list).
$logic = Join-Path $dir 'logic.json'
Set-Content $logic -Encoding utf8 '{ "serverActions": [ { "name": "AddCustomer", "inputs": [ { "name": "Name", "type": "Text" } ], "outputs": [ { "name": "Id", "type": "Customer Identifier" } ], "locals": [ { "name": "Rec", "type": "Customer" } ], "flow": [ { "kind": "assign", "name": "Prepare", "assign": { "Rec.Name": "Name" } }, { "kind": "execute", "action": "CreateCustomer", "args": { "Source": "Rec" } }, { "kind": "assign", "name": "SetId", "assign": { "Id": "CreateCustomer.Id" } } ] } ] }'
$withData = Join-Path $dir 'logic-and-data.json'
Set-Content $withData -Encoding utf8 ((Get-Content $logic -Raw).TrimEnd().TrimEnd('}') + ', "structures": [] }')
$fast = & $Oml apply $mod $logic (Join-Path $dir 'fast.oml') --timings | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $fast.saved -and $null -eq $fast.timings.'save scratch' -and $null -eq $fast.timings.'reload scratch') "timings: a logic-only spec still saved and reloaded: $($fast | ConvertTo-Json -Compress -Depth 5)"
$slow = & $Oml apply $mod $withData (Join-Path $dir 'slow.oml') --timings | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $slow.saved -and $null -ne $slow.timings.'reload scratch') 'timings: a spec with a data key did not reload'
foreach ($key in 'created', 'existing', 'warnings', 'newWarnings', 'reopenNewErrors') {
  Require (Same $fast.$key $slow.$key) "timings: '$key' differs between the skipped and the done reload"
}
$a = & $Oml describe (Join-Path $dir 'fast.oml') AddCustomer | ConvertFrom-Json
$b = & $Oml describe (Join-Path $dir 'slow.oml') AddCustomer | ConvertFrom-Json
Require ((Same $a $b) -and $a.text -match 'CreateCustomer') 'timings: the action built without the reload differs'
Write-Host 'ok   timings: a logic-only spec skips the scratch reload with the same result'

# Source scan: every LogicKeys entry is a Spec property, and the data phase (Apply's Execute up to the reload, and
# the methods it passes the whole spec to) never reads one. A new key read there must leave LogicKeys.
$src = Join-Path $PSScriptRoot '..\src\Core'
$applySrc = Get-Content (Join-Path $src 'Apply.cs') -Raw
$keys = [regex]::Matches([regex]::Match($applySrc, '(?s)LogicKeys = new\(StringComparer\.OrdinalIgnoreCase\)\s*\{(.*?)\};').Groups[1].Value, '"(\w+)"') | ForEach-Object { $_.Groups[1].Value }
$specProps = [regex]::Matches([regex]::Match($applySrc, '(?s)record Spec\((.*?)\);').Groups[1].Value, '\b([A-Z]\w*)\s*=') | ForEach-Object { $_.Groups[1].Value }
Require ($keys.Count -ge 20) "logic keys: could not read LogicKeys from Apply.cs ($($keys.Count))"
foreach ($k in $keys) { Require ($specProps -contains $k) "logic keys: '$k' is not a Spec property" }
$start = $applySrc.IndexOf('static (int code, object report) Execute(Spec spec'); $end = $applySrc.IndexOf('if (!LogicOnly(raw))')
Require ($start -ge 0 -and $end -gt $start) 'logic keys: the data phase of Apply.Execute was not found'
$region = [regex]::Replace($applySrc.Substring($start, $end - $start), '(?s)var logic = .*?;', '')
$bodies = @($region)
foreach ($call in [regex]::Matches($region, '\.(\w+)\(([^()]*\bspec\b[^()]*)\)') | Where-Object { $_.Groups[2].Value -match '(^|,\s*)spec(\s*,|$)' }) {
  $method = $call.Groups[1].Value
  $body = Get-ChildItem $src -Filter *.cs | ForEach-Object { $t = Get-Content $_.FullName -Raw; $m = [regex]::Match($t, "(?s)void $method\(IESpace m, Spec spec[^)]*\)\s*\{.*?\n    \}|void $method\(Spec spec\)\s*\{.*?\n    \}"); if ($m.Success) { $m.Value } }
  Require ($null -ne $body) "logic keys: the data phase passes the spec to $method, whose body the check cannot find"
  $bodies += $body
}
foreach ($text in $bodies) {
  $read = @([regex]::Matches($text, '\bspec\.(\w+)') | ForEach-Object { $_.Groups[1].Value }) + @([regex]::Matches($text, 'Item\((?:raw|Raw),\s*"(\w+)"') | ForEach-Object { $_.Groups[1].Value })
  foreach ($k in $read) { Require (-not ($keys -contains $k)) "logic keys: '$k' is read before the scratch reload, so it cannot be in LogicKeys" }
}
Write-Host "ok   logic keys: $($keys.Count) keys are Spec properties and the data phase reads none of them ($($bodies.Count - 1) methods scanned)"
# A missing spec file is a clear usage error, not a crash.
$noSpec = & $Oml apply $mod (Join-Path $dir 'no-such-spec.json') (Join-Path $dir 'never.oml') 2>$null | ConvertFrom-Json
Require ($LASTEXITCODE -eq 2 -and $noSpec.error -match 'Cannot read the spec') "apply: a missing spec file was not a clear error: $($noSpec | ConvertTo-Json -Compress)"

# install: the installed file equals the validated staged bytes, the backup equals the old target.
$target = Join-Path $dir 'Target.oml'; Copy-Item $mod $target
$oldHash = Hash $target
$installed = & $Oml install (Join-Path $dir 'fast.oml') $target --timings | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $installed.installed -eq $target -and $installed.sha256 -eq (Hash $target) -and (Hash $target) -eq (Hash (Join-Path $dir 'fast.oml'))) "install: target is not the staged file: $($installed | ConvertTo-Json -Compress)"
Require ((Test-Path $installed.backup) -and (Hash $installed.backup) -eq $oldHash) 'install: backup missing or not the old target'
Require ($null -ne $installed.timings.'load staged' -and $null -eq $installed.timings.reopen) 'install: phases (load staged once, no second load)'
$again = & $Oml describe $target AddCustomer | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $again.text -match 'CreateCustomer') 'install: the installed module does not load'
$missing = & $Oml install (Join-Path $dir 'none.oml') $target | ConvertFrom-Json
Require ($LASTEXITCODE -eq 2 -and $missing.error -match 'must exist' -and (Hash $target) -eq $installed.sha256) 'install: a missing staged file was not refused'
$locked = Join-Path $dir 'Locked.oml'; Copy-Item $mod $locked; (Get-Item $locked).IsReadOnly = $true
$lockedHash = Hash $locked
$ro = & $Oml install (Join-Path $dir 'fast.oml') $locked 2>$null | ConvertFrom-Json
Require ($LASTEXITCODE -eq 2 -and $ro.error -match 'read-only' -and (Hash $locked) -eq $lockedHash -and @(Get-ChildItem $dir -Filter 'Locked.oml.*.bak').Count -eq 0) "install: a read-only target was not refused cleanly: $($ro | ConvertTo-Json -Compress)"
(Get-Item $locked).IsReadOnly = $false
# A target another process holds open: the write fails before it changes anything, so nothing is installed.
$held = [IO.File]::Open($locked, 'Open', 'Read', 'Read')
try { $busy = & $Oml install (Join-Path $dir 'fast.oml') $locked 2>$null | ConvertFrom-Json; $busyCode = $LASTEXITCODE }
finally { $held.Dispose() }
Require ($busyCode -eq 2 -and $busy.error -match '^Nothing installed: .* The target is unchanged; backup kept at .*\.bak\.$' -and (Hash $locked) -eq $lockedHash) "install: a target held open was not reported as unchanged: $($busy | ConvertTo-Json -Compress)"
Write-Host 'ok   install: target equals the validated staged bytes, backup equals the old target; read-only and locked targets refused'

# oml run: apply takes the module a read step left open. Its report equals a separate apply, and a later read of
# the source loads the file again, so it does not see the change.
$steps = Join-Path $dir 'steps.json'
$runOut = Join-Path $dir 'run.oml'
@{ steps = @(@('inspect', $mod, '--own'), @('apply', $mod, $logic, $runOut, '--timings'), @('describe', $mod, 'AddCustomer'), @('describe', $runOut, 'AddCustomer')) } |
  ConvertTo-Json -Depth 5 | Set-Content $steps -Encoding utf8
$run = (& $Oml run $steps --continue) | ConvertFrom-Json
$r = $run.results
Require ($r[1].exitCode -eq 0 -and $r[1].output.saved) 'run: apply step failed'
Require ($null -ne $r[1].output.timings -and $null -eq $r[1].output.timings.load -and $null -ne $r[1].output.timings.reopen) "run: the apply step loaded its source again instead of taking the open module: $($r[1].output.timings | ConvertTo-Json -Compress)"
$alone = & $Oml apply $mod $logic (Join-Path $dir 'alone.oml') | ConvertFrom-Json
foreach ($key in 'created', 'existing', 'warnings', 'newWarnings', 'reopenNewErrors', 'baselineErrors') {
  Require (Same $r[1].output.$key $alone.$key) "run: '$key' of the apply step differs from a separate apply"
}
Require ($r[2].exitCode -ne 0 -and $r[3].exitCode -eq 0 -and $r[3].output.text -match 'CreateCustomer') 'run: a read of the source after apply saw the change, or the output did not load'
# oml run --timings: every step but spec reports its phases; the args stay as written.
$timedRun = (& $Oml run $steps --continue --timings) | ConvertFrom-Json
Require ($null -ne $timedRun.results[0].output.timings.load -and $null -ne $timedRun.results[3].output.timings -and $timedRun.results[0].args.Count -eq 3) 'run --timings: steps have no timings, or their args changed'
foreach ($c in @(@('spec', 'check', $logic), @('doc'), @('doctor'), @('warn', 'x'), @('lt', 'environments'))) {
  $refusal = & $Oml @c --timings 2>$null | ConvertFrom-Json
  Require ($LASTEXITCODE -eq 2 -and $refusal.error -eq "--timings is not available for $($c[0]).") "timings: '$($c[0]) --timings' was not refused: $($refusal | ConvertTo-Json -Compress)"
}
$checked = & $Oml check $mod --timings | ConvertFrom-Json
Require ($null -ne $checked.timings.load) 'timings: check has no timings'
Write-Host 'ok   run: apply takes the open module (same report), a later read loads the file again; run --timings; unsupported commands refuse --timings, check has them'
