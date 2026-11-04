# Old command names (census, meta, expr, api, widgets, setup, run, evolve, skills, library, warn, lt, dif, test scaffold) still
# work as hidden aliases of the new names (stats, header, expression, model-types, widget-types, explorer-menu, batch, learn,
# team-rules, outsystems-docs, explain-warning, lifetime, show-change, test bdd): the same output and exit code, also as a step
# of `oml batch`; no old name in `oml --help`, `oml help --all` or the search entries; and the usage log records the new name
# when the old one was typed (older log lines are read under the new names). Needs no network. Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'aliases'
try {
function Require($condition, $message) { if (-not $condition) { throw "aliases: $message" } }
$dir = Join-Path $Tmp ('aliases-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $dir | Out-Null
$aliases = [ordered]@{
  census = 'stats'; meta = 'header'; expr = 'expression'; api = 'model-types'; widgets = 'widget-types'; setup = 'explorer-menu'
  run = 'batch'; evolve = 'learn'; skills = 'team-rules'; library = 'outsystems-docs'; warn = 'explain-warning'; lt = 'lifetime'; dif = 'show-change'
}
$saved = @{}; foreach ($name in 'OML_LOG', 'OML_SKILLS', 'OML_LIBRARY', 'OML_LIFETIME_HOST', 'OML_LIFETIME_TOKEN') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
function Lines($file) { if (Test-Path $file) { @(Get-Content $file | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }) } else { @() } }
function Call([string[]]$a) {
  $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try { $out = & $Oml @a 2>&1 } finally { $ErrorActionPreference = $prev }
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = (($out | ForEach-Object { "$_" }) -join "`n") }
}
# The old name and the new one give the same exit code and the same text.
function Same([string[]]$old, [string[]]$new, [string]$what) {
  $o = Call $old; $n = Call $new
  Require ($o.Text -and $o.Text -ceq $n.Text -and $o.Exit -eq $n.Exit) "$what differs: old exit $($o.Exit), new exit $($n.Exit)`nold: $($o.Text)`nnew: $($n.Text)"
}
try {
  $env:OML_LOG = '0'
  $env:OML_SKILLS = Join-Path $dir 'skills'
  $env:OML_LIBRARY = Join-Path $dir 'library'
  $env:OML_LIFETIME_HOST = ''; $env:OML_LIFETIME_TOKEN = ''
  $mod = Join-Path $dir 'Alias.oml'
  & $Oml new $mod --name Alias --kind CrossDevice | Out-Null
  Require ($LASTEXITCODE -eq 0) 'could not build the test module'

  # 1. Every old name gives the same output and exit code as the new name.
  Same @('expr', '1+1') @('expression', '1+1') 'expr'
  Same @('expr', '1 +') @('expression', '1 +') 'expr (invalid)'
  Same @('warn', 'NoErrorHandlingInFlow') @('explain-warning', 'NoErrorHandlingInFlow') 'warn'
  Same @('lt') @('lifetime') 'lt (usage)'
  Same @('lt', '--help') @('lifetime', '--help') 'lt --help'
  Same @('skills') @('team-rules') 'skills'
  Same @('library', 'status') @('outsystems-docs', 'status') 'library status'
  Same @('evolve', '--help') @('learn', '--help') 'evolve --help'
  Same @('dif', '--help') @('show-change', '--help') 'dif --help'
  Same @('setup', '--help') @('explorer-menu', '--help') 'setup --help'
  Same @('meta', $mod) @('header', $mod) 'meta'
  Same @('census', $mod) @('stats', $mod) 'census'
  Same @('api') @('model-types') 'api'
  Same @('widgets') @('widget-types') 'widgets'
  Same @('test', 'scaffold') @('test', 'bdd') 'test scaffold (usage)'
  Same @('help', 'expr') @('help', 'expression') 'help expr'
  Write-Host 'ok   aliases: every old name gives the same output and exit code as the new name'

  # 2. An old name in a batch step, and `oml run` itself.
  $steps = Join-Path $dir 'steps.json'
  Set-Content $steps (@{ steps = @(@('census', $mod), @('warn', 'NoErrorHandlingInFlow')) } | ConvertTo-Json -Depth 5 -Compress) -Encoding utf8
  $old = (Call @('run', $steps)); $new = (Call @('batch', $steps))
  Require ($old.Exit -eq 0 -and $new.Exit -eq 0) "batch with old step names: exit $($old.Exit) / $($new.Exit): $($old.Text)"
  $o = $old.Text | ConvertFrom-Json; $n = $new.Text | ConvertFrom-Json
  Require ($o.results.Count -eq 2 -and $n.results.Count -eq 2) 'batch with old step names: two results expected'
  for ($i = 0; $i -lt 2; $i++) {
    Require ($o.results[$i].exitCode -eq 0 -and (($o.results[$i].output | ConvertTo-Json -Compress -Depth 20) -ceq ($n.results[$i].output | ConvertTo-Json -Compress -Depth 20))) "batch step $($i + 1) with an old name differs from the new one"
  }
  $nested = Join-Path $dir 'nested.json'
  Set-Content $nested (@{ steps = @(@('run', 'x.json'), @('lt', 'environments')) } | ConvertTo-Json -Depth 5 -Compress) -Encoding utf8
  $r = (Call @('batch', $nested, '--continue')).Text | ConvertFrom-Json
  Require (($r.results[0].output.error -match 'batch') -and ($r.results[0].output.error -notmatch "'run'") -and ($r.results[1].output.error -match 'lifetime')) "refused old-name steps name the new commands: $($r.results.output.error -join ' | ')"
  Write-Host 'ok   aliases: old names work as batch steps and are refused there under the new names'

  # 3. No old name in the help, the full help or the search entries.
  $overview = (Call @('--help')).Text
  $full = (Call @('help', '--all')).Text
  $found = @()
  foreach ($line in ($overview -split "`n")) {
    if ($line -match '^[A-Za-z ]+: ([a-z, -]+?)( \(|$)') { foreach ($w in ($Matches[1] -split ',')) { $found += $w.Trim() } }
  }
  Require ($found.Count -ge 40) "the overview lists $($found.Count) command words, expected at least 40"
  foreach ($old in $aliases.Keys) { Require ($found -notcontains $old) "oml --help lists the old name '$old'" }
  Require ($found -notcontains 'test scaffold') "oml --help lists 'test scaffold'"
  foreach ($new in $aliases.Values) { Require ($found -contains $new) "oml --help does not list '$new'" }
  Require ($found -contains 'test bdd') "oml --help does not list 'test bdd'"
  $words = @($full -split "`n" | ForEach-Object { if ($_ -match '^oml (\S+)') { $Matches[1] } } | Select-Object -Unique)
  foreach ($old in $aliases.Keys) { Require ($words -notcontains $old) "oml help --all has a line for the old name '$old'" }
  Require ($full -notmatch '(?m)^oml test scaffold') "oml help --all has a line for 'test scaffold'"
  foreach ($new in $aliases.Values) { Require ($words -contains $new) "oml help --all has no line for '$new'" }
  foreach ($text in $overview, $full) {
    foreach ($old in $aliases.Keys) { Require ($text -notmatch "\boml $old\b(?![-\w])") "help text mentions 'oml $old'" }
    Require ($text -notmatch 'oml test scaffold') "help text mentions 'oml test scaffold'"
  }
  $ids = @((Call @('doc', '--search-list')).Text -split "`n" | Where-Object { $_ -like 'command:*' } | ForEach-Object { ($_ -split "`t")[0] -replace '~\d+$', '' })
  Require ($ids.Count -ge 30) "the search lists $($ids.Count) command entries"
  foreach ($old in $aliases.Keys) { Require ($ids -notcontains "command:$old") "the search has an entry for the old name '$old'" }
  foreach ($new in 'stats', 'header', 'expression', 'lifetime', 'outsystems-docs', 'team-rules') { Require ($ids -contains "command:$new") "the search has no entry for '$new'" }
  Write-Host 'ok   aliases: no old name in oml --help, oml help --all or the search entries'

  # 4. The usage log records the new name when the old one is typed.
  $log = Join-Path $dir 'usage.jsonl'
  $env:OML_LOG = $log
  & $Oml expr '1+1' | Out-Null
  & $Oml census $mod | Out-Null
  & $Oml lt status 2>$null | Out-Null
  & $Oml skills | Out-Null
  & $Oml warn NoErrorHandlingInFlow | Out-Null
  & $Oml test scaffold 2>$null | Out-Null
  & $Oml run $steps | Out-Null
  $env:OML_LOG = '0'
  $cmds = (Lines $log).cmd -join ','
  Require ($cmds -ceq 'expression,stats,lifetime status,team-rules,explain-warning,test bdd,stats,explain-warning,batch') "the log holds: $cmds"
  Write-Host 'ok   aliases: the usage log records the new command name'

  # 5. Older log lines (written under the old names) are read under the new names.
  $old = Join-Path $dir 'old-usage.jsonl'
  $stamp = '2026-01-01T10:00:00.000Z'
  Set-Content $old @(
    ('{"ts":"' + $stamp + '","cmd":"lt status","exit":2,"ms":5,"bytes":10}'),
    ('{"ts":"' + $stamp + '","cmd":"lifetime status","exit":0,"ms":7,"bytes":10}'),
    ('{"ts":"' + $stamp + '","cmd":"census","exit":0,"ms":9,"bytes":10}'),
    ('{"ts":"' + $stamp + '","cmd":"test scaffold","exit":0,"ms":9,"bytes":10}')) -Encoding utf8
  $summary = (Call @('log', 'summary', '--file', $old)).Text | ConvertFrom-Json
  $names = @($summary.commands.command)
  Require (($names -contains 'lifetime status') -and ($names -contains 'stats') -and ($names -contains 'test bdd') -and ($names -notcontains 'lt status') -and ($names -notcontains 'census')) "log summary names: $($names -join ', ')"
  Require ((@($summary.commands | Where-Object command -eq 'lifetime status')[0].count) -eq 2) 'old and new log lines of one command are grouped'
  Write-Host 'ok   aliases: older log lines are grouped under the new names'
} finally {
  foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}
} finally { Remove-TestWorkFolder $Tmp }
