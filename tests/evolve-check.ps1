# Checks the learning loop: the new usage-log fields, memory recall with usefulness, newest-wins and decay, trap
# suggestions in memory doctor, and oml evolve report / propose / show / accept / reject / undo / history, with the
# safety rules (no secret and no injected text in a proposal, rules.md never patched). The report and the proposals on
# tests/evolve-sample.jsonl are compared with tests/evolve-golden.json (OML_TEST_NOW fixes the clock).
# -Update rewrites the golden file after a reviewed change.
param([string]$Oml, [string]$Tmp, [switch]$Update)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'evolve'
try {
function Require($condition, $message) { if (-not $condition) { throw $message } }
function Json { param([Parameter(ValueFromRemainingArguments)]$a) $out = & $Oml @a; ($out -join "`n") | ConvertFrom-Json }
$sample = Join-Path $PSScriptRoot 'evolve-sample.jsonl'
$golden = Join-Path $PSScriptRoot 'evolve-golden.json'
$root = Join-Path $Tmp ('evolve-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $root | Out-Null
$saved = @{}; foreach ($n in 'OML_LOG', 'OML_SESSION', 'OML_SKILLS', 'OML_TEST_NOW', 'CLAUDE_SESSION_ID', 'CLAUDE_CODE_SESSION_ID') { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
$secretText = 'ghp_1234567890abcdefghijklmnopqrstuvwxyzAB'
function Project($name) {
  $d = Join-Path $root $name; New-Item -ItemType Directory -Force $d | Out-Null
  Set-Content (Join-Path $d 'Orders.oml') 'x'
  & $Oml memory init --dir $d | Out-Null; Require ($LASTEXITCODE -eq 0) "memory init in $name"
  $d
}
try {
  foreach ($n in 'CLAUDE_SESSION_ID', 'CLAUDE_CODE_SESSION_ID', 'OML_TEST_NOW') { [Environment]::SetEnvironmentVariable($n, $null) }
  $env:OML_SKILLS = Join-Path $root 'skills'

  # 1. Usage log: session (hashed), option names, recalled ids, unfound searches, warn ids, spec keys.
  $p = Project 'log'
  $log = Join-Path $root 'usage.jsonl'
  $env:OML_LOG = $log; $env:OML_SESSION = 'session-one'
  & $Oml memory add "Orders total uses the Sum aggregate" --kind decision --module Orders --dir $p | Out-Null
  & $Oml memory recall "orders total" --module Orders --dir $p | Out-Null
  & $Oml doc --search "zzqxv qqvvk" | Out-Null
  & $Oml warn InvalidEntity_OnlyAuto | Out-Null
  $bad = Join-Path $root 'bad.json'; Set-Content $bad '{"bogusKey":1}'
  & $Oml spec check $bad | Out-Null
  $rows = @(Get-Content $log | ForEach-Object { $_ | ConvertFrom-Json })
  $recall = $rows | Where-Object cmd -eq 'memory recall'
  Require ($recall.recalled -contains 0 -and $recall.session -match '^[0-9a-f]{16}$' -and $recall.opts -contains '--module') "log: recall line $($recall | ConvertTo-Json -Compress)"
  $search = $rows | Where-Object cmd -eq 'doc'
  Require ($search.query -eq 'zzqxv qqvvk' -and $search.found -eq $false) "log: unfound search $($search | ConvertTo-Json -Compress)"
  Require (($rows | Where-Object cmd -eq 'warn').topic -eq 'InvalidEntity_OnlyAuto') 'log: warn id'
  Require (($rows | Where-Object cmd -eq 'spec check').specKey -eq 'bogusKey') 'log: spec key'
  $text = Get-Content $log -Raw
  Require (-not $text.Contains('session-one') -and -not $text.Contains($p) -and -not $text.Contains('bad.json')) 'log: raw session id, folder or argument in the file'
  Write-Host 'ok   log: hashed session, option names, recalled ids, unfound search, warn id, spec key'

  # 2. Recall ranks by usefulness. Two records equal for the question: without evidence the newer comes first; after the
  #    log shows that commands after recalling record 0 passed and after record 1 failed, record 0 comes first.
  $p = Project 'rank'
  & $Oml memory add "Orders total is computed with the Sum aggregate" --kind fact --module Orders --dir $p | Out-Null
  & $Oml memory add "Orders total is computed with a SQL query" --kind fact --module Orders --dir $p | Out-Null
  $env:OML_SESSION = $null
  $empty = Join-Path $root 'empty.jsonl'; Set-Content $empty ''
  $env:OML_LOG = $empty
  $r = Json memory recall "orders total computed" --module Orders --dir $p
  Require ($r.hits[0].id -eq 1) "recall: without evidence the newer record first: $($r.hits.id -join ',')"
  $useLog = Join-Path $root 'use.jsonl'
  $lines = foreach ($day in 1..3) {
    '{"ts":"2026-09-0' + $day + 'T09:00:00.000Z","cmd":"memory recall","exit":0,"ms":5,"bytes":5,"session":"aa' + $day + '","recalled":[0]}'
    '{"ts":"2026-09-0' + $day + 'T09:01:00.000Z","cmd":"apply","exit":0,"ms":5,"bytes":5,"session":"aa' + $day + '"}'
    '{"ts":"2026-09-0' + $day + 'T10:00:00.000Z","cmd":"memory recall","exit":0,"ms":5,"bytes":5,"session":"bb' + $day + '","recalled":[1]}'
    '{"ts":"2026-09-0' + $day + 'T10:01:00.000Z","cmd":"apply","exit":1,"ms":5,"bytes":5,"session":"bb' + $day + '","error":"x"}'
  }
  Set-Content $useLog $lines; $env:OML_LOG = $useLog
  $r = Json memory recall "orders total computed" --module Orders --dir $p
  Require ($r.hits[0].id -eq 0) "recall: the useful record first: $($r.hits.id -join ',')"
  $l = Json memory list --module Orders --dir $p
  $r0 = $l.records | Where-Object id -eq 0; $r1 = $l.records | Where-Object id -eq 1
  Require ($r0.use.helped -eq 3 -and $r1.use.failed -eq 3) "list: use counts $($l.records | ConvertTo-Json -Compress -Depth 4)"
  Write-Host 'ok   recall: usefulness changes the order; list shows recalled, helped, failed'

  # 3. Newest wins: a newer record that contradicts an older one is shown alone, with overrides.
  $p = Project 'newest'
  $env:OML_LOG = $empty
  & $Oml memory add "Orders_CS is public for Billing" --kind decision --module Orders --dir $p | Out-Null
  & $Oml memory add "Orders_CS is private for Billing" --kind decision --module Orders --dir $p | Out-Null
  $r = Json memory recall "Orders_CS Billing public private" --module Orders --dir $p
  Require ($r.hits.Count -eq 1 -and $r.hits[0].id -eq 1 -and $r.hits[0].overrides -contains 0 -and $r.overrides) "recall: newest wins $($r | ConvertTo-Json -Compress -Depth 5)"
  Require (((Json memory list --module Orders --dir $p).records).Count -eq 2) 'recall: the older record must stay stored'
  Write-Host 'ok   recall: the newer of two contradicting records wins; the older stays stored'

  # 4. Decay: a fact nobody recalled for 180 days drops out of recall (a decision does not); --include-stale shows it.
  $p = Project 'decay'
  & $Oml memory add "Orders export runs at night" --kind fact --module Orders --dir $p | Out-Null
  & $Oml memory add "Orders export stays in the Orders module" --kind decision --module Orders --dir $p | Out-Null
  $oldLog = Join-Path $root 'old.jsonl'; Set-Content $oldLog '{"ts":"2026-01-01T00:00:00.000Z","cmd":"version","exit":0,"ms":1,"bytes":1}'
  $env:OML_LOG = $oldLog; $env:OML_TEST_NOW = (Get-Date).ToUniversalTime().AddDays(200).ToString('yyyy-MM-ddTHH:mm:ssZ')
  $r = Json memory recall "orders export" --module Orders --dir $p
  Require ($r.stale -eq 1 -and @($r.hits).Count -eq 1 -and $r.hits[0].kind -eq 'decision') "decay: $($r | ConvertTo-Json -Compress -Depth 5)"
  $r = Json memory recall "orders export" --module Orders --dir $p --include-stale
  Require (@($r.hits).Count -eq 2) 'decay: --include-stale'
  Require (((Json memory list --module Orders --dir $p).records | Where-Object kind -eq 'fact').stale) 'decay: list marks it stale'
  $env:OML_TEST_NOW = $null
  Write-Host 'ok   recall: stale facts drop out, decisions stay, --include-stale and list show them'

  # 5. memory doctor suggests traps for failures that repeat, never the secret or injected ones, and never writes one.
  $p = Project 'doctor'
  $env:OML_LOG = $empty
  $before = (Get-Item (Join-Path $p '.oml\memory\memory.log')).Length
  $doc = Json memory doctor --dir $p --file $sample
  $cmds = @($doc.suggestedTraps | ForEach-Object command)
  Require ($cmds -contains 'oml preview' -and $cmds -contains 'oml apply' -and $cmds -notcontains 'oml lt deploy') "doctor: suggestions $($cmds -join ',')"
  Require (-not (($doc | ConvertTo-Json -Depth 6) -match 'ghp_1234|Ignore previous')) 'doctor: a secret or injected text in a suggestion'
  $preview = $doc.suggestedTraps | Where-Object command -eq 'oml preview'
  Require ($preview.add -like 'oml memory add "oml preview fails with: *--kind trap*' -and $preview.passedWith -like 'oml preview*--out*') "doctor: add command $($preview | ConvertTo-Json -Compress)"
  Require ((Get-Item (Join-Path $p '.oml\memory\memory.log')).Length -eq $before) 'doctor wrote a record'
  & $Oml memory add "oml preview fails with: Browser not found, install Edge" --kind trap --module Orders --dir $p | Out-Null
  $doc = Json memory doctor --dir $p --file $sample
  Require (@($doc.suggestedTraps | Where-Object command -eq 'oml preview').Count -eq 0) 'doctor: a trap record covers the failure now'
  Write-Host 'ok   memory doctor: suggests trap records for repeated failures, writes nothing'

  # 6. Golden: report and proposals on the sample log.
  $env:OML_TEST_NOW = '2026-10-08T00:00:00Z'; $env:OML_LOG = $null
  $g = Join-Path $root 'golden'; New-Item -ItemType Directory $g | Out-Null
  $report = Json evolve report --file $sample --dir $g
  $propose = Json evolve propose --file $sample --dir $g
  $actual = [ordered]@{
    report = $report | Select-Object -Property * -ExcludeProperty files
    proposals = @($propose.proposals | ForEach-Object { [ordered]@{ id = $_.id; kind = $_.kind; title = $_.title; lines = $_.lines } })
    flags = @($propose.flags | ForEach-Object reason)
  } | ConvertTo-Json -Depth 10
  if ($Update) { Set-Content $golden $actual -Encoding utf8; Write-Host 'ok   evolve: golden file written' }
  else {
    $expected = (Get-Content $golden -Raw).Trim()
    Require ($expected -eq $actual.Trim()) "evolve: report or proposals differ from tests/evolve-golden.json. Review, then run tests/evolve-check.ps1 -Update.`n$actual"
    Write-Host 'ok   evolve: report and proposals match the golden file'
  }

  # 7. Evidence citations, no secret and no injected text anywhere in what propose wrote.
  foreach ($p2 in $propose.proposals) {
    $full = Json evolve show $p2.id --dir $g
    foreach ($line in $full.lines) { Require (@($line.evidence).Count -ge 1 -and $line.text -match 'usage line|memory #|check reports|oml memory add') "propose: line without evidence: $($line.text)" }
  }
  $written = (Get-ChildItem -Recurse -File (Join-Path $g '.oml') | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
  Require (-not $written.Contains($secretText) -and -not $written.Contains('Ignore previous')) 'propose: a secret or the injected text reached a file'
  Require (-not (($propose | ConvertTo-Json -Depth 8) -match 'ghp_1234|Ignore previous|example.invalid')) 'propose: a secret or the injected text in the output'
  Require (@($propose.flags | Where-Object reason -like 'could weaken*').Count -ge 1) 'propose: --allow-existing-errors must be flagged, not proposed'
  Write-Host 'ok   evolve propose: every line cites evidence; secrets and injected text never reach a proposal'

  # 8. accept, reject, undo; a rejected proposal is not made again.
  $p = Project 'accept'
  $env:OML_TEST_NOW = '2026-10-05T12:00:00Z'
  $first = Json evolve propose --file $sample --dir $p
  $mem = ($first.proposals | Where-Object kind -eq 'memory' | Select-Object -First 1).id
  $skill = ($first.proposals | Where-Object title -like '*evolved-preview*').id
  $rej = ($first.proposals | Where-Object title -like '*evolved-apply*').id
  Require ($mem -and $skill -and $rej) "accept: proposals missing $($first.proposals | ConvertTo-Json -Compress -Depth 4)"
  $a = Json evolve accept $mem --dir $p; Require ($LASTEXITCODE -eq 0 -and @($a.records).Count -eq 1) "accept memory: $($a | ConvertTo-Json -Compress)"
  $trap = (Json memory list --kind trap --dir $p).records | Where-Object id -eq $a.records[0]
  Require ($trap.text -like 'oml *fails with:*') 'accept memory: the trap record'
  $a = Json evolve accept $skill --dir $p; Require ($LASTEXITCODE -eq 0 -and $a.skill -eq 'evolved-preview') "accept skill: $($a | ConvertTo-Json -Compress)"
  Require (((Json skills).skills | Where-Object name -eq 'evolved-preview')) 'accept skill: not installed'
  $again = Json evolve accept $skill --dir $p; Require ($LASTEXITCODE -eq 1) 'accept: twice'
  Json evolve reject $rej --why "known already" --dir $p | Out-Null
  $env:OML_TEST_NOW = '2026-10-08T00:00:00Z'
  $second = Json evolve propose --file $sample --dir $p
  Require (-not ($second.proposals.id -contains $rej) -and -not ($second.proposals.title -like '*evolved-apply*') -and -not ($second.proposals.title -like '*evolved-preview*')) "reject: proposed again $($second.proposals.title -join ' | ')"
  # Measuring: preview failed more after the skill was accepted (2026-10-05T12:00Z): "worse", with the undo command.
  $rep = Json evolve report --file $sample --dir $p
  $effect = $rep.accepted | Where-Object id -eq $skill
  Require ($effect.verdict -eq 'worse' -and $effect.hint -like "*oml evolve undo $skill*") "effect: $($effect | ConvertTo-Json -Compress -Depth 4)"
  $h = Json evolve history --file $sample --dir $p
  Require (@($h.accepted).Count -eq 2 -and $h.rejected -ge 1) 'history'
  Json evolve undo $skill --dir $p | Out-Null; Json evolve undo $mem --dir $p | Out-Null
  Require (-not ((Json skills).skills | Where-Object name -eq 'evolved-preview')) 'undo skill'
  Require (((Json memory list --all --dir $p).records | Where-Object id -eq $a.records).status -ne 'live') 'undo memory'
  Require ((Json evolve history --dir $p --file $sample).accepted.undoneAt.Count -eq 2) 'undo: history'
  Write-Host 'ok   evolve: accept installs and writes, reject is remembered, report marks a worse change, undo takes it back'

  # 9. --maintainer patches docs as diffs and never rules.md, even when the state file is edited to point at it.
  $repo = Join-Path $root 'repo'; New-Item -ItemType Directory -Force (Join-Path $repo 'skill\reference') | Out-Null
  $repoRoot = Split-Path $PSScriptRoot
  Copy-Item (Join-Path $repoRoot 'skill\rules.md') (Join-Path $repo 'skill')
  Copy-Item (Join-Path $repoRoot 'skill\reference\troubleshooting.md'), (Join-Path $repoRoot 'skill\reference\memory.md') (Join-Path $repo 'skill\reference')
  $rulesHash = (Get-FileHash (Join-Path $repo 'skill\rules.md')).Hash
  $m = Json evolve propose --maintainer --repo $repo --file $sample --dir $repo
  Require (@($m.proposals).Count -ge 1 -and -not ($m.proposals.target -match 'rules\.md')) 'maintainer: proposals'
  foreach ($x in $m.proposals) { Require ((Get-Content (Json evolve show $x.id --dir $repo).diffFile -Raw) -match '(?m)^\+\+\+ b/skill/reference/') 'maintainer: unified diff' }
  $id = $m.proposals[0].id
  $troubleHash = (Get-FileHash (Join-Path $repo 'skill\reference\troubleshooting.md')).Hash
  Json evolve accept $id --dir $repo | Out-Null; Require ((Get-FileHash (Join-Path $repo 'skill\reference\troubleshooting.md')).Hash -ne $troubleHash) 'maintainer: accept'
  Json evolve undo $id --dir $repo | Out-Null; Require ((Get-FileHash (Join-Path $repo 'skill\reference\troubleshooting.md')).Hash -eq $troubleHash) 'maintainer: undo restores the file'
  $statePath = Join-Path $repo '.oml\evolve\state.json'
  $state = Get-Content $statePath -Raw | ConvertFrom-Json
  $other = $m.proposals[1].id
  $state.proposals.$other.apply.file = (Join-Path $repo 'skill\rules.md')
  $state | ConvertTo-Json -Depth 20 | Set-Content $statePath
  $refused = Json evolve accept $other --dir $repo
  Require ($LASTEXITCODE -ne 0 -and $refused.error -match 'rules\.md') "maintainer: rules.md accepted: $($refused | ConvertTo-Json -Compress)"
  Require ((Get-FileHash (Join-Path $repo 'skill\rules.md')).Hash -eq $rulesHash) 'maintainer: rules.md changed'
  Write-Host 'ok   evolve --maintainer: unified diffs under skill/reference, accept and undo, rules.md never patched'
} finally {
  foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
}
} finally { Remove-TestWorkFolder $Tmp }
