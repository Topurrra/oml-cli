# oml doc and oml warn: topic list, one topic, one section, unknown names, warning lookups, size budgets of the
# knowledge files and of the default skill load, links in skill/, the warnings.md table shape, and the `hint`
# that apply adds to a known new warning. Throws on the first failure.
param([string]$Oml, [string]$Tmp, [string]$Module)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$ref = Join-Path $repo 'skill/reference'
$work = Join-Path $Tmp 'doc-check'; New-Item -ItemType Directory -Force $work | Out-Null

function Fail($m) { throw "doc: $m" }
function Run([string[]]$a) {
  # A deliberate failure (unknown topic) writes to stderr. On Windows PowerShell 5.1 a native stderr line is an error
  # record, so under Stop the script would abort; lower the preference to Continue for the call, merge stderr into
  # Text (as before), then restore.
  $prev = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try { $out = & $Oml @a 2>&1 } finally { $ErrorActionPreference = $prev }
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = ($out | ForEach-Object { "$_" }) -join "`n" }
}
function Tokens($path) { [math]::Ceiling((Get-Content -Raw $path).Replace("`r`n", "`n").Length / 4) } # same count on LF and CRLF checkouts

# Size budget per topic in tokens (characters / 4). A file may be at most 15% over its budget.
$budget = [ordered]@{
  'ui-reactive' = 1500; 'osui-patterns' = 2000; 'data-rules' = 1200; 'logic-rules' = 1200; 'security' = 800
  'rest' = 900; 'processes-timers-email' = 1200; 'architecture-rules' = 800; 'warnings' = 2000
  'traditional-to-reactive' = 900; 'troubleshooting' = 2000; 'ui-checklist' = 700; 'testing' = 2000
  'best-practices' = 1300; 'rules' = 900; 'memory' = 1100
}
# Where a topic's file is: skill/reference/<topic>.md, except rules (skill/rules.md, which SKILL.md loads first).
function TopicFile($t) { if ($t -eq 'rules') { Join-Path $repo 'skill/rules.md' } else { Join-Path $ref "$t.md" } }

# 1. the topic list names every topic, one line each
$r = Run 'doc'
if ($r.Exit -ne 0) { Fail "oml doc exit $($r.Exit)" }
$listed = @($r.Text -split "`n" | Where-Object { $_ -match '^(\S+)  -  \S' } | ForEach-Object { $Matches[1] })
if ((($listed | Sort-Object) -join ',') -ne ((@($budget.Keys) | Sort-Object) -join ',')) { Fail "topics listed: $($listed -join ', ')" }
# SKILL.md's "Topics:" line names every topic, so agents know each one exists
$skillTopics = [regex]::Match((Get-Content -Raw (Join-Path $repo 'skill/SKILL.md')), '(?m)^Topics: (.*)$').Groups[1].Value
# rules is not on that line: SKILL.md tells agents to read rules.md first. memory is not either: the `write` field of a memory recall points to it, which saves tokens in the default load.
foreach ($t in $budget.Keys) { if ($t -notin 'rules', 'memory' -and -not $skillTopics.Contains("``$t``")) { Fail "SKILL.md Topics line does not name $t" } }

# 2. one topic prints the whole file; topic names are case-insensitive and may end in .md.
#    Topics are plain ASCII, so every console code page shows them unchanged.
foreach ($t in $budget.Keys) {
  if ((Get-Content -Raw (TopicFile $t)) -match '[^\x00-\x7F]') { Fail "$t.md has a non-ASCII character" }
  $r = Run 'doc', $t
  $file = (Get-Content -Raw (TopicFile $t)).Replace("`r`n", "`n").TrimEnd()
  if ($r.Exit -ne 0 -or $r.Text -ne $file) { Fail "oml doc $t does not print the file (exit $($r.Exit))" }
}
$r = Run 'doc', 'UI-Reactive.md'
if ($r.Exit -ne 0 -or $r.Text -notmatch '^# Reactive UI rules') { Fail "case-insensitive topic: exit $($r.Exit)" }

# 3. one section: from its heading to the next heading of the same level
$r = Run 'doc', 'ui-reactive#forms'
if ($r.Exit -ne 0 -or $r.Text -notmatch '^## Forms and validation' -or $r.Text -match '## Tables') { Fail "section forms: exit $($r.Exit): $($r.Text)" }
$r = Run 'doc', 'data-rules#transactions'
if ($r.Exit -ne 0 -or $r.Text -notmatch '^## Transactions' -or $r.Text -notmatch 'CommitTransaction') { Fail "section transactions: $($r.Text)" }

# 4. unknown topic and unknown section: exit 2 with the closest names
$r = Run 'doc', 'ui-reactve'
if ($r.Exit -ne 2 -or $r.Text -notmatch 'Closest: ui-reactive') { Fail "unknown topic: exit $($r.Exit), $($r.Text)" }
$r = Run 'doc', 'ui-reactive#and'
if ($r.Exit -ne 2 -or $r.Text -notmatch 'several sections') { Fail "ambiguous section: exit $($r.Exit), $($r.Text)" }
$r = Run 'doc', 'ui-reactive#nosuchsection'
if ($r.Exit -ne 2 -or $r.Text -notmatch 'forms-and-validation') { Fail "unknown section: exit $($r.Exit), $($r.Text)" }

# 5. oml warn: by id (any case), by title words, unknown
$r = Run 'warn', 'NoErrorHandlingInFlow'
if ($r.Exit -ne 0 -or $r.Text -notmatch '\| `NoErrorHandlingInFlow` \| No Exception Handling \|' -or $r.Text -notmatch '^## Warnings') { Fail "warn by id: $($r.Text)" }
$r = Run 'warn', 'noerrorhandlinginflow'
if ($r.Exit -ne 0 -or $r.Text -notmatch 'NoErrorHandlingInFlow') { Fail "warn by id, lower case: $($r.Text)" }
$r = Run 'warn', 'security', 'warning'
if ($r.Exit -ne 0 -or $r.Text -notmatch 'EntityActionClientSide' -or $r.Text -notmatch 'ServerActionInAnonymousScreen') { Fail "warn by title: $($r.Text)" }
$r = Run 'warn', 'locked module'
if ($r.Exit -ne 0 -or $r.Text -notmatch 'Stop:') { Fail "warn environment row: $($r.Text)" }
# oml's own warning ids (apply raises them in newWarnings) have rows too.
foreach ($id in 'PublicServerAction', 'UniqueIndexOnExistingEntity') { $r = Run 'warn', $id; if ($r.Exit -ne 0 -or $r.Text -notmatch "\| ``$id`` \| \(oml\)") { Fail "warn $id`: $($r.Text)" } }
$r = Run 'warn', 'NoSuchMessageId'
if ($r.Exit -ne 2 -or $r.Text -notmatch 'none is a close match' -or $r.Text -match 'Closest:') { Fail "warn unknown: exit $($r.Exit), $($r.Text)" }
$r = Run 'warn', 'NoErrorHandlingInFlw'
if ($r.Exit -ne 2 -or $r.Text -notmatch 'Closest: NoErrorHandlingInFlow') { Fail "warn closest by id: $($r.Text)" }
$r = Run 'warn', 'unused zzz'
if ($r.Exit -ne 2 -or $r.Text -notmatch 'Closest: Unused') { Fail "warn closest by words: $($r.Text)" }

# 6. warnings.md: every table row has 4 cells, and an id is in one row only (a hint must not be ambiguous)
$ids = @{}
foreach ($line in (Get-Content (Join-Path $ref 'warnings.md'))) {
  if (-not $line.StartsWith('| ') -or $line.StartsWith('| Id |')) { continue }
  $cells = @(($line.Trim().Trim('|') -split ' \| ') | ForEach-Object { $_.Trim() })
  if ($cells.Count -ne 4) { Fail "warnings.md row has $($cells.Count) cells: $line" }
  foreach ($m in [regex]::Matches($cells[0], '`([A-Za-z0-9_]+)`')) {
    $id = $m.Groups[1].Value
    if ($ids.ContainsKey($id)) { Fail "warnings.md lists $id twice" }
    $ids[$id] = $cells[3]
  }
}
if ($ids.Count -lt 40) { Fail "warnings.md has only $($ids.Count) ids" }

# 7. size budgets, and the default skill load (SKILL.md + rules.md): budget 2.7k tokens (+15%), about 2.8k today
foreach ($t in $budget.Keys) {
  $n = Tokens (TopicFile $t)
  if ($n -gt $budget[$t] * 1.15) { Fail "$t.md is $n tokens; budget $($budget[$t]) (+15%)" }
}
$load = (Tokens (Join-Path $repo 'skill/SKILL.md')) + (Tokens (Join-Path $repo 'skill/rules.md'))
if ($load -gt 2700 * 1.15) { Fail "SKILL.md + rules.md is $load tokens; budget 2700 (+15%)" }

# 7b. rules 4 and 7 of the Copilot file, and every rule on the website, read as in rules.md
$ruleLines = @{}
foreach ($m in [regex]::Matches((Get-Content -Raw (Join-Path $repo 'skill/rules.md')).Replace("`r`n", "`n"), '(?m)^(\d+)\. (.*)$')) { $ruleLines[[int]$m.Groups[1].Value] = $m.Value }
$copilot = (Get-Content -Raw (Join-Path $repo 'skill/copilot-instructions.md')).Replace("`r`n", "`n")
foreach ($n in 4, 7) { if (-not $copilot.Contains($ruleLines[$n] + "`n")) { Fail "copilot-instructions.md rule $n is not word for word rules.md" } }
# 7c. the "Writing to the user" block (style of every text an agent writes to the user) is the same in SKILL.md and the Copilot file
$skill = (Get-Content -Raw (Join-Path $repo 'skill/SKILL.md')).Replace("`r`n", "`n")
$writing = foreach ($text in $skill, $copilot) { [regex]::Match($text, '(?ms)^## Writing to the user' + "`n" + '.*?(?=^## )').Value }
if (-not $writing[0] -or $writing[0] -cne $writing[1]) { Fail 'the "## Writing to the user" block differs between SKILL.md and copilot-instructions.md, or one lacks it' }
function Plain([string]$t) { ([System.Net.WebUtility]::HtmlDecode(($t -replace '<[^>]+>', '')) -replace '\*\*|`', '' -replace ' ?\((enforced|enforced by apply)\)', '').Trim() }
# The site names the newer user-visible options.
$siteText = Get-Content -Raw (Join-Path $repo 'docs/index.html')
foreach ($o in '--timings', '--serve', '--click', '--type', '--wait') { if (-not $siteText.Contains($o)) { Fail "docs/index.html does not mention $o" } }
$site = [regex]::Matches((Get-Content -Raw (Join-Path $repo 'docs/index.html')), '<li><span class="badge[^"]*">[^<]*</span><div>(.*?)</div></li>')
if ($site.Count -ne $ruleLines.Count) { Fail "docs/index.html lists $($site.Count) rules; rules.md has $($ruleLines.Count)" }
for ($i = 1; $i -le $site.Count; $i++) {
  if ((Plain $site[$i - 1].Groups[1].Value) -ne (Plain ($ruleLines[$i] -replace '^\d+\. ', ''))) { Fail "docs/index.html rule $i differs from rules.md" }
}

# 7d. the architect skill files (loaded only by an architect, never by default): ASCII, size, and SKILL.md points to them
foreach ($x in @(@('skill/architect.md', 2500), @('skill/reference/worker-brief.md', 700))) {
  $p = Join-Path $repo $x[0]
  if (-not (Test-Path $p)) { Fail "$($x[0]) is missing" }
  if ((Get-Content -Raw $p) -match '[^\x00-\x7F]') { Fail "$($x[0]) has a non-ASCII character" }
  $n = Tokens $p
  if ($n -gt $x[1] * 1.15) { Fail "$($x[0]) is $n tokens; budget $($x[1]) (+15%)" }
}
if (-not $skill.Contains('(architect.md)')) { Fail 'SKILL.md does not point to architect.md' }

# 8. every relative link in skill/ resolves
foreach ($f in Get-ChildItem (Join-Path $repo 'skill') -Recurse -Filter *.md) {
  foreach ($m in [regex]::Matches((Get-Content -Raw $f.FullName), '\]\(([^)\s#]+)(#[^)\s]*)?\)')) {
    $target = $m.Groups[1].Value
    if ($target -match '^[a-z]+:') { continue }
    if (-not (Test-Path (Join-Path $f.DirectoryName $target))) { Fail "$($f.Name): broken link $target" }
  }
}
Write-Host "ok   doc: $($budget.Count) topics, sections, unknown names, warn lookups, $($ids.Count) ids, budgets (default load $load tokens), links"

# 9. apply adds the warnings.md fix as `hint` to a new warning it covers, and no hint to other ids
$spec = Join-Path $work 'hint.json'
Set-Content $spec -Encoding utf8 @'
{
  "serverActions": [ { "name": "StepOne", "flow": [] }, { "name": "StepTwo", "flow": [] } ],
  "clientActions": [ { "name": "RunBoth", "flow": [
    { "kind": "execute", "action": "StepOne" }, { "kind": "execute", "action": "StepTwo" } ] } ]
}
'@
$report = & $Oml apply $Module $spec (Join-Path $work 'hint.oml') | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $report.saved) { Fail "hint apply failed: $($report | ConvertTo-Json -Depth 6 -Compress)" }
$w = @($report.newWarnings | Where-Object id -eq 'ConsecutiveServerInteractions')
if ($w.Count -ne 1) { Fail "no ConsecutiveServerInteractions warning: $($report.newWarnings | ConvertTo-Json -Depth 6 -Compress)" }
if ($w[0].hint -ne $ids['ConsecutiveServerInteractions']) { Fail "hint '$($w[0].hint)' is not the warnings.md fix '$($ids['ConsecutiveServerInteractions'])'" }
foreach ($x in @($report.newWarnings)) {
  if ($ids.ContainsKey($x.id) -and $x.hint -ne $ids[$x.id]) { Fail "hint of $($x.id) is '$($x.hint)'" }
  if (-not $ids.ContainsKey($x.id) -and $null -ne $x.hint) { Fail "$($x.id) has a hint but no warnings.md row" }
}
$pretty = (& $Oml apply $Module $spec (Join-Path $work 'hint-pretty.oml') --pretty) -join "`n"
if ($pretty -notmatch '"hint": "Do the work' -or $pretty -match '"hint": null') { Fail "--pretty report: hint missing or null" }
Write-Host "ok   apply hint: ConsecutiveServerInteractions -> '$($w[0].hint)'"
