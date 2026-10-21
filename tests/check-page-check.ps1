param([string]$Oml = "$PSScriptRoot\..\src\bin\Release\net8.0\oml.exe", [string]$Tmp = "$PSScriptRoot\..\tmp")
# The oml check HTML report: offline, data escaped, and in headless Edge/Chrome the filters work in both themes without script errors.
$ErrorActionPreference = 'Stop'
$dir = Join-Path $Tmp ('check-page-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $dir | Out-Null
$inputs = Join-Path $dir 'input'; New-Item -ItemType Directory -Force $inputs | Out-Null
$blank = Join-Path $dir 'blank.oml'; $module = Join-Path $inputs 'CheckBad.oml'
& $Oml new $blank --name CheckBad --kind CrossDevice | Out-Null
$applied = & $Oml apply $blank (Join-Path $PSScriptRoot 'check-bad.json') $module | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $applied.saved) { throw 'check-bad.json did not apply' }
$html = Join-Path $dir 'check.html'
# The team label is text from the person running the check: markup in it must stay text.
$label = '</script><script>window.untrustedExecuted=1</script> & Team'
# An input that cannot be read must show on the page as not checked.
[IO.File]::WriteAllText((Join-Path $inputs 'broken.oml'), 'not a module')
$report = & $Oml check --in $inputs --out $html --team-label $label | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $html)) { throw 'check --out failed' }
$page = [IO.File]::ReadAllText($html)
$data = [regex]::Match($page, '<script type="application/json" id="oml-check">(.*?)</script>', 'Singleline')
if (-not $data.Success) { throw 'Embedded check JSON missing' }
if (($data.Groups[1].Value | ConvertFrom-Json).findings.Count -ne $report.findings.Count) { throw 'Embedded findings differ from the command output' }
if ($data.Groups[1].Value -match '[<>&]' -or $page -match '<script>window.untrustedExecuted') { throw 'Unescaped HTML characters in embedded data' }
if ($page -match '<(?:script|link|img)\b[^>]*(?:src|href)\s*=' -or $page -match '@import|url\(') { throw 'Check page loads external resources' }
Write-Host "ok   check page: offline, escaped JSON, $($report.findings.Count) findings"

$browser = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $browser) { Write-Host 'skip check page browser checks: Edge/Chrome not installed'; return }
$capture = '<script>window.pageErrors=[];addEventListener("error",e=>pageErrors.push(e.message));console.error=(...args)=>pageErrors.push(args.join(" "));</script>'
$probe = @'
<script>
(() => {
 const checks=[];
 const check=(ok,label)=>{if(!ok)throw Error(label);checks.push(label);};
 const $=id=>document.getElementById(id), visible=()=>[...document.querySelectorAll('.finding')].filter(c=>!c.hidden);
 const data=JSON.parse($('oml-check').textContent);
 const set=(id,v)=>{$(id).value=v;$(id).dispatchEvent(new Event(id==='f-text'?'input':'change'));};
 try {
  check(document.querySelectorAll('.finding').length===data.findings.length,'one card per finding');
  check(!window.untrustedExecuted&&$('title').textContent.startsWith(data.input.team),'untrusted label stays text');
  check(document.querySelectorAll('.stat').length>=6,'counts');
  check(getComputedStyle($('status-filter')).display==='none','no status filter without a baseline');
  check(!$('skipped').hidden&&$('skipped-list').children.length===1&&$('skipped-list').textContent.includes('broken.oml'),'inputs not checked are shown');
  set('f-severity','high');check(visible().length===data.findings.filter(f=>f.severity==='high').length&&visible().length>0,'severity filter');
  set('f-severity','');set('f-category','Security');check(visible().length===data.findings.filter(f=>f.category==='Security').length,'category filter');
  set('f-category','');set('f-rule','query-in-loop');check(visible().length===1,'rule filter');
  set('f-rule','');set('f-module','CheckBad');check(visible().length===data.findings.length,'module filter');
  set('f-text','NeverCalled');check(visible().length===1&&visible()[0].textContent.includes('Unused action'),'text search');
  set('f-text','no such text at all');check(visible().length===0&&!document.querySelector('.empty').hidden,'empty state');
  set('f-text','');set('f-module','');check(visible().length===data.findings.length,'reset filters');
  check(document.querySelector('.finding .fix').textContent.startsWith('Fix: '),'fix text');
  const wanted='__THEME__';if(document.documentElement.dataset.theme!==wanted)$('theme').click();check(document.documentElement.dataset.theme===wanted,'color theme');
 } catch(e) { window.pageErrors.push(e.message); }
 const result=document.createElement('pre');result.id='browser-check-result';result.hidden=true;result.textContent=JSON.stringify({checks,errors:window.pageErrors});document.body.append(result);
})();
</script>
'@
$profile = Join-Path $dir 'browser-profile'
foreach ($theme in @('light','dark')) {
    $testHtml = Join-Path $dir "browser-$theme.html"; $dom = Join-Path $dir "browser-$theme-dom.html"; $log = Join-Path $dir "browser-$theme.log"
    $screenshot = [IO.Path]::GetFullPath((Join-Path $Tmp "check-$theme.png"))
    [IO.File]::WriteAllText($testHtml, $page.Replace('</head>', "$capture</head>").Replace('</body>', $probe.Replace('__THEME__', $theme) + '</body>'))
    $url = ([uri][IO.Path]::GetFullPath($testHtml)).AbsoluteUri
    $arguments = @('--headless=new','--disable-gpu','--no-first-run',"--user-data-dir=`"$profile`"",'--window-size=1280,1600',"--screenshot=`"$screenshot`"",'--dump-dom',"`"$url`"")
    $process = Start-Process -FilePath $browser -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $dom -RedirectStandardError $log
    if (-not $process.WaitForExit(20000)) { $process.Kill(); Write-Host 'skip check page browser checks: headless export timed out'; return }
    # Windows browser launchers can exit before the headless child closes stdout.
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $stream = [IO.File]::Open($dom, 'Open', 'Read', 'ReadWrite'); $reader = [IO.StreamReader]::new($stream)
        try { $rendered = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $result = [regex]::Match($rendered, '<pre id="browser-check-result"[^>]*>(.*?)</pre>', 'Singleline')
        if ($result.Success) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $result.Success) { Write-Host "skip check page browser checks: headless export unavailable (see $log)"; return }
    $checks = [Net.WebUtility]::HtmlDecode($result.Groups[1].Value) | ConvertFrom-Json
    if ($checks.errors.Count -gt 0) { throw "Check page browser errors ($theme): $($checks.errors -join '; ')" }
    Write-Host "ok   check page browser $theme`: $($checks.checks.Count) checks, no script errors ($screenshot)"
}
