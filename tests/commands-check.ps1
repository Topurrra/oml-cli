# Merged commands and the grouped help. Each merged command's new form (inspect --stats, inspect --header, doc library,
# doc warning, spec model-types, spec widget-types, spec expr, new --templates, learn usage, menu explorer, doctor --selftest
# --show, dashboard --change, map mock) gives the same output and exit code as its old name, also as a step of `oml batch`
# and through `oml mcp` (same tool); the old name keeps its own --help, the new form's --help prints its new lines; words
# that were already answers stay answers (`oml doc warning` alone is the warnings topic, `oml spec widgets` a spec key);
# `oml help` lists the 12 areas in order and `oml help --all` has them as headings, with no line for an old name; the search
# has an entry per new form. Needs no network and no Service Studio window; selftest itself is not run (minutes).
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Tmp = New-TestFolder $Tmp 'commands'
$saved = @{}; foreach ($name in 'OML_LOG', 'OML_LIBRARY', 'OML_SKILLS') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$p = $null
try {
function Require($condition, $message) { if (-not $condition) { throw "commands: $message" } }
function Call([string[]]$a) {
  $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try { $out = & $Oml @a 2>&1 } finally { $ErrorActionPreference = $prev }
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = (($out | ForEach-Object { "$_" }) -join "`n") }
}
function Same([string[]]$old, [string[]]$new, [string]$what, [scriptblock]$view = $null) {
  $o = Call $old; $n = Call $new
  if ($view) { $o.Text = & $view $o.Text; $n.Text = & $view $n.Text }
  Require ($o.Text -and $o.Text -ceq $n.Text -and $o.Exit -eq $n.Exit) "$what differs: old exit $($o.Exit), new exit $($n.Exit)`nold: $($o.Text)`nnew: $($n.Text)"
}
$env:OML_LOG = '0'
$env:OML_LIBRARY = Join-Path $Tmp 'library'
$env:OML_SKILLS = Join-Path $Tmp 'skills'
$mod = Join-Path $Tmp 'Merged.oml'
& $Oml new $mod --name Merged --kind CrossDevice | Out-Null
Require ($LASTEXITCODE -eq 0) 'could not build the test module'
$usage = Join-Path $Tmp 'usage.jsonl'
Set-Content $usage @(
  '{"ts":"2026-01-01T10:00:00.000Z","cmd":"inspect","exit":0,"ms":5,"bytes":10}',
  '{"ts":"2026-01-01T10:00:01.000Z","cmd":"describe","exit":1,"ms":7,"bytes":10,"error":"Element not found"}') -Encoding utf8
$missingMd = Join-Path $Tmp 'missing.md'

# 1. Each old name and its new form: same output, same exit code.
Same @('stats', $mod) @('inspect', $mod, '--stats') 'inspect <m> --stats'
Same @('stats', $mod) @('inspect', '--stats', $mod) 'inspect --stats <m>'
Same @('header', $mod) @('inspect', '--header', $mod) 'inspect --header'
Same @('header', '--diff', $mod, $mod) @('inspect', '--header', '--diff', $mod, $mod) 'inspect --header --diff'
Same @('expression', '1+1') @('spec', 'expr', '1+1') 'spec expr'
Same @('expression', '1 +') @('spec', 'expr', '1 +') 'spec expr (invalid, exit 1)'
Same @('explain-warning', 'NoErrorHandlingInFlow') @('doc', 'warning', 'NoErrorHandlingInFlow') 'doc warning <id>'
Same @('explain-warning', 'security', 'warning') @('doc', 'warning', 'security', 'warning') 'doc warning <title words>'
Same @('explain-warning', '--catalog', 'Entity_') @('doc', 'warning', '--catalog', 'Entity_') 'doc warning --catalog'
Same @('explain-warning', 'NoSuchWarningAnywhere') @('doc', 'warning', 'NoSuchWarningAnywhere') 'doc warning (unknown, exit 2)'
Same @('outsystems-docs') @('doc', 'library') 'doc library'
Same @('outsystems-docs', 'status') @('doc', 'library', 'status') 'doc library status'
Same @('outsystems-docs', 'update', '--bogus') @('doc', 'library', 'update', '--bogus') 'doc library update (bad option)'
Same @('model-types') @('spec', 'model-types') 'spec model-types'
Same @('model-types', 'IEntity') @('spec', 'model-types', 'IEntity') 'spec model-types <T>'
Same @('widget-types') @('spec', 'widget-types') 'spec widget-types'
# The template list's references come in no fixed order from run to run: compare the names, kinds and availability.
Same @('templates') @('new', '--templates') 'new --templates' { param($x) ($x | ConvertFrom-Json | ForEach-Object { "$($_.name) $($_.kind) $($_.available)" }) -join ';' }
Same @('log', 'summary', '--file', $usage) @('learn', 'usage', '--file', $usage) 'learn usage'
Same @('log', 'summary', '--file', (Join-Path $Tmp 'none.jsonl')) @('learn', 'usage', '--file', (Join-Path $Tmp 'none.jsonl')) 'learn usage (no log, exit 2)'
if ($IsWindows -or $env:OS -eq 'Windows_NT') { Same @('explorer-menu', '--status') @('menu', 'explorer', '--status') 'menu explorer --status' }
Same @('show-change', $missingMd) @('dashboard', '--change', $missingMd) 'dashboard --change (missing file)'
Same @('show-change', '--no-open', $missingMd) @('dashboard', '--no-open', '--change', $missingMd) 'dashboard --change (option first)'
Same @('show-change') @('dashboard', '--change') 'dashboard --change (no file)'
Same @('mock', '--all', '--project', 'no-such-map-commands-check') @('map', 'mock', '--all', '--project', 'no-such-map-commands-check') 'map mock'
# selftest runs for minutes: its new form is checked by its help, and `doctor` without --selftest --show is unchanged.
$r = Call @('doctor', '--show')
Require ($r.Exit -eq 2 -and $r.Text -match 'Unknown option --show') "doctor --show alone is not the new form: exit $($r.Exit), $($r.Text)"
Write-Host 'ok   commands: every merged command gives the same output and exit code under its old name and its new form'

# 2. Words that were already answers stay answers.
Same @('doc', 'warnings') @('doc', 'warning') 'doc warning alone (the warnings topic)'
$r = Call @('spec', 'widgets')
Require ($r.Exit -eq 0 -and $r.Text -match 'Reactive UI') "oml spec widgets is no longer the spec key: exit $($r.Exit)"
$r = Call @('spec', 'types')
Require ($r.Exit -eq 0 -and $r.Text -match 'Data model') "oml spec types is no longer the spec key: exit $($r.Exit)"
Write-Host 'ok   commands: oml doc warning alone, oml spec widgets and oml spec types answer as before'

# 3. Help: the old name keeps its lines; the new form prints its new lines.
$helps = [ordered]@{
  'stats' = @('inspect', '--stats'); 'header' = @('inspect', '--header'); 'expression' = @('spec', 'expr'); 'explain-warning' = @('doc', 'warning')
  'outsystems-docs' = @('doc', 'library'); 'model-types' = @('spec', 'model-types'); 'widget-types' = @('spec', 'widget-types'); 'templates' = @('new', '--templates')
  'log' = @('learn', 'usage'); 'explorer-menu' = @('menu', 'explorer'); 'selftest' = @('doctor', '--selftest', '--show'); 'show-change' = @('dashboard', '--change'); 'mock' = @('map', 'mock')
}
foreach ($old in $helps.Keys) {
  $o = Call @($old, '--help')
  # The old name's own help: its usage lines, which name the old command or, in usage texts, the new form.
  $form = $helps[$old]
  Require ($o.Exit -eq 0 -and ($o.Text -match "oml $([regex]::Escape($old))( |$)" -or $o.Text -match "oml $([regex]::Escape($form -join ' '))( |$)")) "oml $old --help: exit $($o.Exit), $($o.Text)"
  $n = Call ($form + '--help')
  $lines = @($n.Text -split "`n" | Where-Object { $_ })
  Require ($n.Exit -eq 0 -and $lines.Count -ge 1) "oml $($form -join ' ') --help: exit $($n.Exit), $($n.Text)"
  foreach ($line in $lines) {
    $words = @(($line -split ' \(')[0] -split ' +')
    Require ($words[0] -eq 'oml' -and $words[1] -eq $form[0] -and @($form | Where-Object { $words -notcontains $_ }).Count -eq 0) "oml $($form -join ' ') --help prints a line of another form: $line"
  }
}
Write-Host 'ok   commands: oml <old> --help keeps the old lines; oml <new form> --help prints the new lines'

# 4. The overview: the 12 areas in order, every command word once; oml help --all: the same areas as headings.
$areas = 'Project', 'Read a module', 'The whole system', 'Change a module', 'Quality', 'Service Studio', 'Server and LifeTime', 'Test', 'Agents', 'Look things up', 'Other files', 'For people'
$overview = (Call @('help')).Text
Require ($overview -ceq (Call @('--help')).Text) 'oml help and oml --help differ'
$named = @(); $listed = @()
foreach ($line in ($overview -split "`n")) {
  if ($line -match '^([A-Z][A-Za-z ]*): ([a-z][a-z, -]*?)( \(|$)') { $named += $Matches[1]; $listed += @($Matches[2] -split ', ') }
}
Require (($named -join '|') -ceq ($areas -join '|')) "oml help areas: $($named -join ', ')"
Require ($listed.Count -eq @($listed | Select-Object -Unique).Count) "oml help names a command twice: $($listed -join ', ')"
$full = (Call @('help', '--all')).Text
$headings = @($full -split "`n" | Where-Object { $_ -match '^([A-Z][A-Za-z ]*):$' } | ForEach-Object { $Matches[1] })
Require (($headings -join '|') -ceq ($areas -join '|')) "oml help --all headings: $($headings -join ', ')"
$words = @($full -split "`n" | ForEach-Object { if ($_ -match '^oml (\S+)') { $Matches[1] } } | Select-Object -Unique)
foreach ($old in $helps.Keys) {
  Require ($words -notcontains $old) "oml help --all has a line for the old name '$old'"
  Require ($listed -notcontains $old) "oml help lists the old name '$old'"
}
Require (@($words | Where-Object { $listed -notcontains $_ }).Count -eq 0) "oml help does not list: $(@($words | Where-Object { $listed -notcontains $_ }) -join ', ')"
foreach ($form in $helps.Values) { Require ($full -match "(?m)^oml $([regex]::Escape($form[0])) .*$([regex]::Escape($form[-1]))") "oml help --all has no line for '$($form -join ' ')'" }
$c = Call @('commands', '--check')
Require ($c.Exit -eq 0) "oml commands --check: $($c.Text)"
Write-Host "ok   commands: oml help lists the $($areas.Count) areas in order, oml help --all has them as headings, no old name"

# 5. The search: one entry per new form, none for the old names.
$ids = @((Call @('doc', '--search-list')).Text -split "`n" | Where-Object { $_ -like 'command:*' } | ForEach-Object { ($_ -split "`t")[0] -replace '~\d+$', '' })
foreach ($form in $helps.Values) { Require ($ids -contains "command:$($form -join ' ')") "the search has no entry for '$($form -join ' ')'" }
foreach ($old in $helps.Keys) { Require ($ids -notcontains "command:$old") "the search has an entry for the old name '$old'" }
Write-Host 'ok   commands: the search has an entry per new form and none for the old names'

# 6. A batch step in the new form gives the old step's output (batch runs stats and explain-warning, not header or expression).
$oldSteps = Join-Path $Tmp 'old.json'; $newSteps = Join-Path $Tmp 'new.json'
Set-Content $oldSteps (@{ steps = @(@('stats', $mod), @('explain-warning', 'NoErrorHandlingInFlow')) } | ConvertTo-Json -Depth 5 -Compress) -Encoding utf8
Set-Content $newSteps (@{ steps = @(@('inspect', $mod, '--stats'), @('doc', 'warning', 'NoErrorHandlingInFlow')) } | ConvertTo-Json -Depth 5 -Compress) -Encoding utf8
$o = Call @('batch', $oldSteps); $n = Call @('batch', $newSteps)
Require ($o.Exit -eq 0 -and $n.Exit -eq 0) "batch: exit $($o.Exit) / $($n.Exit): $($n.Text)"
$oj = $o.Text | ConvertFrom-Json; $nj = $n.Text | ConvertFrom-Json
Require ($nj.results.Count -eq 2) 'batch with new forms: two results expected'
for ($i = 0; $i -lt 2; $i++) {
  Require ($nj.results[$i].exitCode -eq 0 -and (($oj.results[$i].output | ConvertTo-Json -Compress -Depth 20) -ceq ($nj.results[$i].output | ConvertTo-Json -Compress -Depth 20))) "batch step $($i + 1) in the new form differs from the old one"
}
Write-Host 'ok   commands: new forms as batch steps give the old steps'' output'

# 7. oml mcp: a new form runs through the same tool as its old name (read), and is refused where the old name is.
$psi = [Diagnostics.ProcessStartInfo]::new($Oml)
$psi.ArgumentList.Add('mcp')
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.StandardInputEncoding = [Text.UTF8Encoding]::new($false); $psi.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$psi.WorkingDirectory = $Tmp
$p = [Diagnostics.Process]::Start($psi)
$stderr = $p.StandardError.ReadToEndAsync()
$script:id = 0
function Ask($tool, [string[]]$a) {
  $script:id++
  $p.StandardInput.WriteLine((@{ jsonrpc = '2.0'; id = $script:id; method = 'tools/call'; params = @{ name = $tool; arguments = @{ args = $a } } } | ConvertTo-Json -Depth 10 -Compress)); $p.StandardInput.Flush()
  $task = $p.StandardOutput.ReadLineAsync()
  if (-not $task.Wait(180000)) { throw 'commands: no answer from oml mcp in 180 s' }
  $m = $task.Result | ConvertFrom-Json
  Require ($m.id -eq $script:id) "mcp answered $($m.id), expected $($script:id)"
  $m
}
$p.StandardInput.WriteLine('{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"commands-check","version":"1"}}}'); $p.StandardInput.Flush()
$null = $p.StandardOutput.ReadLine()
foreach ($pair in @(@(@('stats', 'Merged.oml'), @('inspect', 'Merged.oml', '--stats')), @(@('expression', '1+1'), @('spec', 'expr', '1+1')), @(@('explain-warning', 'NoErrorHandlingInFlow'), @('doc', 'warning', 'NoErrorHandlingInFlow')), @(@('header', 'Merged.oml'), @('inspect', '--header', 'Merged.oml')))) {
  $a = Ask 'read' $pair[0]; $b = Ask 'read' $pair[1]
  Require ($a.result.isError -ne $true -and $b.result.isError -ne $true -and $a.result.content[0].text -ceq $b.result.content[0].text) "mcp read: $($pair[1] -join ' ') differs from $($pair[0] -join ' '): $($b.result.content[0].text)"
}
foreach ($pair in @(@(@('mock', '--all'), @('map', 'mock', '--all')), @(@('log', 'summary'), @('learn', 'usage')), @(@('templates'), @('new', '--templates')))) {
  $a = Ask 'read' $pair[0]; $b = Ask 'read' $pair[1]
  Require ($a.result.isError -eq $true -and $b.result.isError -eq $true) "mcp read: $($pair[1] -join ' ') must be refused like $($pair[0] -join ' ') (no tool runs it)"
}
$h = Ask 'read' @('doc', 'library', '--help')
Require ($h.result.isError -ne $true -and $h.result.content[0].text -match 'oml doc library fetch') "mcp read: doc library --help: $($h.result.content[0].text)"
$p.StandardInput.Close()
if (-not $p.WaitForExit(30000)) { $p.Kill() }
Write-Host 'ok   commands: oml mcp runs each new form with the tool of its old name'
} finally {
  if ($p -and -not $p.HasExited) { $p.Kill() }
  foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
  Remove-TestWorkFolder $Tmp
}
