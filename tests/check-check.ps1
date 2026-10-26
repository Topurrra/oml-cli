param([string]$Oml, [string]$Tmp)
# oml check: every rule fires exactly once on tests/check-bad.json and never on tests/check-clean.json,
# then the rules file, ignore globs, baseline, --fail-on, several modules, input errors and timing.
$ErrorActionPreference = 'Stop'
$tests = $PSScriptRoot
$dir = Join-Path $Tmp ('check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir -Force | Out-Null
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Invoke-Oml([string[]]$Arguments, [int]$Expect = 0) {
    $text = & $Oml @Arguments
    $code = $LASTEXITCODE
    Assert ($code -eq $Expect) "oml $($Arguments -join ' '): exit $code, expected $Expect. $text"
    return ($text | ConvertFrom-Json)
}
function New-Fixture([string]$Name, [string]$Kind, [string]$Spec) {
    $blank = Join-Path $dir "$Name-blank.oml"
    $out = Join-Path $dir "$Name.oml"
    Invoke-Oml @('new', $blank, '--name', $Name, '--kind', $Kind) | Out-Null
    $r = Invoke-Oml @('apply', $blank, (Join-Path $tests $Spec), $out)
    Assert ($r.saved -and $r.reopenNewErrors.Count -eq 0) "Fixture $Spec did not save: $($r | ConvertTo-Json -Depth 8 -Compress)"
    return $out
}
function Count($Report, [string]$Rule) { @($Report.findings | Where-Object rule -eq $Rule).Count }

$bad = New-Fixture 'CheckBad' 'CrossDevice' 'check-bad.json'
$clean = New-Fixture 'CheckClean' 'CrossDevice' 'check-clean.json'
$webBad = New-Fixture 'CheckWebBad' 'WebResponsive' 'check-web-bad.json'
$webClean = New-Fixture 'CheckWebClean' 'WebResponsive' 'check-web-clean.json'
$hashes = @{}; foreach ($f in @($bad, $clean, $webBad, $webClean)) { $hashes[$f] = (Get-FileHash $f).Hash }

# Default rules: each module rule exactly once, opt-in rules and several-module rules never.
$once = @('aggregate-unlimited','sql-unlimited','query-in-loop','server-call-in-client-loop','multiple-server-calls','server-call-in-client-event',
    'site-property-update','timer-long-timeout','anonymous-screen-server','sql-expand-inline','sql-expand-inline-input','getuserid-client',
    'getuserid-block-argument','site-property-secret','client-variable-secret','render-assign','public-entity-writable','long-flow','deep-nesting','duplicated-flow','missing-description',
    'unused-action','unused-query')
$json = Join-Path $dir 'bad.json'
$r = Invoke-Oml @('check', $bad, '--json', $json)
foreach ($rule in $once) { Assert ((Count $r $rule) -eq 1) "Rule $rule fired $(Count $r $rule) times on check-bad.json, expected 1" }
Assert ($r.findings.Count -eq $once.Count) "Unexpected findings on check-bad.json: $(($r.findings | ForEach-Object rule) -join ', ')"
foreach ($f in $r.findings) {
    foreach ($field in @('rule','severity','category','title','module','element','detail','fix','aiMentorName')) { Assert ($f.PSObject.Properties.Name -contains $field) "Finding field $field missing on $($f.rule)" }
    Assert ($f.severity -in @('high','medium','low') -and $f.category -in @('Security','Performance','Maintainability','Architecture') -and $f.module -eq 'CheckBad' -and $f.detail -and $f.fix) "Finding schema of $($f.rule)"
}
$where = @{ 'aggregate-unlimited'='MainFlow/Orders/LoadSql/AllOrders'; 'sql-unlimited'='MainFlow/Orders/LoadSql/AllCustomers'; 'query-in-loop'='LoopQuery/GetOrders'
    'server-call-in-client-loop'='ClientLoop/Ping'; 'multiple-server-calls'='ClientTwice'; 'server-call-in-client-event'='MainFlow/Dashboard/LoadOnReady'
    'sql-expand-inline'='InlinePrivate/Search'; 'sql-expand-inline-input'='InlineSearch/Search'; 'getuserid-client'='ClientUser/ForUser'
    'site-property-secret'='ApiToken'; 'public-entity-writable'='Customer'; 'long-flow'='LongFlow'; 'deep-nesting'='Nested'; 'duplicated-flow'='CopyB'
    'missing-description'='OrderDto'; 'unused-action'='NeverCalled'; 'client-variable-secret'='AuthToken'; 'render-assign'='MainFlow/Counter/CountRenders'; 'unused-query'='UnusedQuery/GetIgnored'; 'anonymous-screen-server'='MainFlow/PublicHome' }
foreach ($rule in $where.Keys) { Assert (($r.findings | Where-Object rule -eq $rule).element -eq $where[$rule]) "Element of $rule is '$(($r.findings | Where-Object rule -eq $rule).element)', expected '$($where[$rule])'" }
Assert (($r.findings | Where-Object rule -eq 'query-in-loop').aiMentorName -eq 'Aggregate or SQL query inside a cycle') 'AI Mentor name of query-in-loop'
Assert (($r.findings | Where-Object rule -eq 'query-in-loop').detail -like 'GetOrders runs inside the loop Inner.*') 'A query in nested loops is reported once, with the inner loop'
Assert ((($r.findings | Where-Object rule -eq 'render-assign').detail) -like "'CountRenders' changes the Counter's data*") 'render-assign names the handler'
Assert (($r.findings | Where-Object rule -eq 'render-assign').severity -eq 'medium' -and ($r.findings | Where-Object rule -eq 'client-variable-secret').severity -eq 'medium') 'render-assign and client-variable-secret are medium'
Assert (($r.findings | Where-Object rule -eq 'sql-expand-inline-input').aiMentorName -eq 'Vulnerable SQL injection' -and ($r.findings | Where-Object rule -eq 'sql-expand-inline').aiMentorName -eq '') 'Reactive AI Mentor names of the SQL rules'
Assert ($r.summary.findings -eq $once.Count -and ($r.summary.high + $r.summary.medium + $r.summary.low) -eq $once.Count -and $r.summary.ignored -eq 0) 'Summary counts'
Assert ($r.modules.Count -eq 1 -and $r.modules[0].name -eq 'CheckBad' -and $r.modules[0].seconds -lt 10) "Module timing: $($r.modules | ConvertTo-Json -Compress)"
$saved = Get-Content $json -Raw | ConvertFrom-Json
Assert ($saved.findings.Count -eq $once.Count -and $saved.schema -eq 1) 'JSON report file'
$r = Invoke-Oml @('check', $clean)
Assert ($r.findings.Count -eq 0) "False positives on check-clean.json: $(($r.findings | ForEach-Object { $_.rule + ' ' + $_.element }) -join '; ')"
$r = Invoke-Oml @('check', '--in', $webBad)
Assert ($r.findings.Count -eq 3 -and ($r.findings | Where-Object rule -eq 'large-session-variable').element -eq 'Cart') "Traditional fixture: $(($r.findings | ForEach-Object rule) -join ', ')"
$esc = $r.findings | Where-Object rule -eq 'escape-content-off'
Assert ($esc.element -eq 'MainFlow/Notice/RawMessage' -and $esc.aiMentorName -eq 'JavaScript or HTML injection' -and $esc.detail -like 'RawMessage shows Message with Escape Content = No.*') "escape-content-off: $($esc | ConvertTo-Json -Compress)"
Assert (($r.findings | Where-Object rule -eq 'sql-expand-inline-input').aiMentorName -eq 'SQL injection') 'Traditional Web AI Mentor name of the SQL rule'
$r = Invoke-Oml @('check', $webClean)
Assert ($r.findings.Count -eq 0) "False positives on check-web-clean.json: $(($r.findings | ForEach-Object rule) -join ', ')"

# An On After Fetch handler (spec key onAfterFetch) that refreshes another source: the Refresh Data node is named by its source.
$fetchSpec = Join-Path $dir 'after-fetch.json'
Set-Content $fetchSpec -Encoding utf8 @'
{ "entities": [{ "name": "Item", "description": "Items.", "attributes": [{ "name": "Title", "description": "Title." }] }],
  "screens": [{ "name": "Items",
    "aggregates": [{ "name": "GetItems", "sources": ["Item"], "maxRecords": 10, "onAfterFetch": "AfterItems" },
                   { "name": "GetMore", "sources": ["Item"], "maxRecords": 10, "fetch": "onDemand" }],
    "actions": [{ "name": "AfterItems", "description": "Loads more.", "flow": [{ "kind": "refreshData", "name": "LoadMore", "dataSource": "GetMore" }] }],
    "widgets": [{ "type": "Expression", "value": "GetItems.List.Length + GetMore.List.Length" }] }] }
'@
$fetchBlank = Join-Path $dir 'AfterFetch-blank.oml'; $fetchOut = Join-Path $dir 'AfterFetch.oml'
Invoke-Oml @('new', $fetchBlank, '--name', 'AfterFetch', '--kind', 'CrossDevice') | Out-Null
$fr = Invoke-Oml @('apply', $fetchBlank, $fetchSpec, $fetchOut)
Assert ($fr.saved) "after-fetch fixture did not save: $($fr | ConvertTo-Json -Depth 8 -Compress)"
$r = Invoke-Oml @('check', $fetchOut)
$hit = @($r.findings | Where-Object rule -eq 'server-call-in-client-event')
Assert ($hit.Count -eq 1 -and $hit[0].element -eq 'MainFlow/Items/AfterItems') "On After Fetch handler not reported: $($r.findings | ConvertTo-Json -Depth 4 -Compress)"
Assert ($hit[0].detail -like '*calls Refresh GetMore.*') "Refresh Data node label: '$($hit[0].detail)'"
Assert ((Count $r 'self-refreshing-aggregate') -eq 0) 'An On After Fetch handler that refreshes another source is not self-refreshing'

# An On After Fetch handler that refreshes its own database aggregate: reported on the aggregate.
# (The local storage case needs a local entity, which a spec cannot create: the self-test covers it.)
$selfSpec = Join-Path $dir 'self-refresh.json'
Set-Content $selfSpec -Encoding utf8 @'
{ "entities": [{ "name": "Item", "description": "Items.", "attributes": [{ "name": "Title", "description": "Title." }] }],
  "screens": [{ "name": "Items",
    "aggregates": [{ "name": "GetItems", "sources": ["Item"], "maxRecords": 10, "onAfterFetch": "Again" }],
    "actions": [{ "name": "Again", "description": "Reloads.", "flow": [{ "kind": "refreshData", "name": "Reload", "dataSource": "GetItems" }] }],
    "widgets": [{ "type": "Expression", "value": "GetItems.Count" }] }] }
'@
$selfBlank = Join-Path $dir 'SelfRefresh-blank.oml'; $selfOut = Join-Path $dir 'SelfRefresh.oml'
Invoke-Oml @('new', $selfBlank, '--name', 'SelfRefresh', '--kind', 'CrossDevice') | Out-Null
$sr = Invoke-Oml @('apply', $selfBlank, $selfSpec, $selfOut)
Assert ($sr.saved) "self-refresh fixture did not save: $($sr | ConvertTo-Json -Depth 8 -Compress)"
$r = Invoke-Oml @('check', $selfOut)
$hit = @($r.findings | Where-Object rule -eq 'self-refreshing-aggregate')
Assert ($hit.Count -eq 1 -and $hit[0].element -eq 'MainFlow/Items/GetItems' -and $hit[0].severity -eq 'medium') "self-refreshing-aggregate: $($r.findings | ConvertTo-Json -Depth 4 -Compress)"

# Rules file: init, refuse overwrite, disable, severity, option, opt-in rules, ignore globs.
$rulesFile = Join-Path $dir 'oml-rules.json'
$init = Invoke-Oml @('check', '--init-rules', '--out', $rulesFile)
$rules = Get-Content $rulesFile -Raw | ConvertFrom-Json
Assert ($init.rules -eq $rules.rules.Count -and $rules.rules.Count -ge 30 -and $rules.schema -eq 1 -and $rules.ignore.Count -eq 0) 'Init rules file'
foreach ($rule in $rules.rules) { foreach ($field in @('id','title','category','aiMentorName','enabled','severity','options')) { Assert ($rule.PSObject.Properties.Name -contains $field) "Rules file field $field on $($rule.id)" } }
Assert (-not ($rules.rules | Where-Object id -eq 'registered-screen').enabled -and -not ($rules.rules | Where-Object id -eq 'rest-no-auth').enabled) 'Opt-in rules start disabled'
Assert (($rules.rules | Where-Object id -eq 'long-flow').options.maxNodes -eq 40) 'Rule options in the rules file'
$before = (Get-FileHash $rulesFile).Hash
Invoke-Oml @('check', '--init-rules', '--out', $rulesFile) 2 | Out-Null
Assert ((Get-FileHash $rulesFile).Hash -eq $before) 'Init must not overwrite'
($rules.rules | Where-Object id -eq 'query-in-loop').enabled = $false
($rules.rules | Where-Object id -eq 'deep-nesting').severity = 'high'
($rules.rules | Where-Object id -eq 'long-flow').options.maxNodes = 50
($rules.rules | Where-Object id -eq 'registered-screen').enabled = $true
($rules.rules | Where-Object id -eq 'rest-no-auth').enabled = $true
$rules.ignore = @(@{ rule = 'duplicated-flow'; element = 'copy?'; reason = 'Kept on purpose.' }, @{ rule = '*'; element = 'CheckBad/OrderD*'; reason = 'Generated.' })
$rules | ConvertTo-Json -Depth 10 | Set-Content $rulesFile
$r = Invoke-Oml @('check', $bad, '--rules', $rulesFile)
Assert ((Count $r 'query-in-loop') -eq 0) 'Disabled rule still reported'
Assert (($r.findings | Where-Object rule -eq 'deep-nesting').severity -eq 'high') 'Severity override'
Assert ((Count $r 'long-flow') -eq 0) 'Option override (maxNodes 50)'
Assert ((Count $r 'registered-screen') -eq 1 -and ($r.findings | Where-Object rule -eq 'registered-screen').element -eq 'MainFlow/Profile') 'Opt-in registered-screen'
Assert ((Count $r 'rest-no-auth') -eq 1 -and ($r.findings | Where-Object rule -eq 'rest-no-auth').element -eq 'OrdersApi') 'Opt-in rest-no-auth'
Assert ((Count $r 'duplicated-flow') -eq 0 -and (Count $r 'missing-description') -eq 0 -and $r.summary.ignored -eq 2) "Ignore globs: ignored $($r.summary.ignored)"
Assert ($r.input.rules -eq $rulesFile) 'Rules path in the report'
$r = Invoke-Oml @('check', $clean, '--rules', $rulesFile)
Assert ($r.findings.Count -eq 0) "Opt-in rules on check-clean.json: $(($r.findings | ForEach-Object { $_.rule + ' ' + $_.element }) -join '; ')"
$invalid = [ordered]@{
    'unknown rule' = '{"rules":[{"id":"no-such-rule"}]}'; 'severity' = '{"rules":[{"id":"long-flow","severity":"critical"}]}'
    'option name' = '{"rules":[{"id":"long-flow","options":{"maxLines":3}}]}'; 'option value' = '{"rules":[{"id":"long-flow","options":{"maxNodes":0}}]}'
    'enabled' = '{"rules":[{"id":"long-flow","enabled":"yes"}]}'; 'duplicate' = '{"rules":[{"id":"long-flow"},{"id":"long-flow"}]}'
    'ignore reason' = '{"ignore":[{"rule":"long-flow","element":"X"}]}'; 'ignore rule' = '{"ignore":[{"rule":"nope","element":"X","reason":"r"}]}'
    'top key' = '{"rule":[]}'; 'json' = '{ not json'; 'array root' = '[]'; 'rules object' = '{"rules":{}}'
    'whole number' = '{"rules":[{"id":"long-flow","options":{"maxNodes":2.5}}]}'; 'null enabled' = '{"rules":[{"id":"long-flow","enabled":null}]}'
    'no options' = '{"rules":[{"id":"cycle","options":{"x":1}}]}'; 'ignore key' = '{"ignore":[{"rule":"*","element":"*","reason":"r","until":"x"}]}'
}
foreach ($case in $invalid.Keys) {
    $file = Join-Path $dir 'invalid-rules.json'
    [IO.File]::WriteAllText($file, $invalid[$case])
    $e = Invoke-Oml @('check', $bad, '--rules', $file) 2
    Assert ($e.error -like 'Rules file *') "Invalid rules ($case): $($e.error)"
}

# Baseline: new vs existing, and --fail-on only counts new findings when a baseline is given.
$changedSpec = Join-Path $dir 'more.json'
'{"serverActions":[{"name":"AlsoNeverCalled","flow":[]}]}' | Set-Content $changedSpec
$changed = Join-Path $dir 'CheckBad2.oml'
$applied = Invoke-Oml @('apply', $bad, $changedSpec, $changed)
Assert $applied.saved 'Changed module'
$r = Invoke-Oml @('check', $changed, '--baseline', $json)
Assert ($r.summary.new -eq 1 -and $r.summary.existing -eq $once.Count) "Baseline counts: new $($r.summary.new), existing $($r.summary.existing)"
Assert ((($r.findings | Where-Object status -eq 'new').element) -eq 'AlsoNeverCalled') 'The new finding'
Invoke-Oml @('check', $changed, '--baseline', $json, '--fail-on', 'low') 1 | Out-Null
Invoke-Oml @('check', $changed, '--baseline', $json, '--fail-on', 'medium') 0 | Out-Null
Invoke-Oml @('check', $bad, '--baseline', $json, '--fail-on', 'low') 0 | Out-Null
Invoke-Oml @('check', $bad, '--fail-on', 'high') 1 | Out-Null
Invoke-Oml @('check', $clean, '--fail-on', 'low') 0 | Out-Null
$e = Invoke-Oml @('check', $bad, '--fail-on', 'critical') 2
Assert ($e.error -like '*--fail-on*') 'Invalid --fail-on'
[IO.File]::WriteAllText((Join-Path $dir 'not-a-report.json'), '{"modules":[]}')
$e = Invoke-Oml @('check', $bad, '--baseline', (Join-Path $dir 'not-a-report.json')) 2
Assert ($e.error -like 'Baseline *findings*') "Invalid baseline: $($e.error)"

# check --diff: what a change fixed and what it introduced, per rule. $both fixes missing-description on OrderDto and keeps the new unused action.
$fixSpec = Join-Path $dir 'fix-description.json'
'{"edits":[{"op":"set","element":"OrderDto","properties":{"description":"An order."}}]}' | Set-Content $fixSpec
$both = Join-Path $dir 'CheckBad3.oml'
$fixed = Invoke-Oml @('apply', $changed, $fixSpec, $both)
Assert $fixed.saved 'Fixed and changed module'
$d = Invoke-Oml @('check', '--diff', $bad, $both) 1
Assert ($d.summary.fixed -eq 1 -and $d.summary.introduced -eq 1 -and $d.summary.unchanged -eq ($once.Count - 1)) "Diff summary: $($d.summary | ConvertTo-Json -Compress)"
Assert (@($d.rules).Count -eq 2) "Diff lists only the rules with a change: $(($d.rules | ForEach-Object rule) -join ', ')"
$mdRule = $d.rules | Where-Object rule -eq 'missing-description'
$ua = $d.rules | Where-Object rule -eq 'unused-action'
Assert ($mdRule.fixed -eq 1 -and $mdRule.introduced -eq 0 -and $mdRule.fixedFindings[0].element -eq 'OrderDto' -and @($mdRule.introducedFindings).Count -eq 0) "missing-description diff: $($mdRule | ConvertTo-Json -Compress -Depth 4)"
Assert ($ua.introduced -eq 1 -and $ua.fixed -eq 0 -and $ua.introducedFindings[0].element -eq 'AlsoNeverCalled' -and $ua.introducedFindings[0].module -eq 'CheckBad') "unused-action diff: $($ua | ConvertTo-Json -Compress -Depth 4)"
# The other way round, the description comes back and the new action goes: one fixed, one introduced, exit 1.
$d = Invoke-Oml @('check', '--diff', $both, $bad) 1
Assert ($d.summary.fixed -eq 1 -and $d.summary.introduced -eq 1) 'Diff reversed: the description comes back, the action goes'
$d = Invoke-Oml @('check', '--diff', $changed, $bad) 0
Assert ($d.summary.fixed -eq 1 -and $d.summary.introduced -eq 0 -and $d.summary.unchanged -eq $once.Count -and @($d.rules).Count -eq 1 -and $d.rules[0].rule -eq 'unused-action') "Diff with only fixes: $($d | ConvertTo-Json -Compress -Depth 4)"
$d = Invoke-Oml @('check', '--diff', $bad, $bad) 0
Assert ($d.summary.fixed -eq 0 -and $d.summary.introduced -eq 0 -and $d.summary.unchanged -eq $once.Count -and @($d.rules).Count -eq 0) 'Diff of a file with itself'
# The module name is not part of the match: a copy saved under another module name has the same findings.
$renamedCopy = New-Fixture 'CheckBadCopy' 'CrossDevice' 'check-bad.json'
$d = Invoke-Oml @('check', '--diff', $bad, $renamedCopy) 0
Assert ($d.summary.fixed -eq 0 -and $d.summary.introduced -eq 0 -and $d.summary.unchanged -eq $once.Count) "Diff of a renamed copy: $($d.summary | ConvertTo-Json -Compress)"
# --rules applies to both sides: with unused-action off, the introduced finding is not seen; an ignore hides a fix too.
$diffRules = Join-Path $dir 'diff-rules.json'
'{"rules":[{"id":"unused-action","enabled":false}],"ignore":[{"rule":"missing-description","element":"OrderDto","reason":"Test."}]}' | Set-Content $diffRules
$d = Invoke-Oml @('check', '--diff', $bad, $both, '--rules', $diffRules) 0
Assert ($d.summary.fixed -eq 0 -and $d.summary.introduced -eq 0) "Diff with --rules: $($d.summary | ConvertTo-Json -Compress)"
Invoke-Oml @('check', '--diff', $bad) 2 | Out-Null
Invoke-Oml @('check', '--diff', $bad, $both, '--baseline', $json) 2 | Out-Null
Invoke-Oml @('check', '--diff', $bad, (Join-Path $dir 'missing.oml')) 2 | Out-Null
Invoke-Oml @('check', '--diff', $bad, $fixSpec) 2 | Out-Null

# Several modules: the architecture rules join, and duplicates are found across modules.
$folder = Join-Path $dir 'team'; New-Item -ItemType Directory $folder | Out-Null
Copy-Item $bad, $clean $folder
$html = Join-Path $dir 'team.html'; $teamJson = Join-Path $dir 'team.json'
$watch = [Diagnostics.Stopwatch]::StartNew()
$layers = Join-Path $dir 'layers.json'; '{"CheckBad":"Core"}' | Set-Content $layers
$team = Invoke-Oml @('check', '--in', $folder, '--out', $html, '--json', $teamJson, '--team-label', 'Check Team', '--layers', $layers)
$seconds = $watch.Elapsed.TotalSeconds
$r = $team
Assert ($r.modules.Count -eq 2 -and $r.input.team -eq 'Check Team') 'Two modules'
Assert ((Count $r 'unused-public') -eq 2) "Architecture findings with several modules: $(Count $r 'unused-public') unused-public"
Assert (($r.findings | Where-Object rule -eq 'core-ui').module -eq 'CheckBad' -and $r.input.layers -eq $layers) '--layers sets the layer for the architecture rules'
$cross = $r.findings | Where-Object { $_.rule -eq 'duplicated-flow' -and $_.module -eq 'CheckClean' }
Assert ($cross.element -eq 'SimilarA' -and $cross.detail -like '*CheckBad.CopyA*') 'Duplicate across modules'
Assert (Test-Path $html) 'HTML report'
$page = Get-Content $html -Raw
Assert ($page -match '<script type="application/json" id="oml-check">([\s\S]*?)</script>') 'Embedded report JSON'
Assert (($Matches[1] | ConvertFrom-Json).findings.Count -eq $r.findings.Count) 'Embedded findings'
Assert ($page -notmatch '<(?:script|link|img)\b[^>]*(?:src|href)\s*=' -and $page -notmatch 'https?://') 'The page loads nothing from outside'
$e = Invoke-Oml @('check', '--in', $folder, '--out', $html) 2
Assert ($e.error -like 'Output already exists:*') 'HTML overwrite refusal'
$r = Invoke-Oml @('check', '--in', $folder, '--app', 'NoSuchApp') 2
Assert ($r.error -like 'No module was checked*') 'Empty selection'

# Inputs that are not checked are listed; with --fail-on they make the run fail (exit 2).
[IO.File]::WriteAllText((Join-Path $folder 'broken.oml'), 'not a module')
$r = Invoke-Oml @('check', '--in', $folder)
Assert (@($r.summary.skipped).Count -eq 1 -and $r.summary.skipped[0].reason -like 'Not checked:*broken.oml*') "Skipped input: $($r.summary.skipped | ConvertTo-Json -Compress)"
$r = Invoke-Oml @('check', '--in', $folder, '--fail-on', 'high') 2
Assert (@($r.summary.skipped).Count -eq 1) 'Skipped input with --fail-on'
# Outputs: a missing folder is refused before anything is read or written.
$e = Invoke-Oml @('check', $clean, '--json', (Join-Path (Join-Path $dir 'no-such-folder') 'r.json'), '--out', (Join-Path $dir 'x.html')) 2
Assert ($e.error -like 'Output folder does not exist:*' -and -not (Test-Path (Join-Path $dir 'x.html'))) 'Missing output folder'

# Inside oml run, without --team.
$steps = Join-Path $dir 'steps.json'
[ordered]@{ steps = @(, [string[]]@('check', $clean)) + @(, [string[]]@('check', $clean, '--team', 'X')) } | ConvertTo-Json -Depth 5 | Set-Content $steps
$run = Invoke-Oml @('run', $steps, '--continue') 2
Assert ($run.results[0].exitCode -eq 0 -and $run.results[0].output.summary.findings -eq 0 -and $run.results[1].output.error -like "'check --team' is not allowed inside 'oml run'*") 'check inside oml run'

# missing-description: every kind once without and once with a description, the well-known names, allNames and attributes.
Copy-Item (Join-Path $tests 'check-desc.svg') $dir
$desc = New-Fixture 'CheckDesc' 'CrossDevice' 'check-desc.json'
function Same($A, $B) { (@($A) -join '|') -eq (@($B) -join '|') }
function Missing($Report) { @($Report.findings | Where-Object rule -eq 'missing-description' | ForEach-Object { ($_.detail -split ' has no description')[0] + ' @ ' + $_.element } | Sort-Object) }
$expected = @('Block BareCard @ MainFlow/BareCard', 'Image BareLogo @ BareLogo', 'Parameter Query of BareHome @ MainFlow/BareHome/Query', 'Parameter Ref of BareProc @ BareProc/Ref',
    'Parameter Title of BareCard @ MainFlow/BareCard/Title', 'Process BareProc @ BareProc', 'Role BareRole @ BareRole', 'Screen BareHome @ MainFlow/BareHome',
    'SiteProperty BareProp @ BareProp', 'Theme BareTheme @ BareTheme', 'Timer BareTimer @ BareTimer') | Sort-Object
$r = Invoke-Oml @('check', $desc)
Assert (Same (Missing $r) $expected) "missing-description kinds: $((Missing $r) -join '; ')"
$timer = $r.findings | Where-Object { $_.detail -like 'Timer *' }
Assert ($timer.detail -like '*timers have no public flag*' -and ($r.findings | Where-Object { $_.detail -like 'SiteProperty *' }).detail -like '*site properties have no public flag*') 'Timers and site properties say they have no public flag'
Assert (-not ($r.findings | Where-Object { $_.detail -like '*Lookup*' -or $_.detail -like '*FullCard*' -or $_.element -like '*Full*' })) 'Filled descriptions and Id/Name parameters are not reported'
# The options: allNames also reports the well-known names, attributes reports attributes of public entities and structures.
function Rules([hashtable]$Options) {
    $file = Join-Path $dir ('desc-rules-' + [guid]::NewGuid().ToString('N') + '.json')
    @{ rules = @(@{ id = 'missing-description'; options = $Options }) } | ConvertTo-Json -Depth 6 | Set-Content $file
    return $file
}
$r = Invoke-Oml @('check', $desc, '--rules', (Rules @{ allNames = 1 }))
$extra = @(Missing $r | Where-Object { $expected -notcontains $_ })
Assert (Same $extra (@('Parameter Id of Lookup @ Lookup/Id', 'Parameter Name of BareCard @ MainFlow/BareCard/Name') | Sort-Object)) "allNames: $($extra -join '; ')"
$r = Invoke-Oml @('check', $desc, '--rules', (Rules @{ attributes = 1 }))
$extra = @(Missing $r | Where-Object { $expected -notcontains $_ })
Assert (Same $extra (@('Attribute Amount of Ledger @ Ledger/Amount', 'Attribute Cell of Row @ Row/Cell') | Sort-Object)) "attributes: $($extra -join '; ')"
$r = Invoke-Oml @('check', $desc, '--rules', (Rules @{ allNames = 1; attributes = 1 }))
Assert ((Missing $r).Count -eq $expected.Count + 6) "allNames and attributes together (the 2 names, 2 attributes, and Ledger's Id and Name attributes): $((Missing $r).Count)"
foreach ($value in 2, -1) { $e = Invoke-Oml @('check', $desc, '--rules', (Rules @{ allNames = $value })) 2; Assert ($e.error -like '*option allNames must be 0 or 1*') "allNames $value is refused: $($e.error)" }
# Traditional Web: a session variable has no public flag and no spec key for a description; edits.set gives it one.
$webSpec = Get-Content (Join-Path $tests 'check-web-clean.json') -Raw | ConvertFrom-Json
$webSpec.PSObject.Properties.Remove('edits')
$webNoDesc = Join-Path $dir 'web-nodesc.json'; $webSpec | ConvertTo-Json -Depth 30 | Set-Content $webNoDesc
$blank = Join-Path $dir 'WebNoDesc-blank.oml'; $webMod = Join-Path $dir 'WebNoDesc.oml'
Invoke-Oml @('new', $blank, '--name', 'WebNoDesc', '--kind', 'WebResponsive') | Out-Null
$w = Invoke-Oml @('apply', $blank, $webNoDesc, $webMod); Assert $w.saved 'Web fixture without descriptions'
$r = Invoke-Oml @('check', $webMod)
Assert (Same (Missing $r) @('SessionVariable CartId @ CartId')) "Session variable: $((Missing $r) -join '; ')"
# set paths: the plain element name or a Kind: prefix works for each kind; a parameter is Owner/Param, a well-known name too.
$paths = @('BareLogo', 'BareProc', 'BareProc/Ref', 'BareProp', 'BareRole', 'BareTheme', 'BareTimer', 'BareCard', 'BareCard/Title', 'BareCard/Name', 'BareHome', 'BareHome/Query', 'ServerAction:Lookup/Id', 'Entity:Ledger/Amount', 'Entity:Ledger/Id', 'Entity:Ledger/Name', 'Structure:Row/Cell')
$setSpec = Join-Path $dir 'set-descriptions.json'
[ordered]@{ edits = @($paths | ForEach-Object { [ordered]@{ op = 'set'; element = $_; properties = [ordered]@{ description = "Set on $_" } } }) } | ConvertTo-Json -Depth 6 | Set-Content $setSpec
$setOut = Join-Path $dir 'CheckDesc-set.oml'
$a = Invoke-Oml @('apply', $desc, $setSpec, $setOut)
Assert ($a.saved -and $a.created.Count -eq $paths.Count) "set descriptions: $($a | ConvertTo-Json -Depth 5 -Compress)"
$r = Invoke-Oml @('check', $setOut, '--rules', (Rules @{ allNames = 1; attributes = 1 }))
Assert ((Missing $r).Count -eq 0) "Findings left after set: $((Missing $r) -join '; ')"
# The element of each finding is a set path as it is (commands.md says so).
$all = Invoke-Oml @('check', $desc, '--rules', (Rules @{ allNames = 1; attributes = 1 }))
$byElement = Join-Path $dir 'set-by-element.json'
[ordered]@{ edits = @($all.findings | Where-Object { $_.rule -eq 'missing-description' -and $_.element } | ForEach-Object { [ordered]@{ op = 'set'; element = $_.element; properties = [ordered]@{ description = "Set on $($_.element)" } } }) } | ConvertTo-Json -Depth 6 | Set-Content $byElement
$byOut = Join-Path $dir 'CheckDesc-by-element.oml'
$a = Invoke-Oml @('apply', $desc, $byElement, $byOut)
Assert ($a.saved -and $a.created.Count -eq $paths.Count) "set by finding element: $($a | ConvertTo-Json -Depth 5 -Compress)"
Assert ((Missing (Invoke-Oml @('check', $byOut, '--rules', (Rules @{ allNames = 1; attributes = 1 })))).Count -eq 0) 'Findings left after set by finding element'
$setSession = Join-Path $dir 'set-session.json'; '{"edits":[{"op":"set","element":"SessionVariable:CartId","properties":{"description":"Cart."}}]}' | Set-Content $setSession
$a = Invoke-Oml @('apply', $webMod, $setSession, (Join-Path $dir 'WebNoDesc-set.oml'))
Assert ($a.saved -and (Missing (Invoke-Oml @('check', (Join-Path $dir 'WebNoDesc-set.oml')))).Count -eq 0) 'set description of a session variable'
# duplicated-flow on odd flows (no nodes of its own, a node without an outgoing connector, a comment alone, a grouped
# query), counted from one node: the rule runs for the module, nothing is skipped, and the one real copy is found.
$flows = New-Fixture 'CheckFlows' 'CrossDevice' 'check-flows.json'
$oneNode = Join-Path $dir 'one-node-rules.json'
Invoke-Oml @('check', '--init-rules', '--out', $oneNode) | Out-Null
$oneRules = Get-Content $oneNode -Raw | ConvertFrom-Json
($oneRules.rules | Where-Object id -eq 'duplicated-flow').options.minNodes = 1
$oneRules | ConvertTo-Json -Depth 10 | Set-Content $oneNode
$r = Invoke-Oml @('check', $flows, '--rules', $oneNode)
Assert (@($r.summary.skipped).Count -eq 0) "duplicated-flow failed on odd flows: $(($r.summary.skipped | ConvertTo-Json -Compress))"
Assert ((Count $r 'duplicated-flow') -eq 1 -and ($r.findings | Where-Object rule -eq 'duplicated-flow').element -eq 'RaiseTwin') "duplicated-flow on odd flows: $(($r.findings | ForEach-Object { $_.rule + ' ' + $_.element }) -join '; ')"

# Input errors.
Invoke-Oml @('check') 2 | Out-Null
Invoke-Oml @('check', (Join-Path $dir 'missing.oml')) 2 | Out-Null
Invoke-Oml @('check', $bad, '--bogus', 'x') 2 | Out-Null
foreach ($f in $hashes.Keys) { Assert ((Get-FileHash $f).Hash -eq $hashes[$f]) "Input changed: $f" }
Write-Host ("ok   check: {0} rules once on the bad fixture, none on the clean ones, rules file, ignore, baseline, --fail-on, two modules in {1:N1}s ({2}s and {3}s per module)" -f $once.Count, $seconds, $team.modules[0].seconds, $team.modules[1].seconds)
