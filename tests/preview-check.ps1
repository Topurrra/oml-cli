# oml preview, end to end: build a module from tests/preview.json, render blocks and a screen, check the report and the HTML.
# With Edge or Chrome installed it also takes screenshots. The page records script errors on <html data-oml-errors="N"> and in
# window.__omlPreviewErrors; headless Edge/Chrome with --dump-dom prints the DOM after the scripts ran, so this check reads
# data-oml-errors and the attribute the fixture's JavaScript set on its root (proof that $parameters.RootId was right).
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$Tmp = [IO.Path]::GetFullPath($Tmp)
function Require($condition, $message) { if (!$condition) { throw $message } }
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
                    '.pv-card {', '.pv-inner {', 'function($parameters, $actions, $roles, $public){', 'p:{"RootId":"PvRoot","Label":"Arg title"}', 'window.__omlPreviewErrors') {
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
    Require ($shot.Json.javaScriptErrors.count -eq 1 -and $shot.Json.javaScriptErrors.messages[0] -match 'missingFunction') 'The JavaScript error was not reported'
    $okPng = Join-Path $Tmp "pv-$id-card.png"
    $good = Run-Oml @('preview', $module, 'PvCard', '--out', (Join-Path $Tmp "pv-$id-card2.html"), '--screenshot', $okPng)
    Require ($good.Code -eq 0 -and (Test-Path $okPng) -and $good.Json.javaScriptErrors.count -eq 0 -and $good.Json.horizontalOverflow -eq $false) 'Clean block: screenshot or script errors'
    $profile = Join-Path $Tmp "pv-$id-profile"
    $dom = & $browser --headless=new --disable-gpu --no-first-run "--user-data-dir=$profile" --virtual-time-budget=1000 --dump-dom ([uri]$good.Json.html).AbsoluteUri 2>$null | Out-String
    Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue
    Require ($dom -match 'id="PvRoot"[^>]*data-pv="Hello preview"' -and $dom -match 'data-oml-errors="0"') 'JavaScript did not run with the right root id'
    $note = 'screenshots and JavaScript run checked'
} else {
    $note = 'no browser found, screenshot step skipped'
}
Write-Host "ok   preview: report, HTML, nested blocks, CSS, JavaScript call, checklist, overwrite refused, $note"
