# Checks `describe --history`, `inspect --history` and `inspect --changed-since`.
# A module built by oml has no change history (a headless save sets none, and a spec may not write these fields),
# so the checks for a built module cover what must NOT appear. The values and dates are read from the tutorial
# module that ships with Service Studio (saved by Service Studio), when it is installed.
# Setting, saving and reopening history values is in `oml selftest` (SelfTest.History.cs).
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($condition, $message) { if (-not $condition) { throw $message } }
function Run([string[]]$cliArgs) {
    $text = & $Oml @cliArgs 2>&1
    [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($text -join "`n") }
}
function Read-Oml([string[]]$cliArgs) {
    $r = Run $cliArgs
    Require ($r.Code -eq 0) "Command failed ($($r.Code)): $cliArgs $($r.Text)"
    return ($r.Text | ConvertFrom-Json)
}
# ConvertFrom-Json turns an ISO date string into a [datetime]; this gives the text back.
function Iso($v) { if ($v -is [datetime]) { $v.ToString("yyyy-MM-dd'T'HH:mm:ss", [cultureinfo]::InvariantCulture) } else { $v } }
function Day($v) { (Iso $v).Substring(0, 10) }
$dir = Join-Path $Tmp 'history'
New-Item -ItemType Directory -Force $dir | Out-Null
$blank = Join-Path $dir 'blank.oml'; $built = Join-Path $dir 'built.oml'
Read-Oml @('new', $blank, '--kind', 'CrossDevice', '--name', 'HistoryTest') | Out-Null
$r = Read-Oml @('apply', $blank, (Join-Path $PSScriptRoot 'logic.json'), $built)
Require $r.saved 'history fixture did not save'

# A spec cannot write history: authorship is Service Studio's.
$spec = Join-Path $dir 'forge.json'
foreach ($field in @('createdBy', 'lastModifiedBy', 'lastModifiedDate', 'lastMergedBy', 'lastMergedDate')) {
    $value = if ($field -like '*Date') { '2024-05-06T07:08:09' } else { 'Mallory' }
    Set-Content $spec ('{"edits":[{"op":"set","element":"Order","properties":{"' + $field + '":"' + $value + '"}}]}') -Encoding utf8
    $r = Run @('apply', $built, $spec, (Join-Path $dir "forged-$field.oml"))
    Require ($r.Code -ne 0 -and $r.Text -match 'history fields are written by Service Studio') "a spec could set $field : $($r.Text)"
    Require (!(Test-Path (Join-Path $dir "forged-$field.oml"))) "a forged $field was saved"
}

# Built module: no history anywhere, with or without the flags, compact and --pretty.
foreach ($pretty in @(@(), @('--pretty'))) {
    $label = if ($pretty) { '--pretty' } else { 'compact' }
    $plain = Run (@($pretty) + @('describe', $built, 'Order', 'SumCustomerOrders'))
    Require ($plain.Code -eq 0 -and $plain.Text -cnotmatch '"history"|Changed by|created by') "describe without --history changed ($label)"
    $off = Run (@($pretty) + @('inspect', $built))
    Require ($off.Code -eq 0 -and $off.Text -cnotmatch '"lastModified|"changedSince') "inspect without flags changed ($label)"
    # No history stored: the flag adds nothing to describe (no object, no line); inspect rows have no change fields with values.
    $asked = Run (@($pretty) + @('describe', $built, 'Order', 'SumCustomerOrders', '--history'))
    Require ($asked.Code -eq 0 -and $asked.Text -notmatch 'Changed by|created by' -and $asked.Text -notmatch '"history": ?\{') "describe --history invented history ($label)"
}
$forHistory = Run @('inspect', $built, '--history')
Require ($forHistory.Code -eq 0 -and $forHistory.Text -cnotmatch '"lastModified') 'inspect --history invented a change date'
$since = Read-Oml @('inspect', $built, '--changed-since', '2000-01-01')
Require ($since.name -eq 'HistoryTest' -and $since.changedSince -eq '2000-01-01' -and !$since.entities -and !$since.serverActions -and !$since.screens) '--changed-since kept elements without a change date'
foreach ($gone in @('references', 'module', 'content', 'defaultScreen')) {
    Require ($since.PSObject.Properties.Name -notcontains $gone) "--changed-since kept the $gone section"
}
# Arguments.
foreach ($badDate in @('2024-13-01', '06/05/2024', '2024-5-6', 'yesterday')) {
    $r = Run @('inspect', $built, '--changed-since', $badDate)
    Require ($r.Code -eq 2 -and $r.Text -match 'yyyy-mm-dd') "--changed-since $badDate was accepted: $($r.Text)"
}
$r = Run @('inspect', $built, '--changed-since')
Require ($r.Code -eq 2 -and $r.Text -match 'changed-since') 'inspect --changed-since without a value'
$r = Run @('inspect', $built, '--changed-since', '2024-01-01', '--only', 'references')
Require ($r.Code -eq 2 -and $r.Text -match 'references' -and $r.Text -match 'no change date') "--only references with --changed-since: $($r.Text)"
$r = Run @('describe', $built, 'Order', '--histry')
Require ($r.Code -eq 2 -and $r.Text -match '--history') "describe unknown option: $($r.Text)"

# Real values: the tutorial module that ships with Service Studio was saved by Service Studio.
$studio = if ($env:OML_SERVICE_STUDIO) { $env:OML_SERVICE_STUDIO } else { 'C:\Program Files\OutSystems\Service Studio 11\Service Studio' }
$sample = Join-Path $studio 'TutorialResources\NoLockIn.oml'
if (-not (Test-Path $sample)) {
    Write-Host 'ok   history: nothing appears without the flags, specs cannot write history, arguments (Service Studio tutorial sample not found: value and date checks skipped)'
    return
}
$item = Read-Oml @('describe', $sample, 'MenuItem', '--history')
Require ($item.history.createdBy -and $item.history.lastModifiedBy -and (Iso $item.history.lastModifiedDate) -match '^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$') "sample history: $($item.history | ConvertTo-Json -Compress)"
$line = "Changed by $($item.history.lastModifiedBy) on $(Iso $item.history.lastModifiedDate); created by $($item.history.createdBy)"
Require ($item.text.TrimEnd().EndsWith($line)) "sample last line: $($item.text)"
Require ($item.history.PSObject.Properties.Name -notcontains 'lastMergedBy') 'an empty merge value was printed'
$base = Read-Oml @('describe', $sample, 'MenuItem')
Require ($base.PSObject.Properties.Name -notcontains 'history' -and $item.text.StartsWith($base.text.TrimEnd())) 'describe --history changed more than the last line'
# Several names; --history anywhere in the arguments; --pretty keeps the history out when it is not asked for.
$many = Read-Oml @('describe', $sample, '--history', 'MenuItem', 'MessageType')
Require ($many.Count -eq 2 -and $many[0].history -and $many[1].history) 'describe --history with several names'
Require ((Run @('--pretty', 'describe', $sample, 'MenuItem')).Text -cnotmatch '"history"') 'describe --pretty without --history printed history'
Require ((Run @('--pretty', 'inspect', $sample, '--own')).Text -cnotmatch '"lastModified') 'inspect --pretty without --history printed change fields'
# With --depth the line follows the element's own text, before the called bodies.
$deep = Read-Oml @('describe', $sample, 'Contact_List', '--history', '--depth', '1')
$parts = $deep.text -split "\r?\n\r?\n"
Require ($parts.Count -ge 3 -and $parts[1].StartsWith('Changed by ')) "history line is not right after the element's text: $($parts[1])"

# --changed-since agrees with the dates --history prints, for every section that has them, around the edges.
$rows = Read-Oml @('inspect', $sample, '--own', '--only', 'entities,actions,screens,blocks', '--history')
$sections = 'entities', 'serverActions', 'clientActions', 'screens', 'blocks'
$all = foreach ($s in $sections) { foreach ($e in @($rows.$s)) { if ($e) { [pscustomobject]@{ Section = $s; Name = $e.name; Day = (Day $e.lastModifiedDate) } } } }
Require (@($all | Where-Object { !$_.Day }).Count -eq 0 -and $all.Count -gt 5) 'the sample has elements without a change date'
$days = $all.Day | Sort-Object -Unique
Require ($days.Count -ge 2) 'the sample has only one change day, so the filter cannot be checked'
$dayAfterLast = [datetime]::ParseExact($days[-1], 'yyyy-MM-dd', $null).AddDays(1).ToString('yyyy-MM-dd')
$dayBeforeFirst = [datetime]::ParseExact($days[0], 'yyyy-MM-dd', $null).AddDays(-1).ToString('yyyy-MM-dd')
foreach ($cut in @($dayBeforeFirst) + @($days) + @($dayAfterLast)) {
    $got = Read-Oml @('inspect', $sample, '--own', '--only', 'entities,actions,screens,blocks', '--changed-since', $cut, '--history')
    Require ($got.changedSince -eq $cut) "changedSince missing for $cut"
    $expected = @($all | Where-Object { $_.Day -ge $cut } | ForEach-Object { "$($_.Section)/$($_.Name)" } | Sort-Object)
    $names = foreach ($s in $sections) { foreach ($e in @($got.$s)) { if ($e) { "$s/$($e.name)" } } }
    $actual = @($names | Sort-Object)
    Require (($expected -join ',') -ceq ($actual -join ',')) "--changed-since $cut kept $($actual.Count) elements, expected $($expected.Count)"
}
$none = Read-Oml @('inspect', $sample, '--own', '--only', 'entities,actions,screens,blocks', '--changed-since', $dayAfterLast)
Require (($none.PSObject.Properties.Name -join ',') -eq 'changedSince') "a cut after the last change should keep nothing: $($none | ConvertTo-Json -Compress)"
# Without --history the kept rows carry no change fields.
$bare = Run @('inspect', $sample, '--own', '--only', 'entities', '--changed-since', $days[0])
Require ($bare.Code -eq 0 -and $bare.Text -cnotmatch '"lastModified') '--changed-since alone printed change fields'

# oml run passes the flags on.
$steps = Join-Path $dir 'steps.json'
@{ steps = @(@('describe', $sample, 'MenuItem', '--history'), @('inspect', $sample, '--own', '--history', '--only', 'entities', '--changed-since', $days[0])) } |
    ConvertTo-Json -Depth 5 | Set-Content $steps -Encoding utf8
$run = Read-Oml @('run', $steps)
Require ($run.results.Count -eq 2 -and $run.results[0].output.text.Contains($line) -and $run.results[1].output.changedSince -eq $days[0] -and ($run.results[1].output.entities | Where-Object name -eq 'MenuItem').lastModifiedBy) 'oml run did not pass the flags on'
Write-Host 'ok   history: values and dates on the Service Studio sample, --changed-since at every edge, nothing without the flags (also --pretty), specs cannot write history'
