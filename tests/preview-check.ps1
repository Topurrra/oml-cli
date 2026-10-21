# oml preview, end to end: build a module from tests/preview.json, render blocks and a screen, check the report and the HTML.
# With Edge or Chrome installed it also takes screenshots. The page records script errors on <html data-oml-errors="N"> and in
# window.__omlPreviewErrors; headless Edge/Chrome with --dump-dom prints the DOM after the scripts ran, so this check reads
# data-oml-errors and the attribute the fixture's JavaScript set on its root (proof that $parameters.RootId was right).
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$Tmp = [IO.Path]::GetFullPath($Tmp)
function Require($condition, $message) { if (!$condition) { throw $message } }
# A raw request, so the Host header and the method are exactly what the test says (copied from dashboard-check).
function Raw([int]$Port, [string]$Path, [hashtable]$Headers, [string]$Method = 'GET') {
    $client = [Net.Sockets.TcpClient]::new('127.0.0.1', $Port)
    try {
        $client.ReceiveTimeout = 10000
        $stream = $client.GetStream()
        $lines = @("$Method $Path HTTP/1.1") + @($Headers.GetEnumerator() | ForEach-Object { "$($_.Key): $($_.Value)" }) + @('Content-Length: 0', 'Connection: close', '', '')
        $bytes = [Text.Encoding]::ASCII.GetBytes($lines -join "`r`n")
        $stream.Write($bytes, 0, $bytes.Length)
        $status = [IO.StreamReader]::new($stream).ReadLine()
        return [int]($status -split ' ')[1]
    } finally { $client.Close() }
}
function Run-Oml([string[]]$cliArgs) {
    $text = & $Oml @cliArgs
    return [pscustomobject]@{ Code = $LASTEXITCODE; Json = (($text -join "`n") | ConvertFrom-Json) }
}
$id = [guid]::NewGuid().ToString('N').Substring(0, 8)
$blank = Join-Path $Tmp "pv-$id-blank.oml"
$module = Join-Path $Tmp "pv-$id.oml"
& $Oml new $blank --name PvCheck --kind CrossDevice | Out-Null
$applied = & $Oml apply $blank (Join-Path $PSScriptRoot 'preview.json') $module | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $applied.saved) 'Preview fixture did not apply'
$hash = (Get-FileHash $module).Hash

# A block with defaults, a nested block, a stylesheet and an OnRender JavaScript node.
$out = Join-Path $Tmp "pv-$id-card.html"
$r = Run-Oml @('preview', $module, 'PvCard', '--out', $out, '--theme', 'dark', '--width', '390', '--arg', 'Title=Arg title')
Require ($r.Code -eq 0 -and $r.Json.element -eq 'PvCard' -and $r.Json.kind -eq 'Block' -and $r.Json.html -eq $out) 'Block preview failed'
Require ($r.Json.widgets -ge 7 -and $r.Json.blocksRendered -contains 'PvInner' -and $r.Json.javaScriptNodes -eq 1 -and $r.Json.notes.Count -ge 2) 'Report fields missing'
Require (@($r.Json.checklist).Count -eq 6 -and @($r.Json.checklist | Where-Object { !$_.ok }).Count -eq 0) 'Clean block failed the checklist'
$html = [IO.File]::ReadAllText($out)
foreach ($needle in 'data-theme="dark"', 'width:390px', 'id="PvRoot"', 'class="pv-card"', 'id="InnerRoot"', 'Arg title', 'Nested heading', 'Slot text',
                    '.pv-card {', '.pv-inner {', 'function($parameters, $actions, $roles, $public, $resolve, $reject){', 'p:{"RootId":"PvRoot","Label":"Arg title"}', 'window.__omlPreviewErrors',
                    "connect-src 'none'") {
    Require ($html.Contains($needle)) "HTML is missing: $needle"
}
$again = Run-Oml @('preview', $module, 'PvCard', '--out', $out)
Require ($again.Code -ne 0 -and $again.Json.error -match 'already exists.*Use a new output path, or delete that file first') 'Overwrite was not refused'

# A screen: nested blocks, If (true branch), TableRecords (header row plus the row template 3 times), a widget kind that is not supported.
$screen = Run-Oml @('preview', $module, 'PvScreen', '--out', (Join-Path $Tmp "pv-$id-screen.html"))
Require ($screen.Code -eq 0 -and $screen.Json.kind -eq 'Screen' -and $screen.Json.blocksRendered.Count -eq 2) 'Screen preview failed'
$screenHtml = [IO.File]::ReadAllText($screen.Json.html)
Require (([regex]::Matches($screenHtml, '<tr>')).Count -eq 4 -and $screenHtml.Contains('True branch text') -and !$screenHtml.Contains('False branch text')) 'Table rows or If branch wrong'
Require ($screenHtml.Contains('>abc<')) 'Screen input default not rendered'

# A block built to trip every automatic check, including the "linkCopy spec" bug (a seeded Link text kept next to the added text).
$badOut = Join-Path $Tmp "pv-$id-bad.html"
$bad = Run-Oml @('preview', $module, 'PvBad', '--out', $badOut)
Require ($bad.Code -eq 0 -and @($bad.Json.checklist | Where-Object { !$_.ok }).Count -eq 6) 'Bad block must fail all 6 checks and still exit 0'
Require (($bad.Json.checklist | Where-Object { $_.id -eq 'a' }).detail -match 'linkCopy spec') 'Check a did not name the default text'
Require (($bad.Json.unsupported | Where-Object { $_.kind -eq 'Popup' }).count -eq 1) 'Unsupported widget kind not reported'

$unknown = Run-Oml @('preview', $module, 'Nope', '--out', (Join-Path $Tmp "pv-$id-none.html"))
Require ($unknown.Code -eq 2 -and $unknown.Json.error -match 'PvCard') 'Unknown element must list the blocks'
Require ((Get-FileHash $module).Hash -eq $hash) 'preview changed the module'

# Screenshots, when a browser exists. No browser is not a failure: the report says why.
$candidates = @($env:OML_BROWSER) + @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe")
$browser = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if ($browser) {
    $png = Join-Path $Tmp "pv-$id-bad.png"
    $shot = Run-Oml @('preview', $module, 'PvBad', '--out', (Join-Path $Tmp "pv-$id-bad2.html"), '--screenshot', $png, '--width', '390')
    Require ($shot.Code -eq 0 -and $shot.Json.screenshot -eq $png -and (Test-Path $png)) "Screenshot failed: $($shot.Json.screenshotNote)"
    $bytes = [IO.File]::ReadAllBytes($png)
    Require ($bytes.Length -gt 1000 -and $bytes[0] -eq 0x89 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47) 'Screenshot is not a PNG'
    Require (@($shot.Json.scriptErrors).Count -eq 1 -and $shot.Json.scriptErrors[0].node -eq 'PvBad.OnRender.Draw' -and $shot.Json.scriptErrors[0].message -match 'missingFunction') 'The JavaScript error was not reported with its node'
    $okPng = Join-Path $Tmp "pv-$id-card.png"
    $good = Run-Oml @('preview', $module, 'PvCard', '--out', (Join-Path $Tmp "pv-$id-card2.html"), '--screenshot', $okPng)
    Require ($good.Code -eq 0 -and (Test-Path $okPng) -and -not $good.Json.scriptErrors -and $good.Json.horizontalOverflow -eq $false) 'Clean block: screenshot or script errors'
    $profile = Join-Path $Tmp "pv-$id-profile"
    $dom = & $browser --headless=new --disable-gpu --no-first-run "--user-data-dir=$profile" --virtual-time-budget=1000 --dump-dom ([uri]$good.Json.html).AbsoluteUri 2>$null | Out-String
    Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue
    Require ($dom -match 'id="PvRoot"[^>]*data-pv="Hello preview"' -and $dom -match 'data-oml-errors="0"') 'JavaScript did not run with the right root id'
    $note = 'screenshots and JavaScript run checked'
} else {
    $note = 'no browser found, screenshot step skipped'
}

# Option errors: each is exit 2 with a message, before the module is read.
foreach ($bad in @(@('--wait', 'soon'), @('--wait', '60001'), @('--type', 'NoEquals'), @('--click', ' '), @('--no-open'), @('--serve', '--out', 'x.html'))) {
    $r = Run-Oml (@('preview', $module, 'PvCard') + $bad)
    Require ($r.Code -eq 2 -and $r.Json.error) "preview $($bad -join ' ') must exit 2, got $($r.Code)"
}

# A JavaScript-driven block (tests/preview-js): Script elements (a small library and chart code that uses it), OnInitialize,
# OnReady (one synchronous and one $resolve node) and OnRender JavaScript nodes, stubs for $public, $actions and controller.
# The spec names its script files relative to its folder, so apply runs there.
$jsModule = Join-Path $Tmp "pv-$id-js.oml"
Push-Location (Join-Path $PSScriptRoot 'preview-js')
try { $applied = & $Oml apply $blank 'spec.json' $jsModule | ConvertFrom-Json } finally { Pop-Location }
Require ($LASTEXITCODE -eq 0 -and $applied.saved) "JavaScript fixture did not apply: $($applied | ConvertTo-Json -Compress -Depth 5)"
$plain = Run-Oml @('preview', $jsModule, 'PvChart', '--out', (Join-Path $Tmp "pv-$id-chart0.html"))
Require ($plain.Code -eq 0 -and ($plain.Json.scripts -join ',') -eq 'PvMiniLib,PvChartCode' -and $plain.Json.javaScriptNodes -eq 4 -and $null -eq $plain.Json.stubbed) 'Scripts not listed, or the browser ran without a screenshot or steps'
Require (@($plain.Json.notes | Where-Object { $_ -match 'not loaded: PvBrokenLib, PvUnused' }).Count -eq 1) 'Scripts that the block does not require must be named in notes'
$chartHtml = [IO.File]::ReadAllText($plain.Json.html)
Require ($chartHtml.IndexOf('data-oml-script="PvMiniLib"') -lt $chartHtml.IndexOf('data-oml-script="PvChartCode"') -and $chartHtml.IndexOf('data-oml-script="PvChartCode"') -lt $chartHtml.IndexOf('id="oml-frame"')) 'Scripts must load in order, before the widgets'
# A block rendered twice: each node label names its instance, and each instance follows the If branch its own input selects.
$twice = Run-Oml @('preview', $jsModule, 'PvTwice', '--out', (Join-Path $Tmp "pv-$id-twice0.html"))
$twiceHtml = [IO.File]::ReadAllText($twice.Json.html)
Require ($twice.Code -eq 0 -and $twiceHtml.Contains('n:"PvFlow[1].OnReady.BarJs"') -and $twiceHtml.Contains('n:"PvFlow[2].OnReady.LineJs"') -and -not $twiceHtml.Contains('n:"PvFlow[1].OnReady.LineJs"') -and -not $twiceHtml.Contains('n:"PvFlow[2].OnReady.BarJs"')) 'Instance labels or If branches per instance'
if ($browser) {
    # A node that does not compile, in a block shown twice: one error, under the node's own name.
    $syntax = Run-Oml @('preview', $jsModule, 'PvTwice', '--out', (Join-Path $Tmp "pv-$id-twice1.html"), '--eval', '1')
    Require (@($syntax.Json.scriptErrors).Count -eq 1 -and $syntax.Json.scriptErrors[0].node -eq 'PvSyntax.OnReady.Broken' -and $syntax.Json.scriptErrors[0].message -match '^SyntaxError') "Syntax error per instance: $($syntax.Json.scriptErrors | ConvertTo-Json -Compress)"
}
Require (@($twice.Json.notes | Where-Object { $_ -eq 'If Long in PvFlow.OnReady cannot be evaluated here (Length(Mode) > 2), so the JavaScript nodes of both branches run.' }).Count -eq 1 -and @($twice.Json.notes | Where-Object { $_ -match '^Switch Pick in PvFlow.OnReady' }).Count -eq 1) 'If and Switch notes'

if ($browser) {
    # Steps run in order after the lifecycle nodes; the eval step reads the text that the clicks and the typing produced.
    $chartPng = Join-Path $Tmp "pv-$id-chart.png"
    $eval = "document.getElementById('Status').textContent + '|' + document.getElementById('Greeting').textContent + '|' + document.getElementById('ChartArea').getAttribute('data-order')"
    $chart = Run-Oml @('preview', $jsModule, 'PvChart', '--out', (Join-Path $Tmp "pv-$id-chart.html"), '--screenshot', $chartPng, '--width', '390',
        '--click', '#ToggleButton', '--click', '#ToggleButton', '--type', '#NameInput=Ann', '--wait', '50', '--eval', $eval, '--click', '#Missing')
    Require ($chart.Code -eq 0 -and (Test-Path $chartPng) -and -not $chart.Json.scriptErrors) "Chart block: screenshot or script errors: $($chart.Json.scriptErrors | ConvertTo-Json -Compress)"
    $steps = @($chart.Json.steps)
    Require ($steps.Count -eq 6 -and $steps[0].step -eq 'click #ToggleButton' -and $steps[2].step -eq 'type #NameInput' -and $steps[3].step -eq 'wait 50') "Steps: $($steps | ConvertTo-Json -Compress)"
    # init, draw, wire, render: OnReady waited for the $resolve of Wire before OnRender ran.
    Require ($steps[4].ok -and $steps[4].result -eq 'Clicked 2 time(s)|Hello Ann|init,draw,wire,render') "Clicks, typing or flow order did not change the page: $($steps[4] | ConvertTo-Json -Compress)"
    Require (-not $steps[5].ok -and $steps[5].error -eq 'no element matches #Missing') 'A step with a missing element must fail with its reason'
    foreach ($call in 'PvChart.OnReady.Draw: $public.FeedbackMessage.showFeedbackMessage("Sales: 4 bars", 1)', 'PvChart.OnReady.Draw: $actions.ChartDrawn(4)', 'PvChart.OnReady.Draw: controller.trackEvent("chart-ready")') {
        Require (@($chart.Json.stubbed) -contains $call) "Stub call not reported: $call"
    }
    Require (@($chart.Json.console | Where-Object { $_.level -eq 'log' -and $_.text -eq 'chart ready 4' }).Count -eq 1) 'console.log not reported'
    # The HTML file holds the steps too: a plain DOM dump of it shows the bars the library drew and the clicked text.
    $profile = Join-Path $Tmp "pv-$id-profile2"
    $dom = & $browser --headless=new --disable-gpu --no-first-run "--user-data-dir=$profile" --virtual-time-budget=2000 --dump-dom ([uri]$chart.Json.html).AbsoluteUri 2>$null | Out-String
    Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue
    Require ($dom -match 'id="ChartArea"[^>]*data-bars="4"' -and ([regex]::Matches($dom, 'class="pv-bar"')).Count -eq 4 -and $dom -match 'id="Status"[^>]*>Clicked 2 time\(s\)<' -and $dom -match 'data-oml-done="true"') 'The DOM does not show what the scripts drew'

    # Errors carry the node or script name; a failed node stops the rest of its action; remote requests are blocked.
    $broken = Run-Oml @('preview', $jsModule, 'PvBroken', '--out', (Join-Path $Tmp "pv-$id-broken.html"), '--eval', 'throw new Error("eval failed")')
    $errors = @($broken.Json.scriptErrors)
    Require ($broken.Code -eq 0 -and $errors.Count -eq 3) "PvBroken errors: $($errors | ConvertTo-Json -Compress)"
    Require ($errors[0].node -eq 'script PvBrokenLib' -and $errors[0].message -match '^SyntaxError') 'Script syntax error not named'
    Require ($errors[1].node -eq 'PvBroken.OnReady.Throw' -and $errors[1].message -match 'undefinedHelper is not defined') 'Node error not named'
    Require ($errors[2].node -eq 'PvBroken.OnRender.Reject' -and $errors[2].message -eq '$reject: Error: could not load data') '$reject not reported'
    Require ((@($broken.Json.skipped) -join ',') -eq 'PvBroken.OnReady.Never') 'The node after a failed node must be skipped'
    Require (((@($broken.Json.blocked) | Sort-Object) -join ',') -eq 'https://example.com/data.json,https://example.com/pixel.png') "Remote requests not blocked: $($broken.Json.blocked -join ',')"
    Require (@($broken.Json.stubbed) -contains 'PvBroken.OnInitialize.Net: $actions.Loaded("Broken")' -and @($broken.Json.console | Where-Object { $_.level -eq 'warn' -and $_.text -eq 'label is Broken' }).Count -eq 1) 'Stub or console of PvBroken'
    Require (-not $broken.Json.steps[0].ok -and $broken.Json.steps[0].error -eq 'Error: eval failed') 'A failing eval step must report its error'
    Require ($broken.Json.scriptsRan -eq $true) 'scriptsRan must be true when the page reported'

    # Flow branches: an If the preview can evaluate follows one branch (by --arg); others, and a Switch, run every branch.
    # The console text is not ASCII: the browser output is read as UTF-8.
    $flow = Run-Oml @('preview', $jsModule, 'PvFlow', '--out', (Join-Path $Tmp "pv-$id-flow.html"), '--eval', "window.pvFlow.slice().sort().join()")
    Require ($flow.Json.steps[0].result -eq 'bar,case,no,other,yes') "If branch: $($flow.Json.steps[0] | ConvertTo-Json -Compress)"
    $line = Run-Oml @('preview', $jsModule, 'PvFlow', '--out', (Join-Path $Tmp "pv-$id-flow2.html"), '--arg', 'Mode="line"', '--eval', "window.pvFlow.slice().sort().join()")
    Require ($line.Json.steps[0].result -eq 'case,line,no,other,yes') "If branch with --arg: $($line.Json.steps[0] | ConvertTo-Json -Compress)"
    # oml's own stdout encoding is not part of preview: compare exactly only when a non-ASCII path survives a round trip through it.
    $probe = Join-Path $Tmp "pv-$id-$([char]0xE7)$([char]0xE3).html"
    [IO.File]::WriteAllText($probe, 'x')
    $echo = Run-Oml @('preview', $jsModule, 'PvFlow', '--out', $probe)
    if ($echo.Json.error -and $echo.Json.error.Contains($probe)) {
        Require (@($flow.Json.console).Count -eq 1 -and $flow.Json.console[0].text -like "Ol$([char]0xE1) * A$([char]0xE7)$([char]0xE3)o") "Non-ASCII console text: $($flow.Json.console | ConvertTo-Json -Compress)"
    } else {
        Require (@($flow.Json.console).Count -eq 1 -and $flow.Json.console[0].text -like 'Ol* A*o') "Console text: $($flow.Json.console | ConvertTo-Json -Compress)"
        Write-Host "note preview: oml stdout did not keep a non-ASCII path in this console, so the non-ASCII console text was not compared exactly"
    }

    # Navigation and window.open: cancelled and reported; with the browser's proxy and resolver rules nothing reaches a local listener.
    $logger = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $logger.Start()
    try {
        $target = "http://127.0.0.1:$($logger.LocalEndpoint.Port)/"
        $nav = Run-Oml @('preview', $jsModule, 'PvNav', '--out', (Join-Path $Tmp "pv-$id-nav.html"), '--screenshot', (Join-Path $Tmp "pv-$id-nav.png"), '--arg', "Target=`"$target`"",
            '--eval', "var l=document.createElement('link');l.rel='prefetch';l.href='${target}prefetch';document.head.appendChild(l);document.getElementById('NavRoot').textContent", '--wait', '300')
        Start-Sleep -Milliseconds 500
        Require (-not $logger.Pending()) 'A request reached the local listener: the preview is not offline'
    } finally { $logger.Stop() }
    Require ($nav.Code -eq 0 -and $nav.Json.steps[0].result -eq 'Stays here') 'The page must stay after a navigation'
    foreach ($b in "${target}window (window.open)", "${target}anchor (navigation)", "${target}navigate (navigation)") { Require (@($nav.Json.blocked) -contains $b) "Not reported as blocked: $b" }
    Require (@($nav.Json.notes | Where-Object { $_ -like "The page tried to leave or open a window: ${target}window, ${target}anchor, ${target}navigate.*" }).Count -eq 1) 'Navigation note'

    # Forms (requestSubmit, a submit button as Enter does, form.submit() to another origin, target=_blank), a link with
    # target=_blank, speculation rules and a reload by page code: all stopped, nothing reaches the listener, the page stays.
    $logger = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $logger.Start()
    try {
        $t = "http://127.0.0.1:$($logger.LocalEndpoint.Port)/"
        $vectors = @(
            "var f=document.createElement('form');f.action='${t}rs';document.body.appendChild(f);f.requestSubmit();",
            "var f=document.createElement('form');f.action='${t}enter';var i=document.createElement('input');f.appendChild(i);var b=document.createElement('button');f.appendChild(b);document.body.appendChild(f);b.click();",
            "var f=document.createElement('form');f.action='${t}submit';f.method='post';document.body.appendChild(f);f.submit();",
            "var f=document.createElement('form');f.action='${t}blankform';f.target='_blank';document.body.appendChild(f);f.requestSubmit();",
            "var a=document.createElement('a');a.href='${t}blanklink';a.target='_blank';document.body.appendChild(a);a.click();",
            "var s=document.createElement('script');s.type='speculationrules';s.textContent=JSON.stringify({prerender:[{source:'list',urls:['${t}spec']}]});document.head.appendChild(s);",
            "location.reload();document.getElementById('NavRoot').textContent")
        $args2 = @('preview', $jsModule, 'PvNav', '--out', (Join-Path $Tmp "pv-$id-forms.html")) + @($vectors | ForEach-Object { '--eval', $_ }) + @('--wait', '300')
        $forms = Run-Oml $args2
        Start-Sleep -Milliseconds 500
        Require (-not $logger.Pending()) 'A form, link or speculation rule reached the local listener'
    } finally { $logger.Stop() }
    Require ($forms.Code -eq 0 -and $forms.Json.steps[6].result -eq 'Stays here' -and @($forms.Json.steps | Where-Object { -not $_.ok }).Count -eq 0) "Form steps: $($forms.Json.steps | ConvertTo-Json -Compress)"
    $html = ([uri]$forms.Json.html).AbsoluteUri
    foreach ($b in "${t}rs (form)", "${t}enter (form)", "${t}submit (form.submit())", "${t}blankform (form target=_blank)", "${t}blanklink (link target=_blank)", 'speculation rules (script)', "$html (navigation)") {
        Require (@($forms.Json.blocked) -contains $b) "Not reported as blocked: $b; got $($forms.Json.blocked -join ' | ')"
    }

    # Errors: at most 20 in the JSON, each at most 160 characters, repeats dropped, errorsTotal counts every one.
    $many = Run-Oml @('preview', $jsModule, 'PvSlow', '--out', (Join-Path $Tmp "pv-$id-errors.html"),
        '--eval', "for(let i=0;i<30;i++)setTimeout(function(){throw new Error('distinct '+i+' '+'x'.repeat(200))});for(let k=0;k<5;k++)setTimeout(function(){throw new Error('same')});1", '--wait', '100')
    $errs = @($many.Json.scriptErrors)
    Require ($errs.Count -eq 20 -and $many.Json.errorsTotal -eq 37 -and ($errs.message | Measure-Object Length -Maximum).Maximum -le 160) "Error caps: $($errs.Count) shown, total $($many.Json.errorsTotal)"

    # Two async nodes that never resolve: each gets its own 2 s, then the steps still run. An eval promise that never settles times out.
    $slow = Run-Oml @('preview', $jsModule, 'PvSlow', '--out', (Join-Path $Tmp "pv-$id-slow.html"), '--eval', "'done'", '--eval', 'new Promise(function(){})')
    Require (@($slow.Json.scriptErrors).Count -eq 2 -and $slow.Json.steps[0].result -eq 'done' -and $slow.Json.steps[1].error -eq 'the promise did not settle within 2000 ms') "Async budget: $($slow.Json | ConvertTo-Json -Compress -Depth 4)"

    # Report size: at most 20 stub calls and console messages of at most 160 characters, with totals; blocked file: and data: requests are named.
    $big = Run-Oml @('preview', $jsModule, 'PvNav', '--out', (Join-Path $Tmp "pv-$id-big.html"),
        '--eval', "for(var i=0;i<25;i++)controller.ping(i);for(var j=0;j<30;j++)console.log('line '+j+' '+'x'.repeat(200));try{var x=new XMLHttpRequest();x.open('GET','file:///C:/Windows/win.ini');x.send()}catch(e){}try{new Worker('data:text/javascript,1')}catch(e){}1")
    Require (@($big.Json.stubbed).Count -eq 20 -and $big.Json.stubbedTotal -eq 25 -and @($big.Json.console).Count -eq 20 -and $big.Json.consoleTotal -eq 30 -and
        ($big.Json.console.text | Measure-Object Length -Maximum).Maximum -le 160) "Report caps: stubbed $(@($big.Json.stubbed).Count)/$($big.Json.stubbedTotal), console $(@($big.Json.console).Count)/$($big.Json.consoleTotal)"
    Require (@($big.Json.blocked) -contains 'file:// (XHR or fetch)' -and @($big.Json.blocked) -contains 'data: (Worker)') "Readable blocked entries: $($big.Json.blocked -join ', ')"

    # A node that loops forever: the browser is stopped after the time budget plus a margin, and the report says the scripts did not report.
    $loop = Run-Oml @('preview', $jsModule, 'PvLoop', '--out', (Join-Path $Tmp "pv-$id-loop.html"), '--eval', '1')
    Require ($loop.Code -eq 0 -and $loop.Json.scriptsRan -eq $false -and @($loop.Json.notes | Where-Object { $_ -match 'a script or JavaScript node may loop forever' }).Count -eq 1) "Loop: $($loop.Json | ConvertTo-Json -Compress -Depth 4)"

    # --serve: 127.0.0.1 on a random port, only "/" and "/v", GET only, Host checked; the page runs the scripts; a changed file renders again.
    $serveOut = Join-Path $Tmp "pv-$id-serve.out"
    # Step 1 reloads through the server's own live reload once; step 2 is a reload by page code, which must be cancelled.
    $process = Start-Process -FilePath $Oml -ArgumentList 'preview', "`"$jsModule`"", 'PvChart', '--serve', '--no-open',
        '--eval', "sessionStorage.n=(+sessionStorage.n||0)+1;if(sessionStorage.n<2)__omlLiveReload();'load'+sessionStorage.n", '--eval', "location.reload();'stayed'" -RedirectStandardOutput $serveOut -RedirectStandardError "$serveOut.err" -PassThru -WindowStyle Hidden
    try {
        $first = $null
        for ($i = 0; $i -lt 150 -and -not $first; $i++) {
            Start-Sleep -Milliseconds 200
            if (Test-Path $serveOut) { $line = Get-Content $serveOut -TotalCount 1 -ErrorAction SilentlyContinue; if ($line) { $first = $line | ConvertFrom-Json } }
            Require (-not $process.HasExited) "oml preview --serve ended early: $(Get-Content "$serveOut.err" -Raw -ErrorAction SilentlyContinue)"
        }
        Require $first 'oml preview --serve printed no address'
        $url = [uri]$first.url
        Require ($url.Host -eq '127.0.0.1' -and $url.AbsolutePath -eq '/' -and $url.Query -eq '') "Unexpected address $($first.url)"
        $listen = @(Get-NetTCPConnection -LocalPort $url.Port -State Listen -ErrorAction SilentlyContinue)
        Require ($listen.Count -ge 1 -and @($listen | Where-Object { $_.LocalAddress -ne '127.0.0.1' }).Count -eq 0) "Listens on: $($listen.LocalAddress -join ', ')"
        $page = Invoke-WebRequest $first.url
        Require ($page.StatusCode -eq 200 -and $page.Content.Contains('data-oml-script="PvMiniLib"') -and $page.Headers['Content-Security-Policy'] -match "connect-src 'self'") 'Served page'
        $version = (Invoke-WebRequest ($first.url + 'v')).Content
        Require ((Raw $url.Port '/other' @{ Host = "127.0.0.1:$($url.Port)" }) -eq 404) 'Only / and /v are served'
        Require ((Raw $url.Port '/' @{ Host = "127.0.0.1:$($url.Port)" } 'POST') -eq 405) 'Only GET'
        Require ((Raw $url.Port '/' @{ Host = "attacker.example:$($url.Port)" }) -eq 403) 'A foreign Host header must be refused'
        # HEAD gets 405 with no body, and the server keeps serving afterwards.
        Require ((Raw $url.Port '/' @{ Host = "127.0.0.1:$($url.Port)" } 'HEAD') -eq 405) 'HEAD must be 405'
        Require ((Raw $url.Port '/v' @{ Host = "127.0.0.1:$($url.Port)" } 'HEAD') -eq 405 -and -not $process.HasExited) 'The server stopped after a HEAD request'
        $again = Invoke-WebRequest $first.url
        Require ($again.StatusCode -eq 200 -and $again.Headers['X-Frame-Options'] -eq 'DENY' -and $again.Headers['Content-Security-Policy'] -match "frame-ancestors 'none'" -and $again.Headers['Content-Type'] -eq 'text/html; charset=utf-8') 'Served page headers'
        $profile = Join-Path $Tmp "pv-$id-profile3"
        $dom = & $browser --headless=new --disable-gpu --no-first-run "--user-data-dir=$profile" --virtual-time-budget=5000 --dump-dom $first.url 2>$null | Out-String
        Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue
        Require ($dom -match 'id="ChartArea"[^>]*data-bars="4"' -and $dom -match 'data-oml-errors="0"') 'The served page did not run its scripts'
        $served = [regex]::Match($dom, '<pre id="oml-preview-report"[^>]*>(.*?)</pre>', 'Singleline').Groups[1].Value | ForEach-Object { [Net.WebUtility]::HtmlDecode($_) } | ConvertFrom-Json
        Require ($served.steps[0].result -eq 'load2' -and $served.steps[1].result -eq 'stayed' -and @($served.blocked | Where-Object { $_ -eq "$($first.url) (navigation)" }).Count -eq 1) "Live reload must load the page twice, and a page-code reload must be cancelled: $($served.steps | ConvertTo-Json -Compress)"
        (Get-Item $jsModule).LastWriteTimeUtc = [DateTime]::UtcNow.AddSeconds(5)
        Require ((Invoke-WebRequest ($first.url + 'v')).Content -ne $version) 'The version did not change with the file'
        Require ((Invoke-WebRequest $first.url).StatusCode -eq 200) 'Render after a change failed'
        Require ((Get-Content $serveOut -Raw) -match 'Rendered again') 'The server did not render again after the change'
    } finally {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    $note += '; JavaScript-driven block, steps and --serve checked'
}
Require ((Get-FileHash $module).Hash -eq $hash) 'preview changed the module'
Write-Host "ok   preview: report, HTML, nested blocks, CSS, JavaScript call, checklist, overwrite refused, scripts, $note"
