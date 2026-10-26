param([string]$Oml = "$PSScriptRoot\..\src\bin\Release\net10.0\oml.exe", [string]$Tmp = "$PSScriptRoot\..\tmp", [switch]$Page)
# oml review: a before/after pair made with oml apply (tests/review-before.json, then tests/review-after.json) covers every
# area; each change is found in plain words, renames stay renames, risks name their rule, new findings come from oml check,
# and the outputs (JSON, --md, --notes, --out) and input errors behave. -Page: the HTML page in headless Edge/Chrome instead.
# The risks that oml apply refuses (deletes, entity renames, type changes, data-changing SQL, ...) are in the self-test.
$ErrorActionPreference = 'Stop'
$dir = Join-Path $Tmp ('review-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $dir | Out-Null
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Invoke-Oml([string[]]$Arguments, [int]$Expect = 0) {
    $text = (& $Oml @Arguments 2>&1) -join "`n"
    $code = $LASTEXITCODE
    Assert ($code -eq $Expect) "oml $($Arguments -join ' '): exit $code, expected $Expect. $text"
    return $text
}

$blank = Join-Path $dir 'blank.oml'; $before = Join-Path $dir 'Before.oml'; $after = Join-Path $dir 'After.oml'
$producerBlank = Join-Path $dir 'producer-blank.oml'; $producer = Join-Path $dir 'ReferenceProducer.oml'; $refs = Join-Path $dir 'references.json'
Invoke-Oml @('new', $blank, '--name', 'ReviewApp', '--kind', 'CrossDevice') | Out-Null
Invoke-Oml @('new', $producerBlank, '--name', 'ReferenceProducer', '--kind', 'CrossDevice') | Out-Null
# The change also adds a reference to a producer module.
@{ references = @(@{ producer = $producer; elements = @('GetCustomerById') }) } | ConvertTo-Json -Depth 5 | Set-Content $refs -Encoding utf8
foreach ($step in @(@($producerBlank, @((Join-Path $PSScriptRoot 'references-producer.json')), $producer), @($blank, @((Join-Path $PSScriptRoot 'review-before.json')), $before),
        @($before, @((Join-Path $PSScriptRoot 'review-after.json'), $refs), $after))) {
    $r = Invoke-Oml (@('apply', $step[0]) + $step[1] + @($step[2])) | ConvertFrom-Json
    Assert ($r.saved -and $r.newErrors.Count -eq 0) "$($step[1] -join ', ') did not apply: $($r | ConvertTo-Json -Depth 8 -Compress)"
}
$beforeHash = (Get-FileHash $before).Hash; $afterHash = (Get-FileHash $after).Hash

if ($Page) {
    $html = Join-Path $dir 'review.html'
    $report = Invoke-Oml @('review', $before, $after, '--out', $html) | ConvertFrom-Json
    $pageHtml = [IO.File]::ReadAllText($html)
    $data = [regex]::Match($pageHtml, '<script type="application/json" id="oml-review">(.*?)</script>', 'Singleline')
    Assert $data.Success 'Embedded review JSON missing'
    Assert (($data.Groups[1].Value | ConvertFrom-Json).changes.Count -eq $report.summary.added + $report.summary.removed + $report.summary.recreated + $report.summary.renamed + $report.summary.changed) 'Embedded changes differ from the summary'
    Assert ($data.Groups[1].Value -notmatch '[<>&]') 'Unescaped HTML characters in embedded data'
    Assert (-not ($pageHtml -match '<(?:script|link|img)\b[^>]*(?:src|href)\s*=' -or $pageHtml -match '@import|url\(')) 'Review page loads external resources'
    Write-Host "ok   review page: offline, escaped JSON, $($report.summary.changed) changed elements"
    $browser = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe") | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $browser) { Write-Host 'skip review page browser checks: Edge/Chrome not installed'; return }
    $capture = '<script>window.pageErrors=[];addEventListener("error",e=>pageErrors.push(e.message));console.error=(...args)=>pageErrors.push(args.join(" "));</script>'
    $probe = @'
<script>
(() => {
 const checks=[];
 const check=(ok,label)=>{if(!ok)throw Error(label);checks.push(label);};
 const $=id=>document.getElementById(id), visible=()=>[...document.querySelectorAll('#changes .card')].filter(c=>!c.hidden);
 const data=JSON.parse($('oml-review').textContent);
 const set=(id,v)=>{$(id).value=v;$(id).dispatchEvent(new Event(id==='f-text'?'input':'change'));};
 try {
  check(document.querySelectorAll('#changes .card').length===data.changes.length,'one card per change');
  check(document.querySelectorAll('#risks .card').length===data.risks.length&&data.risks.length>0,'one card per risk');
  check(document.querySelectorAll('#findings .card').length===data.newFindings.length,'one card per new finding');
  check($('note').textContent===data.note&&data.note.includes('Logic bugs are not covered'),'the not-covered note');
  check(document.querySelectorAll('.stat').length===9,'counts');
  check(document.querySelectorAll('#changes .diff div.add').length>0&&document.querySelectorAll('#changes .diff div.del').length>0,'changed lines');
  check(!/[\\\/]/.test(data.before+data.after)&&!/\d{2}:\d{2}:\d{2}\./.test($('generated').textContent),'file names only, readable time');
  set('f-area','data');check(visible().length===data.changes.filter(c=>c.area==='data').length&&visible().length>0,'area filter');
  set('f-area','');set('f-change','added');check(visible().length===data.changes.filter(c=>c.change==='added').length&&visible().length>0,'change filter');
  set('f-change','');set('f-text','CreateOrderFor');check(visible().length===1,'text search');
  set('f-text','no such text at all');check(visible().length===0&&!document.querySelector('#changes .empty').hidden,'empty state');
  set('f-text','');check(visible().length===data.changes.length,'reset filters');
  const wanted='__THEME__';if(document.documentElement.dataset.theme!==wanted)$('theme').click();check(document.documentElement.dataset.theme===wanted,'color theme');
 } catch(e) { window.pageErrors.push(e.message); }
 const result=document.createElement('pre');result.id='browser-check-result';result.hidden=true;result.textContent=JSON.stringify({checks,errors:window.pageErrors});document.body.append(result);
})();
</script>
'@
    $profile = Join-Path $dir 'browser-profile'
    foreach ($theme in @('light','dark')) {
        $testHtml = Join-Path $dir "browser-$theme.html"; $dom = Join-Path $dir "browser-$theme-dom.html"; $log = Join-Path $dir "browser-$theme.log"
        $screenshot = [IO.Path]::GetFullPath((Join-Path $Tmp "review-$theme.png"))
        [IO.File]::WriteAllText($testHtml, $pageHtml.Replace('</head>', "$capture</head>").Replace('</body>', $probe.Replace('__THEME__', $theme) + '</body>'))
        $url = ([uri][IO.Path]::GetFullPath($testHtml)).AbsoluteUri
        $arguments = @('--headless=new','--disable-gpu','--no-first-run',"--user-data-dir=`"$profile`"",'--window-size=1280,1600',"--screenshot=`"$screenshot`"",'--dump-dom',"`"$url`"")
        $process = Start-Process -FilePath $browser -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $dom -RedirectStandardError $log
        if (-not $process.WaitForExit(20000)) { $process.Kill(); Write-Host 'skip review page browser checks: headless export timed out'; return }
        # Windows browser launchers can exit before the headless child closes stdout.
        for ($attempt = 0; $attempt -lt 100; $attempt++) {
            $stream = [IO.File]::Open($dom, 'Open', 'Read', 'ReadWrite'); $reader = [IO.StreamReader]::new($stream)
            try { $rendered = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $result = [regex]::Match($rendered, '<pre id="browser-check-result"[^>]*>(.*?)</pre>', 'Singleline')
            if ($result.Success) { break }
            Start-Sleep -Milliseconds 100
        }
        if (-not $result.Success) { Write-Host "skip review page browser checks: headless export unavailable (see $log)"; return }
        $checks = [Net.WebUtility]::HtmlDecode($result.Groups[1].Value) | ConvertFrom-Json
        if ($checks.errors.Count -gt 0) { throw "Review page browser errors ($theme): $($checks.errors -join '; ')" }
        Write-Host "ok   review page browser $theme`: $($checks.checks.Count) checks, no script errors ($screenshot)"
    }
    return
}

# --- The report ---
$md = Join-Path $dir 'review.md'; $notes = Join-Path $dir 'notes.md'; $html = Join-Path $dir 'review.html'
$text = Invoke-Oml @('review', $before, $after, '--md', $md, '--notes', $notes, '--out', $html)
$r = $text | ConvertFrom-Json
Assert ($text.StartsWith('{"module":"ReviewApp","summary":') -and $text.IndexOf('"summary"') -lt $text.IndexOf('"changes"')) "Summary counts come first: $($text.Substring(0, [Math]::Min(120, $text.Length)))"
Assert ((Get-FileHash $before).Hash -eq $beforeHash -and (Get-FileHash $after).Hash -eq $afterHash) 'review changed an input file'
$s = $r.summary
Assert ($s.added -eq 4 -and $s.removed -eq 0 -and $s.recreated -eq 0 -and $s.renamed -eq 0) "Summary: $($s | ConvertTo-Json -Compress -Depth 5)"
function Change([string]$Name) { $c = @($r.changes | Where-Object name -eq $Name); Assert ($c.Count -eq 1) "Expected one change for $Name, got $($c.Count): $($r.changes | ConvertTo-Json -Depth 5 -Compress)"; $c[0] }
function Details([string]$Name) { (Change $Name).details -join ' | ' }
# The JSON keeps 5 details per element; the Markdown has all of them.
$all = Get-Content $md -Raw
function Has([string]$Name, [string]$Text) { Assert ($all.Contains($Text.Replace('_', '\_').Replace('*', '\*').Replace('[', '\[').Replace(']', '\]')) -or (Details $Name).Contains($Text)) "$Name`: '$Text' missing. JSON: $(Details $Name)" }
$expect = [ordered]@{
    'Customer' = @('data', 'changed', @('index IX_Email added: Email (unique)', 'attribute Phone (Phone Number) added'))
    'Status' = @('data', 'changed', @('record Cancelled added'))
    'OrderDto' = @('data', 'changed', @('attribute Note (Text) added'))
    'Invoice' = @('data', 'added', @('attributes: Id (Long Integer, mandatory), Number (Text)'))
    'CreateOrderFor' = @('logic', 'changed', @('input Note (Text) added', 'step Audit added (set Rec.Total = Total), after PrepareOrder, before CreateOrder',
        'step ValidTotal: condition Total >= 0 -> Total > 0', 'step Prepare renamed to PrepareOrder'))
    'FormatTotal' = @('logic', 'changed', @('local variable Prefix renamed to LabelPrefix', 'step Build: set Label Prefix + Value -> LabelPrefix + Value'))
    'SumOrders' = @('logic', 'changed', @('filter Order.Total > 0 added in GetOrders'))
    'Report' = @('logic', 'changed', @('step ReadTotals: statement SELECT {Order}.[Total] FROM {Order} -> SELECT {Order}.[Total] FROM {Order} WHERE {Order}.[Total] > 0'))
    'ExportOrders' = @('logic', 'added', @('public'))
    'Card' = @('ui', 'changed', @('1 widget added: expression CardFooter'))
    'Orders' = @('ui', 'changed', @('1 widget added: expression Hint', 'widget expression CountText: value "Orders: " + GetOrders.Count -> "Total orders: " + GetOrders.Count',
        'widget input TotalInput renamed to AmountInput', 'widget button SaveButton: style "btn btn-primary" -> "btn btn-secondary"', 'style sheet added (', 'roles Manager, Registered -> Anonymous'))
    'AppTheme' = @('ui', 'changed', @('style sheet changed (23 -> 41 characters, +18)'))
    'Weather' = @('integrations', 'changed', @('REST method GetAlerts added'))
    'OrdersApi' = @('integrations', 'changed', @('REST method Health added'))
    'Auditor' = @('security', 'added', @())
    'MaxItems' = @('settings', 'changed', @('default value 50 -> 100'))
    'Nightly' = @('settings', 'changed', @('timeout in minutes 20 -> 30'))
    'Approval' = @('settings', 'changed', @('detail (none) -> "Order approval"'))
    'ReferenceProducer' = @('references', 'added', @('uses '))
}
foreach ($name in $expect.Keys) {
    $area, $change, $lines = $expect[$name]
    $c = Change $name
    Assert ($c.area -eq $area -and $c.change -eq $change) "$name`: $($c.area)/$($c.change), expected $area/$change"
    foreach ($line in $lines) { Has $name $line }
}
Assert ($r.changes.Count -eq $expect.Count) "Unexpected changes: $(($r.changes | Where-Object { $expect.Keys -notcontains $_.name } | ForEach-Object { "$($_.kind) $($_.name)" }) -join ', ')"
# Renamed parts are renames: never an add plus a remove of the same element.
Assert (-not ($all -match 'step (Prepare|PrepareOrder) (added|removed)|AmountInput.*added|TotalInput.*removed|LabelPrefix \(Text\) added')) 'A rename was reported as an add and a remove'
Assert ($r.changes | Where-Object name -eq 'Orders' | Where-Object { $_.moreDetails -gt 0 }) 'More than 5 details are cut in the JSON and counted'
Assert ($r.truncated.changesWithMoreDetails -ge 1 -and $r.truncated.hint) "truncated: $($r.truncated | ConvertTo-Json -Compress)"

# Risks name their rule; new findings are check(after) minus check(before).
$risks = @($r.risks | ForEach-Object { "$($_.rule) $($_.element): $($_.text)" })
foreach ($risk in @('^5 Customer: unique index IX_Email \(Email\) on the existing entity', '^7 ExportOrders: server action .ExportOrders. is public now', '^7 MainFlow/Orders: screen .Orders. allows Anonymous now')) {
    Assert (@($risks -match $risk).Count -eq 1) "Risk $risk missing: $($risks -join ' | ')"
}
Assert ($risks.Count -eq 3) "Unexpected risks: $($risks -join ' | ')"
$findings = @($r.newFindings | ForEach-Object { "$($_.rule) $($_.element)" })
Assert ($findings -contains 'anonymous-screen-server MainFlow/Orders' -and $findings -contains 'missing-description ExportOrders') "New findings: $($findings -join ', ')"
$checkBefore = Invoke-Oml @('check', $before) | ConvertFrom-Json
Assert (-not ($checkBefore.findings | Where-Object { $_.rule -eq 'anonymous-screen-server' })) 'The before module must not have the anonymous finding'
Assert ($r.note -eq 'Logic bugs are not covered; 0 findings does not mean the code is correct.') 'The not-covered note in JSON'
# The summary counts what the changes list says, so a script never parses the Markdown.
Assert ($r.summary.changes -eq $r.changes.Count) "summary.changes $($r.summary.changes) vs $($r.changes.Count) changes"
$areas = @($r.changes | Group-Object area)
Assert (@($r.summary.byArea.PSObject.Properties).Count -eq $areas.Count) "byArea areas: $($r.summary.byArea | ConvertTo-Json -Compress -Depth 5)"
foreach ($g in $areas) { Assert ($r.summary.byArea.($g.Name) -eq $g.Count) "byArea.$($g.Name) is $($r.summary.byArea.($g.Name)), the list has $($g.Count)" }
$kinds = $r.summary.changeKinds
Assert ($kinds.'default value' -eq 1 -and $kinds.'style sheet' -ge 2 -and $kinds.'timeout in minutes' -eq 1 -and $kinds.detail -eq 1) "changeKinds: $($kinds | ConvertTo-Json -Compress -Depth 5)"
$counts = @($kinds.PSObject.Properties.Value)
Assert ((($counts | Sort-Object -Descending) -join ',') -eq ($counts -join ',')) "changeKinds are sorted, most frequent first: $($counts -join ',')"

# --md, --notes and --out.
foreach ($file in @($md, $notes, $html)) { Assert ((Get-Content $file -Raw).Contains('Logic bugs are not covered; 0 findings does not mean the code is correct.')) "Note missing in $file" }
Assert ($all.Contains('## Risks') -and $all.Contains('| 7 (security stays) | ExportOrders |') -and $all.Contains('### Data model') -and $all.Contains('## New check findings')) 'Markdown sections'
$notesText = Get-Content $notes -Raw
Assert ($notesText.Contains('- New entity Invoice.') -and $notesText.Contains('- Changed screen Orders.') -and $notesText.Contains('Test with care')) "Release notes: $notesText"
Write-Host "ok   review: $($expect.Count) changed elements in every area, renames, $($risks.Count) risks, $($findings.Count) new findings, md/notes/html"

# Changed lines in --md (not in the JSON): the theme's style sheet.
Assert ($all.Contains('Changed lines of theme AppTheme: style sheet') -and $all.Contains('+ .title { color: navy; font-weight: 600; }') -and $all.Contains('- .title { color: navy; }')) 'Changed lines of the style sheet in the Markdown'
Assert (-not $text.Contains('"texts"') -and -not $text.Contains('Changed lines')) 'The JSON on stdout has no changed lines'

# A re-saved copy (an apply that changes nothing) has no changes.
$noop = Join-Path $dir 'noop.json'; $copy = Join-Path $dir 'Copy.oml'
Set-Content $noop '{}' -Encoding utf8
Invoke-Oml @('apply', $before, $noop, $copy) | Out-Null
$c = Invoke-Oml @('review', $before, $copy) | ConvertFrom-Json
Assert ($c.changes.Count -eq 0 -and $c.risks.Count -eq 0 -and $c.summary.info -eq 0) "A re-saved copy: $($c.changes | ConvertTo-Json -Depth 5 -Compress)"

# Whitespace only, and a secret site property whose value must never be printed.
$more = Join-Path $dir 'more.json'; $after2 = Join-Path $dir 'After2.oml'
Set-Content $more '{ "edits": [
  { "op": "set", "element": "Report/ReadTotals", "properties": { "statement": "SELECT {Order}.[Total]\n  FROM {Order}\n  WHERE {Order}.[Total] > 0" } },
  { "op": "set", "element": "MaxItems", "properties": { "isSecret": true, "defaultValue": "987654", "description": "Changed text." } } ] }' -Encoding utf8
Invoke-Oml @('apply', $after, $more, $after2) | Out-Null
$md2 = Join-Path $dir 'more.md'; $html2 = Join-Path $dir 'more.html'
$text2 = Invoke-Oml @('review', $after, $after2, '--md', $md2, '--out', $html2)
$r3 = $text2 | ConvertFrom-Json
Assert ((@($r3.changes | Where-Object name -eq 'Report').details) -contains 'step ReadTotals: statement changed (whitespace only)') "Whitespace only: $($r3.changes | ConvertTo-Json -Depth 5 -Compress)"
Assert ((@($r3.changes | Where-Object name -eq 'MaxItems').details) -contains 'default value changed (secret: values not shown)') "Secret site property: $($r3.changes | ConvertTo-Json -Depth 5 -Compress)"
Assert ($r3.summary.changeKinds.description -eq 1 -and $r3.summary.changeKinds.statement -eq 1 -and $r3.summary.changeKinds.'default value' -eq 1 -and @($r3.changes.details | Where-Object { $_ -eq 'description changed' }).Count -eq 1) "Summary of the second review: $($r3.summary | ConvertTo-Json -Compress -Depth 5)"
foreach ($out in @($text2, (Get-Content $md2 -Raw), (Get-Content $html2 -Raw))) { Assert (-not $out.Contains('987654') -and -not $out.Contains('Changed lines of step ReadTotals')) 'A secret value or a whitespace-only change was printed' }

# Traditional Web: an input's Type and a button's Label are settings of their own.
$webBlank = Join-Path $dir 'web-blank.oml'; $web1 = Join-Path $dir 'Web1.oml'; $web2 = Join-Path $dir 'Web2.oml'
Invoke-Oml @('new', $webBlank, '--name', 'ReviewWeb', '--kind', 'WebResponsive') | Out-Null
Set-Content (Join-Path $dir 'web1.json') '{ "screens": [{ "name": "Login", "locals": [{ "name": "Pwd", "type": "Text" }], "widgets": [{ "type": "Input", "name": "PwdInput", "variable": "Pwd" }, { "type": "Button", "name": "Go", "label": "\"Go\"", "onClick": "Login" }] }] }' -Encoding utf8
Set-Content (Join-Path $dir 'web2.json') '{ "edits": [{ "op": "set", "element": "Login/PwdInput", "properties": { "type": "Email" } }, { "op": "set", "element": "Login/Go", "properties": { "label": "\"Sign in\"" } }] }' -Encoding utf8
Invoke-Oml @('apply', $webBlank, (Join-Path $dir 'web1.json'), $web1) | Out-Null
Invoke-Oml @('apply', $web1, (Join-Path $dir 'web2.json'), $web2) | Out-Null
$wr = Invoke-Oml @('review', $web1, $web2) | ConvertFrom-Json
$wd = @($wr.changes | Where-Object name -eq 'Login').details
Assert ($wd -contains 'widget input PwdInput: type Text -> Email' -and $wd -contains 'widget button Go: label "Go" -> "Sign in"') "Traditional Web Type and Label: $($wd -join ' | ')"
Write-Host 'ok   review: changed lines in --md, re-saved copy, whitespace only, secret value hidden, Traditional Web type and label'

# Service actions live in a Service module: a second, small pair.
$svcBlank = Join-Path $dir 'svc-blank.oml'; $svcBefore = Join-Path $dir 'SvcBefore.oml'; $svcAfter = Join-Path $dir 'SvcAfter.oml'
Invoke-Oml @('new', $svcBlank, '--name', 'ReviewSvc', '--kind', 'Service') | Out-Null
Set-Content (Join-Path $dir 'svc-before.json') '{ "serviceActions": [{ "name": "SyncOrders", "inputs": [{ "name": "Since", "type": "Date Time" }], "flow": [] }] }' -Encoding utf8
Set-Content (Join-Path $dir 'svc-after.json') '{ "edits": [{ "op": "addVariables", "action": "SyncOrders", "outputs": [{ "name": "Count", "type": "Integer" }] }] }' -Encoding utf8
Invoke-Oml @('apply', $svcBlank, (Join-Path $dir 'svc-before.json'), $svcBefore) | Out-Null
Invoke-Oml @('apply', $svcBefore, (Join-Path $dir 'svc-after.json'), $svcAfter) | Out-Null
$svc = Invoke-Oml @('review', $svcBefore, $svcAfter) | ConvertFrom-Json
Assert ($svc.changes.Count -eq 1 -and $svc.changes[0].kind -eq 'service action' -and $svc.changes[0].area -eq 'logic' -and $svc.changes[0].details -contains 'output Count (Integer) added') "Service action: $($svc.changes | ConvertTo-Json -Depth 5 -Compress)"

# The check rules that read Service Studio warnings or client variables are new findings too: render-assign,
# self-refreshing-aggregate and client-variable-secret (Reactive), escape-content-off (Traditional Web).
$loops = Join-Path $dir 'loops.json'; $after3 = Join-Path $dir 'After3.oml'
Set-Content $loops -Encoding utf8 @'
{ "clientVariables": [{ "name": "AuthToken" }],
  "screens": [
    { "name": "Counter", "access": { "roles": ["Manager"] }, "locals": [{ "name": "Renders", "type": "Integer" }],
      "actions": [{ "name": "CountRenders", "flow": [{ "kind": "assign", "name": "AddOne", "assign": { "Renders": "Renders + 1" } }] }],
      "onRender": "CountRenders", "widgets": [{ "type": "Expression", "value": "Renders" }] },
    { "name": "Reloads", "access": { "roles": ["Manager"] },
      "aggregates": [{ "name": "GetAll", "sources": ["Order"], "maxRecords": 10, "onAfterFetch": "Again" }],
      "actions": [{ "name": "Again", "flow": [{ "kind": "refreshData", "name": "Reload", "dataSource": "GetAll" }] }],
      "widgets": [{ "type": "Expression", "value": "GetAll.Count" }] } ] }
'@
$a3 = Invoke-Oml @('apply', $after, $loops, $after3) | ConvertFrom-Json
Assert ($a3.saved -and $a3.newErrors.Count -eq 0) "loops.json did not apply: $($a3 | ConvertTo-Json -Depth 8 -Compress)"
$lr = Invoke-Oml @('review', $after, $after3) | ConvertFrom-Json
$found = @($lr.newFindings | ForEach-Object { "$($_.rule) $($_.element)" })
foreach ($hit in @('render-assign MainFlow/Counter/CountRenders', 'self-refreshing-aggregate MainFlow/Reloads/GetAll', 'client-variable-secret AuthToken')) {
    Assert ($found -contains $hit) "review new findings miss '$hit': $($found -join ', ')"
}
$web3 = Join-Path $dir 'Web3.oml'
Set-Content (Join-Path $dir 'web3.json') '{ "screens": [{ "name": "Notice", "access": { "anonymous": false, "registered": true }, "inputs": [{ "name": "Message" }], "widgets": [{ "type": "Expression", "name": "RawMessage", "value": "Message", "escapeContent": false }] }] }' -Encoding utf8
Invoke-Oml @('apply', $web2, (Join-Path $dir 'web3.json'), $web3) | Out-Null
$er = Invoke-Oml @('review', $web2, $web3) | ConvertFrom-Json
Assert (@($er.newFindings | Where-Object { $_.rule -eq 'escape-content-off' -and $_.element -eq 'MainFlow/Notice/RawMessage' }).Count -eq 1) "review new findings miss escape-content-off: $(($er.newFindings | ForEach-Object { "$($_.rule) $($_.element)" }) -join ', ')"
Write-Host 'ok   review: render-assign, self-refreshing-aggregate, client-variable-secret and escape-content-off are new findings'

# Referenced blocks sit inside the producer's UI flow: review names them, not the flow.
$fwBlank = Join-Path $dir 'fw-blank.oml'; $fw = Join-Path $dir 'BDDFramework.oml'; $refBlank = Join-Path $dir 'ref-blank.oml'
$ref1 = Join-Path $dir 'Ref1.oml'; $ref2 = Join-Path $dir 'Ref2.oml'
Invoke-Oml @('new', $fwBlank, '--name', 'BDDFramework', '--kind', 'CrossDevice') | Out-Null
Invoke-Oml @('apply', $fwBlank, (Join-Path $PSScriptRoot 'bdd-standin.json'), $fw) | Out-Null
Invoke-Oml @('new', $refBlank, '--name', 'RefUser', '--kind', 'CrossDevice') | Out-Null
@{ references = @(@{ producer = $fw; elements = @('BDDScenario') }) } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $dir 'ref1.json') -Encoding utf8
@{ references = @(@{ producer = $fw; elements = @('BDDStep', 'FinalResult') }) } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $dir 'ref2.json') -Encoding utf8
Invoke-Oml @('apply', $refBlank, (Join-Path $dir 'ref1.json'), $ref1) | Out-Null
Invoke-Oml @('apply', $ref1, (Join-Path $dir 'ref2.json'), $ref2) | Out-Null
$rr = Invoke-Oml @('review', $refBlank, $ref1) | ConvertFrom-Json
$rc = @($rr.changes | Where-Object { $_.kind -eq 'reference' -and $_.name -eq 'BDDFramework' })
Assert ($rc.Count -eq 1 -and $rc[0].change -eq 'added' -and ($rc[0].details -join ' ') -match 'BDDScenario' -and ($rc[0].details -join ' ') -notmatch 'MainFlow') "Added reference: $($rr.changes | ConvertTo-Json -Depth 5 -Compress)"
$rr = Invoke-Oml @('review', $ref1, $ref2) | ConvertFrom-Json
$rc = @($rr.changes | Where-Object { $_.kind -eq 'reference' -and $_.name -eq 'BDDFramework' })
Assert ($rc.Count -eq 1 -and $rc[0].change -eq 'changed' -and ($rc[0].details -join ' ') -match 'block BDDStep' -and ($rc[0].details -join ' ') -match 'block FinalResult') "Referenced blocks added to a reference: $($rr.changes | ConvertTo-Json -Depth 5 -Compress)"
Write-Host 'ok   review: referenced blocks inside a producer UI flow are reported by name'

# Counts in the Markdown summary line are singular for one: "1 risk", "1 new check finding".
$one = Join-Path $dir 'one.json'; $after4 = Join-Path $dir 'After4.oml'; $md4 = Join-Path $dir 'one.md'
Set-Content $one '{ "serverActions": [{ "name": "ShareTotals", "public": true, "description": "Totals for other modules.", "flow": [] }] }' -Encoding utf8
Invoke-Oml @('apply', $before, $one, $after4) | Out-Null
$r4 = Invoke-Oml @('review', $before, $after4, '--md', $md4) | ConvertFrom-Json
$line = (Get-Content $md4 | Where-Object { $_ -like '`*`*1 added*' }) -join ''
$plural = { param($n, $noun) "$n $noun" + $(if ($n -eq 1) { '' } else { 's' }) }
Assert ($r4.summary.risks -eq 1 -and $line.Contains('1 risk, ') -and $line.Contains((& $plural $r4.summary.newFindings 'new check finding') + ' (')) "Plurals: $line"
Write-Host "ok   review: singular counts in the Markdown summary ($line)"

# --rules: a rule turned off in the team rules file is not a new finding.
$rules = Join-Path $dir 'oml-rules.json'
Invoke-Oml @('check', '--init-rules', '--out', $rules) | Out-Null
$team = Get-Content $rules -Raw | ConvertFrom-Json
($team.rules | Where-Object id -eq 'missing-description').enabled = $false
$team | ConvertTo-Json -Depth 10 | Set-Content $rules -Encoding utf8
$r2 = Invoke-Oml @('review', $before, $after, '--rules', $rules) | ConvertFrom-Json
Assert (-not ($r2.newFindings | Where-Object rule -eq 'missing-description') -and ($r2.newFindings | Where-Object rule -eq 'anonymous-screen-server')) '--rules turns a rule off'

# The same file twice: nothing changed. In oml run, review is a step.
$same = Invoke-Oml @('review', $after, $after) | ConvertFrom-Json
Assert ($same.changes.Count -eq 0 -and $same.risks.Count -eq 0 -and $same.newFindings.Count -eq 0 -and $same.summary.changed -eq 0) "Same file: $($same | ConvertTo-Json -Depth 5 -Compress)"
$steps = Join-Path $dir 'steps.json'
@{ steps = @(, @('review', $before, $after)) } | ConvertTo-Json -Depth 5 | Set-Content $steps -Encoding utf8
$run = Invoke-Oml @('run', $steps) | ConvertFrom-Json
Assert ($run.results[0].exitCode -eq 0 -and $run.results[0].output.summary.added -eq 4) "review in oml run: $($run | ConvertTo-Json -Depth 8 -Compress)"

# Input errors: exit 2 and nothing written.
$other = Join-Path $dir 'Other.oml'
Invoke-Oml @('new', $other, '--name', 'Other', '--kind', 'CrossDevice') | Out-Null
$e = Invoke-Oml @('review', $before, $other) 2 | ConvertFrom-Json
Assert ($e.error -like '*different modules (ReviewApp and Other)*') "Different modules: $($e.error)"
# --md may replace an earlier .md file and says so; a staged .oml and the other outputs are still refused.
Set-Content $md '# Review: old review'
$o = Invoke-Oml @('review', $before, $after, '--md', $md) | ConvertFrom-Json
Assert ($o.overwritten -like "*$(Split-Path $md -Leaf)" -and (Get-Content $md -Raw) -notmatch 'old review') "Existing .md was not overwritten: $($o | ConvertTo-Json -Compress)"
$o = Invoke-Oml @('review', $before, $after, '--md', (Join-Path $dir 'fresh-overwrite.md')) | ConvertFrom-Json
Assert ($null -eq $o.overwritten) 'overwritten reported for a new file'
Set-Content (Join-Path $dir 'notes.md') '# My notes'
$e = Invoke-Oml @('review', $before, $after, '--md', (Join-Path $dir 'notes.md')) 2 | ConvertFrom-Json
Assert ($e.error -like 'Output already exists*' -and (Get-Content (Join-Path $dir 'notes.md') -Raw) -match 'My notes') "A non-review .md was overwritten: $($e.error)"
$e = Invoke-Oml @('review', $before, $after, '--md', $after) 2 | ConvertFrom-Json
Assert ($e.error -like 'Output already exists*' -and (Get-FileHash $after).Hash -eq $afterHash) "Staged .oml as --md: $($e.error)"
$e = Invoke-Oml @('review', $before, $after, '--notes', $md) 2 | ConvertFrom-Json
Assert ($e.error -like 'Output already exists*') "Existing --notes: $($e.error)"
$e = Invoke-Oml @('review', $before, $after, '--md', (Join-Path $dir 'same.md'), '--notes', (Join-Path $dir 'same.md')) 2 | ConvertFrom-Json
Assert ($e.error -like '*different paths*' -and -not (Test-Path (Join-Path $dir 'same.md'))) "Same output paths: $($e.error)"
$e = Invoke-Oml @('review', $before) 2 | ConvertFrom-Json
Assert ($e.error -like '*two module files*') "One file: $($e.error)"
$e = Invoke-Oml @('review', $before, (Join-Path $dir 'missing.oml')) 2 | ConvertFrom-Json
Assert ($e.error -like 'File not found*') "Missing file: $($e.error)"
$e = Invoke-Oml @('review', $before, $after, '--fail-on', 'high') 2 | ConvertFrom-Json
Assert ($e.error -like 'review accepts*') "Unknown option: $($e.error)"
$help = Invoke-Oml @('review', '--help')
Assert ($help -match '^oml review ') "review --help: $help"
Write-Host 'ok   review: --rules, same file, oml run step, input errors'
