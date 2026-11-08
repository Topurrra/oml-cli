# oml doc and oml explain-warning: topic list, one topic, one section, unknown names, warning lookups, size budgets of the
# knowledge files, of the default skill load, of oml --help and of each command reference section, links in skill/, the warnings.md table shape, and the `hint`
# that apply adds to a known new warning. Throws on the first failure.
param([string]$Oml, [string]$Tmp, [string]$Module)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
Require-TestFile $Module 'module for doc-check'
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'doc'
try {
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

# Size budget per topic in tokens (characters / 4). A file may be at most 15% over its budget. data-rules, rest and
# troubleshooting were raised by 50-100 for the SQL column order, CrossSiteRequests and same-name publish notes; ui-reactive by 50 for the polling-block pattern. memory
# was raised by 200 for "What recall learns" (usefulness, newest wins, stale records, suggested traps, oml learn). warnings was
# raised by 50 because the command is now named `oml explain-warning` (was `oml warn`) and the file names it twice.
$budget = [ordered]@{
  'ui-reactive' = 1550; 'osui-patterns' = 2000; 'data-rules' = 1250; 'logic-rules' = 1200; 'security' = 800
  'rest' = 950; 'processes-timers-email' = 1200; 'architecture-rules' = 800; 'warnings' = 2050
  'traditional-to-reactive' = 900; 'troubleshooting' = 2100; 'ui-checklist' = 700; 'testing' = 2000
  'best-practices' = 1300; 'rules' = 900; 'memory' = 1300; 'dif' = 800
}
# Where a topic's file is: skill/reference/<topic>.md, except rules (skill/rules.md; SKILL.md holds the same 11 rules).
function TopicFile($t) { if ($t -eq 'rules') { Join-Path $repo 'skill/rules.md' } else { Join-Path $ref "$t.md" } }

# 1. the topic list names every topic, one line each
$r = Run 'doc'
if ($r.Exit -ne 0) { Fail "oml doc exit $($r.Exit)" }
$listed = @($r.Text -split "`n" | Where-Object { $_ -match '^(\S+)  -  \S' } | ForEach-Object { $Matches[1] })
if ((($listed | Sort-Object) -join ',') -ne ((@($budget.Keys) | Sort-Object) -join ',')) { Fail "topics listed: $($listed -join ', ')" }
# SKILL.md's "Topics:" line names every topic, so agents know each one exists
$skillTopics = [regex]::Match((Get-Content -Raw (Join-Path $repo 'skill/SKILL.md')), '(?m)^Topics: (.*)$').Groups[1].Value
# rules is not on that line: SKILL.md holds the rules itself. memory is not either: the `write` field of a memory recall points to it, which saves tokens in the default load.
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

# 5. oml explain-warning: by id (any case), by title words, unknown
$r = Run 'explain-warning', 'NoErrorHandlingInFlow'
if ($r.Exit -ne 0 -or $r.Text -notmatch '\| `NoErrorHandlingInFlow` \| No Exception Handling \|' -or $r.Text -notmatch '^## Warnings') { Fail "warn by id: $($r.Text)" }
$r = Run 'explain-warning', 'noerrorhandlinginflow'
if ($r.Exit -ne 0 -or $r.Text -notmatch 'NoErrorHandlingInFlow') { Fail "warn by id, lower case: $($r.Text)" }
$r = Run 'explain-warning', 'security', 'warning'
if ($r.Exit -ne 0 -or $r.Text -notmatch 'EntityActionClientSide' -or $r.Text -notmatch 'ServerActionInAnonymousScreen') { Fail "warn by title: $($r.Text)" }
$r = Run 'explain-warning', 'locked module'
if ($r.Exit -ne 0 -or $r.Text -notmatch 'Stop:') { Fail "warn environment row: $($r.Text)" }
# oml's own warning ids (apply raises them in newWarnings) have rows too.
foreach ($id in 'PublicServerAction', 'UniqueIndexOnExistingEntity') { $r = Run 'explain-warning', $id; if ($r.Exit -ne 0 -or $r.Text -notmatch "\| ``$id`` \| \(oml\)") { Fail "warn $id`: $($r.Text)" } }
$r = Run 'explain-warning', 'NoSuchMessageId'
if ($r.Exit -ne 2 -or $r.Text -notmatch 'none is a close match' -or $r.Text -match 'Closest:') { Fail "warn unknown: exit $($r.Exit), $($r.Text)" }
$r = Run 'explain-warning', 'NoErrorHandlingInFlw'
if ($r.Exit -ne 2 -or $r.Text -notmatch 'Closest: NoErrorHandlingInFlow') { Fail "warn closest by id: $($r.Text)" }
$r = Run 'explain-warning', 'unused zzz'
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

# 7. size budgets, and the default skill load: SKILL.md, which holds the 11 rules, so no second read is needed.
#    Budget 2.7k tokens (+15%); SKILL.md + rules.md was 2.7k before the rules moved in.
foreach ($t in $budget.Keys) {
  $n = Tokens (TopicFile $t)
  if ($n -gt $budget[$t] * 1.15) { Fail "$t.md is $n tokens; budget $($budget[$t]) (+15%)" }
}
$load = Tokens (Join-Path $repo 'skill/SKILL.md')
if ($load -gt 2700 * 1.15) { Fail "SKILL.md is $load tokens; budget 2700 (+15%)" }

# 7b. rules 4 and 7 of AGENTS.md, and every rule on the website, read as in rules.md
$ruleLines = @{}
foreach ($m in [regex]::Matches((Get-Content -Raw (Join-Path $repo 'skill/rules.md')).Replace("`r`n", "`n"), '(?m)^(\d+)\. (.*)$')) { $ruleLines[[int]$m.Groups[1].Value] = $m.Value }
$agents = (Get-Content -Raw (Join-Path $repo 'skill/AGENTS.md')).Replace("`r`n", "`n")
foreach ($n in 4, 7) { if (-not $agents.Contains($ruleLines[$n] + "`n")) { Fail "AGENTS.md rule $n is not word for word rules.md" } }
# SKILL.md holds every rule word for word, and no longer sends agents to read rules.md.
$skillText = (Get-Content -Raw (Join-Path $repo 'skill/SKILL.md')).Replace("`r`n", "`n")
foreach ($n in $ruleLines.Keys) { if (-not $skillText.Contains($ruleLines[$n] + "`n")) { Fail "SKILL.md rule $n is not word for word rules.md" } }
if ($ruleLines.Count -ne 11 -or $skillText -match '\]\(rules\.md\)') { Fail "SKILL.md must hold the 11 rules and not link rules.md ($($ruleLines.Count) rules)" }
# 7c. the "Writing to the user" block (style of every text an agent writes to the user) is the same in SKILL.md and AGENTS.md
$skill = (Get-Content -Raw (Join-Path $repo 'skill/SKILL.md')).Replace("`r`n", "`n")
$writing = foreach ($text in $skill, $agents) { [regex]::Match($text, '(?ms)^## Writing to the user' + "`n" + '.*?(?=^## )').Value }
if (-not $writing[0] -or $writing[0] -cne $writing[1]) { Fail 'the "## Writing to the user" block differs between SKILL.md and AGENTS.md, or one lacks it' }
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

# 7e. the command reference (commands.md, about 47k tokens) is read by section only: oml doc commands lists the
# sections, each one prints within its budget (the largest, check with its rule table, is about 5.6k tokens), and no
# skill file sends an agent to the whole file. oml --help is the overview (budget 700 tokens, +15%); oml help --all is the whole usage.
$r = Run 'doc', 'commands'
if ($r.Exit -ne 0 -or $r.Text -notmatch 'Sections: ' -or $r.Text.Length -gt 2500) { Fail "oml doc commands: exit $($r.Exit), $($r.Text.Length) characters" }
$sections = @(($r.Text -split 'Sections: ')[1].Trim() -split ', ')
if ($sections.Count -lt 20) { Fail "oml doc commands lists only $($sections.Count) sections" }
$biggest = 0
foreach ($s in $sections) {
  $t = Run 'doc', "commands#$s"
  if ($t.Exit -ne 0) { Fail "oml doc commands#${s}: exit $($t.Exit), $($t.Text)" }
  $n = [math]::Ceiling($t.Text.Length / 4)
  if ($n -gt 5600 * 1.15) { Fail "oml doc commands#$s is $n tokens; budget 5600 (+15%)" }
  $biggest = [math]::Max($biggest, $n)
}
$r = Run 'doc', 'commands#no-such-section'
if ($r.Exit -ne 2 -or $r.Text -notmatch 'Sections: ') { Fail "unknown commands section: exit $($r.Exit), $($r.Text)" }
foreach ($f in Get-ChildItem (Join-Path $repo 'skill') -Recurse -Include *.md, *.jsonc | Where-Object Name -ne 'commands.md') {
  foreach ($line in Get-Content $f.FullName) {
    if ($line -match 'commands\.md' -and $line -notmatch 'never read `reference/commands\.md` whole') { Fail "$($f.Name) sends agents to the whole command reference: $line" }
  }
}
$help = [math]::Ceiling((Run '--help').Text.Length / 4)
if ($help -gt 700 * 1.15) { Fail "oml --help is $help tokens; budget 700 (+15%)" }
# oml help --all: every usage line. 2550: raised from 2400 for the three oml learn lines and the qa lines; 2700 for the oml map
# and oml mock lines; 2900 for the longer renamed commands and the test scenarios and test data lines; 2950 for oml skill refresh and qa page --style-conflicts. Agents read the overview
# (oml --help), not this.
$helpAll = [math]::Ceiling((Run 'help', '--all').Text.Length / 4)
if ($helpAll -gt 2950 * 1.15) { Fail "oml help --all is $helpAll tokens; budget 2950 (+15%)" }
# every command word of the full usage is named in the overview (a word is whole: `test` is not `testing`)
$fullUsage = (Run 'help', '--all').Text
$overview = (Run '--help').Text
$commandWords = @([regex]::Matches($fullUsage, '(?m)^oml ([a-z][a-z-]*)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($commandWords.Count -lt 40) { Fail "oml help --all has only $($commandWords.Count) command words" }
$unnamed = @($commandWords | Where-Object { $overview -notmatch "(?<![\w-])$([regex]::Escape($_))(?![\w-])" })
if ($unnamed.Count -gt 0) { Fail "oml --help does not name: $($unnamed -join ', ')" }

# 8. every relative link in skill/ resolves (examples in code fences and code spans, such as the image links of a gallery, are not links)
foreach ($f in Get-ChildItem (Join-Path $repo 'skill') -Recurse -Filter *.md) {
  foreach ($m in [regex]::Matches([regex]::Replace([regex]::Replace((Get-Content -Raw $f.FullName), '(?ms)^```.*?^```', ''), '`[^`\r\n]*`', ''), '\]\(([^)\s#]+)(#[^)\s]*)?\)')) {
    $target = $m.Groups[1].Value
    if ($target -match '^[a-z]+:') { continue }
    if (-not (Test-Path (Join-Path $f.DirectoryName $target))) { Fail "$($f.Name): broken link $target" }
  }
}
Write-Host "ok   doc: $($budget.Count) topics, sections, unknown names, warn lookups, $($ids.Count) ids, budgets (default load $load tokens, oml --help $help, oml help --all $helpAll, $($commandWords.Count) command words in the overview, largest of $($sections.Count) command sections $biggest), links"

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
} finally { Remove-TestWorkFolder $Tmp }
