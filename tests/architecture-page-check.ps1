param([string]$Oml = "$PSScriptRoot\..\src\bin\Release\net8.0\oml.exe", [string]$Tmp = "$PSScriptRoot\..\tmp")
$ErrorActionPreference = 'Stop'
$dir = Join-Path $Tmp 'architecture-page'
New-Item -ItemType Directory -Force $dir | Out-Null
# A unique output also lets this check run twice without deleting user files.
$html = Join-Path $dir ("sample-" + [guid]::NewGuid().ToString('N') + '.html')
$sample = Join-Path $PSScriptRoot 'architecture-sample.json'
$result = & $Oml architecture-render $sample $html | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $result.output)) { throw 'Architecture render failed' }
$page = [IO.File]::ReadAllText($html)
$data = [regex]::Match($page, '<script type="application/json" id="oml-architecture">(.*?)</script>', 'Singleline')
if (-not $data.Success) { throw 'Embedded architecture JSON missing' }
$actual = $data.Groups[1].Value | ConvertFrom-Json
$expected = Get-Content -Raw $sample | ConvertFrom-Json
if ($actual.schema -ne 1 -or $actual.modules.Count -ne 18 -or $actual.applications.Count -ne 3 -or $actual.findings.Count -ne 8) { throw 'Architecture sample contract changed' }
if (($actual | ConvertTo-Json -Depth 50 -Compress) -cne ($expected | ConvertTo-Json -Depth 50 -Compress)) { throw 'Embedded JSON did not round trip' }
if ($data.Groups[1].Value -match '[<>&]' -or $page -match '<script>window.untrustedExecuted') { throw 'Unsafe HTML in embedded data' }
if ($page -match '<(?:script|link|img)\b[^>]*(?:src|href)\s*=' -or $page -match '@import|url\(\s*["'']?https?:') { throw 'Architecture page contains external resources' }
if ($page -notmatch 'Copyright \(c\) 2026 OutSystems' -or $page -notmatch 'MIT License') { throw 'Architecture template notice missing' }
$before = (Get-FileHash -LiteralPath $html).Hash
$refusal = & $Oml architecture-render $sample $html | ConvertFrom-Json
if ($LASTEXITCODE -ne 2 -or $refusal.error -notlike 'Output already exists:*Use a new output path, or delete that file first.') { throw 'Architecture render must refuse overwrite' }
if ((Get-FileHash -LiteralPath $html).Hash -ne $before) { throw 'Existing HTML was changed' }
$invalid = Join-Path $dir 'invalid.json'
[IO.File]::WriteAllText($invalid, '{ invalid json')
$badOut = Join-Path $dir ([guid]::NewGuid().ToString('N') + '.html')
$null = & $Oml architecture-render $invalid $badOut
if ($LASTEXITCODE -ne 2 -or (Test-Path -LiteralPath $badOut)) { throw 'Invalid JSON must fail before output creation' }
$null = & $Oml architecture-render (Join-Path $dir 'missing.json') $badOut
if ($LASTEXITCODE -ne 2) { throw 'Missing JSON must return a CLI error' }
$help = & $Oml --help
if ($LASTEXITCODE -ne 0 -or ($help -join "`n") -match 'architecture-render') { throw 'Render helper must stay hidden' }
Write-Host "ok   architecture page: offline resources, JSON round trip, script escaping, no overwrite ($html)"

# Optional real-browser check: headless export needs no driver or debugging port.
$browser = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $browser) { Write-Host 'skip architecture browser checks: Edge/Chrome not installed'; return }
$capture = '<script>window.pageErrors=[];addEventListener("error",e=>pageErrors.push(e.message));console.error=(...args)=>pageErrors.push(args.join(" "));</script>'
$probe = @'
<script>
(() => {
 const checks=[];
 const check=(ok,label)=>{if(!ok)throw Error(label);checks.push(label);};
 const $=id=>document.getElementById(id), box=()=>$('graph').getAttribute('viewBox');
 const data=JSON.parse($('oml-architecture').textContent);
 try {
  check(document.querySelectorAll('.node').length===data.modules.length,'module cards');
  check(document.querySelectorAll('.edge').length===data.edges.length,'dependency arrows');
  check(document.querySelectorAll('.finding').length===data.findings.length,'findings');
  check(!window.untrustedExecuted,'untrusted script stays text');
  const initial=box();$('zoom-in').click();check(box()!==initial,'zoom in');$('fit').click();check(box()===initial,'fit');
  const order=document.querySelector('.node[aria-label^="Orders_CS,"]');order.dispatchEvent(new MouseEvent('click',{bubbles:true}));
  check($('details').textContent.includes('RepriceLegacyOrder')&&$('details').textContent.includes('Uses')&&$('details').textContent.includes('Used by'),'module details and references');
  check(document.querySelectorAll('.node.selected').length===1&&document.querySelectorAll('.node.faded').length>0,'selection highlights neighbors');
  $('reset').click();$('search').value='WarehouseERP';$('search').dispatchEvent(new Event('input'));
  check($('search-results').textContent.includes('ERP_IS'),'search element names');
  $('reset').click();document.querySelector('.finding button').click();check(document.querySelectorAll('.node.selected').length===2,'finding locates modules');
  $('graph').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));check(document.querySelectorAll('.node.faded,.node.selected').length===0,'Escape reset');
  $('graph').focus();$('graph').dispatchEvent(new KeyboardEvent('keydown',{key:'+',bubbles:true}));check(box()!==initial,'keyboard zoom');
  const zoomed=box();$('graph').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true}));check(box()!==zoomed,'keyboard pan');
  $('fit').click();const r=$('graph').getBoundingClientRect();$('graph').dispatchEvent(new WheelEvent('wheel',{deltaY:-80,clientX:r.left+100,clientY:r.top+100,bubbles:true,cancelable:true}));check(box()!==initial,'wheel zoom');
  $('fit').click();order.dispatchEvent(new PointerEvent('pointerdown',{pointerId:42,button:0,clientX:100,clientY:100,bubbles:true}));
  for(let x=101;x<=112;x++)$('graph').dispatchEvent(new PointerEvent('pointermove',{pointerId:42,clientX:x,clientY:100,bubbles:true}));
  $('graph').dispatchEvent(new PointerEvent('pointerup',{pointerId:42,clientX:112,clientY:100,bubbles:true}));check(box()!==initial,'slow drag pans');
  $('reset').click();$('graph').blur();
  const cards=[...document.querySelectorAll('.node')].map(n=>{const t=n.transform.baseVal.getItem(0).matrix;return {x:t.e,y:t.f};});
  check(cards.every((a,i)=>cards.every((b,j)=>i===j||Math.abs(a.x-b.x)>=214||Math.abs(a.y-b.y)>=70)),'cards do not overlap');
  const wanted='__THEME__';if(document.documentElement.dataset.theme!==wanted)$('theme').click();check(document.documentElement.dataset.theme===wanted,'color theme');
 } catch(e) { window.pageErrors.push(e.message); }
 const result=document.createElement('pre');result.id='browser-check-result';result.hidden=true;result.textContent=JSON.stringify({checks,errors:window.pageErrors});document.body.append(result);
})();
</script>
'@
$profile = Join-Path $dir ('browser-profile-' + [guid]::NewGuid().ToString('N'))
foreach ($theme in @('light','dark')) {
    $testHtml = Join-Path $dir "browser-$theme.html"
    $dom = Join-Path $dir "browser-$theme-dom.html"
    $log = Join-Path $dir "browser-$theme.log"
    $screenshot = [IO.Path]::GetFullPath((Join-Path $Tmp "architecture-$theme.png"))
    $instrumented = $page.Replace('</head>', "$capture</head>").Replace('</body>', $probe.Replace('__THEME__', $theme) + '</body>')
    [IO.File]::WriteAllText($testHtml, $instrumented)
    $url = ([uri][IO.Path]::GetFullPath($testHtml)).AbsoluteUri
    $arguments = @('--headless=new','--disable-gpu','--no-first-run',"--user-data-dir=`"$profile`"",'--window-size=1680,1400',"--screenshot=`"$screenshot`"",'--dump-dom',"`"$url`"")
    $process = Start-Process -FilePath $browser -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $dom -RedirectStandardError $log
    if (-not $process.WaitForExit(20000)) {
        $process.Kill()
        Write-Host 'skip architecture browser checks: headless export timed out'
        return
    }
    # Windows browser launchers can exit before the headless child closes stdout.
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $stream = [IO.File]::Open($dom, 'Open', 'Read', 'ReadWrite')
        $reader = [IO.StreamReader]::new($stream)
        try { $rendered = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $report = [regex]::Match($rendered, '<pre id="browser-check-result"[^>]*>(.*?)</pre>', 'Singleline')
        if ($report.Success) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $report.Success) { Write-Host "skip architecture browser checks: headless export unavailable (see $log)"; return }
    $checks = [Net.WebUtility]::HtmlDecode($report.Groups[1].Value) | ConvertFrom-Json
    if ($checks.errors.Count -gt 0) { throw "Architecture browser errors: $($checks.errors -join '; ')" }
    if (-not (Test-Path -LiteralPath $screenshot)) { throw 'Architecture browser screenshot missing' }
    Write-Host "ok   architecture browser $theme`: $($checks.checks.Count) checks, no console errors ($screenshot)"
}
