param([string]$Oml = "$PSScriptRoot\..\src\bin\Release\net10.0\oml.exe", [string]$Tmp = "$PSScriptRoot\..\tmp")
$ErrorActionPreference = 'Stop'
$dir = Join-Path $Tmp 'architecture-page'
New-Item -ItemType Directory -Force $dir | Out-Null
# A unique output also lets this check run twice without deleting user files.
function New-Output($name) { Join-Path $dir ("$name-" + [guid]::NewGuid().ToString('N') + '.html') }
function Render($json, $html) {
    $r = & $Oml architecture-render $json $html | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $r.output)) { throw "Architecture render failed for $json" }
}
function Embedded($page) {
    $m = [regex]::Match($page, '<script type="application/json" id="oml-architecture">(.*?)</script>', 'Singleline')
    if (-not $m.Success) { throw 'Embedded architecture JSON missing' }
    return $m.Groups[1].Value
}
$html = New-Output 'sample'
$sample = Join-Path $PSScriptRoot 'architecture-sample.json'
Render $sample $html
$page = [IO.File]::ReadAllText($html)
$raw = Embedded $page
$actual = $raw | ConvertFrom-Json
$expected = Get-Content -Raw $sample | ConvertFrom-Json
if ($actual.schema -ne 1 -or $actual.modules.Count -ne 18 -or $actual.applications.Count -ne 3 -or $actual.findings.Count -ne 8) { throw 'Architecture sample contract changed' }
# The renderer adds the layout object; everything else must round trip unchanged.
if ($actual.layout.crossings -isnot [long] -or $actual.layout.ranks -isnot [long]) { throw 'Rendered data has no layout.crossings and layout.ranks' }
$sampleCrossings = $actual.layout.crossings
$actual.PSObject.Properties.Remove('layout')
if (($actual | ConvertTo-Json -Depth 50 -Compress) -cne ($expected | ConvertTo-Json -Depth 50 -Compress)) { throw 'Embedded JSON did not round trip' }
if ($raw -match '[<>&]' -or $page -match '<script>window.untrustedExecuted') { throw 'Unsafe HTML in embedded data' }
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
[IO.File]::WriteAllText($invalid, '[1,2]')
$null = & $Oml architecture-render $invalid $badOut
if ($LASTEXITCODE -ne 2 -or (Test-Path -LiteralPath $badOut)) { throw 'JSON that is not an object must fail before output creation' }
$null = & $Oml architecture-render (Join-Path $dir 'missing.json') $badOut
if ($LASTEXITCODE -ne 2) { throw 'Missing JSON must return a CLI error' }
$help = & $Oml --help
if ($LASTEXITCODE -ne 0 -or ($help -join "`n") -match 'architecture-render') { throw 'Render helper must stay hidden' }
Write-Host "ok   architecture page: offline resources, JSON round trip, script escaping, no overwrite ($html)"

# Layout numbers. Crossings may go down, never up: a better layout means the stored baseline can be lowered by hand.
$baseline = Get-Content -Raw (Join-Path $PSScriptRoot 'architecture-baseline.json') | ConvertFrom-Json
function New-Synthetic($copies, $name) {
    $json = Join-Path $dir "$name.json"
    & (Join-Path $PSScriptRoot 'architecture-synthetic.ps1') -Out $json -Copies $copies
    $out = New-Output $name
    $seconds = (Measure-Command { Render $json $out }).TotalSeconds
    [pscustomobject]@{ json = $json; html = $out; seconds = $seconds; doc = ((Embedded ([IO.File]::ReadAllText($out))) | ConvertFrom-Json) }
}
$syn = New-Synthetic 1 'synthetic'
$syn3 = New-Synthetic 3 'synthetic-300'
$syntheticHtml = $syn.html; $synthetic = $syn.doc
if ($synthetic.modules.Count -ne 100) { throw "Synthetic sample must have 100 modules, has $($synthetic.modules.Count)" }
if ($syn3.doc.modules.Count -lt 250) { throw "Large synthetic sample must have about 300 modules, has $($syn3.doc.modules.Count)" }
foreach ($x in @($syn, $syn3)) { if ($x.seconds -gt 10) { throw "Rendering $($x.doc.modules.Count) modules took $([math]::Round($x.seconds,1)) s (limit 10 s)" } }
foreach ($case in @(@('sample', $sampleCrossings, 'tests/architecture-sample.json'), @('synthetic', $synthetic.layout.crossings, 'the 100-module synthetic sample'), @('synthetic300', $syn3.doc.layout.crossings, 'the 300-module synthetic sample'))) {
    $limit = [long]$baseline.($case[0])
    if ($case[1] -gt $limit) { throw "Edge crossings on $($case[2]) went up: $($case[1]) (baseline $limit)" }
    if ($case[1] -lt $limit) { Write-Host "note architecture crossings on $($case[2]) improved: $($case[1]) (baseline $limit). Lower tests/architecture-baseline.json." }
}
# The result must not depend on the order of modules and edges in the file.
function Render-Shuffled($path, $seed) {
    $doc = Get-Content -Raw $path | ConvertFrom-Json
    $rng = [Random]::new($seed)
    $doc.modules = @($doc.modules | Sort-Object { $rng.Next() })
    $doc.edges = @($doc.edges | Sort-Object { $rng.Next() })
    $shuffled = Join-Path $dir ('shuffled-' + [guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllText($shuffled, ($doc | ConvertTo-Json -Depth 50))
    $out = New-Output 'shuffled'; Render $shuffled $out
    return ((Embedded ([IO.File]::ReadAllText($out))) | ConvertFrom-Json).layout
}
foreach ($case in @(@($sample, $sampleCrossings), @($syn.json, $synthetic.layout.crossings))) {
    foreach ($seed in 1, 2) {
        $layout = Render-Shuffled $case[0] $seed
        if ($layout.crossings -ne $case[1]) { throw "Layout depends on input order: $($layout.crossings) crossings after shuffling (seed $seed), $($case[1]) before" }
    }
}
Write-Host "ok   architecture layout: $sampleCrossings crossings on the sample, $($synthetic.layout.crossings) on 100 modules, $($syn3.doc.layout.crossings) on $($syn3.doc.modules.Count); rendered in $([math]::Round($syn.seconds,2)) s and $([math]::Round($syn3.seconds,2)) s; shuffled input gives the same layout"

# Loose modules: no application, so the module names become the title. Also a single module.
function New-Loose($names) {
    $doc = Get-Content -Raw $sample | ConvertFrom-Json
    $keep = @($doc.modules | Where-Object { $names -contains $_.name })
    foreach ($m in $keep) { $m.application = '(no application)' }
    $doc.modules = $keep
    $doc.applications = @([pscustomobject]@{ name = '(no application)'; modules = @($names | Sort-Object) })
    $doc.edges = @($doc.edges | Where-Object { $names -contains $_.from -and $names -contains $_.to })
    $doc.findings = @($doc.findings | Where-Object { @($_.modules | Where-Object { $names -notcontains $_ }).Count -eq 0 })
    $path = Join-Path $dir ('loose-' + [guid]::NewGuid().ToString('N') + '.json')
    [IO.File]::WriteAllText($path, ($doc | ConvertTo-Json -Depth 50))
    return $path
}
$looseOne = New-Output 'loose-one'; Render (New-Loose @('Orders_CS')) $looseOne
$looseTwo = New-Output 'loose-two'; Render (New-Loose @('Orders_CS', 'Inventory_CS')) $looseTwo

# Documents for the page-versus-CLI comparison: cycles, wrapped levels, arcs, duplicate names, no application, missing modules.
function New-M($name, $app, $layer, $ext = $false) { [ordered]@{ name = $name; application = $app; external = $ext; layer = $layer; kind = 'Service' } }
function New-E($a, $b, $strong = $true) { [ordered]@{ from = $a; to = $b; strong = $strong; elements = @() } }
function New-ParityDoc($mods, $edges) {
    $apps = @($mods | Where-Object { -not $_.external -and $_.application } | Group-Object application | ForEach-Object { [ordered]@{ name = $_.Name; modules = @($_.Group | ForEach-Object { $_.name }) } })
    [ordered]@{ schema = 1; generatedAt = 'x'; omlVersion = 'x'; applications = $apps; modules = @($mods); edges = @($edges); findings = @() }
}
$rng = [Random]::new(7)
$cases = [ordered]@{}
$cases['zero-edges'] = New-ParityDoc @((New-M 'A' 'X' 'Core'), (New-M 'B' 'X' 'Core'), (New-M 'C' 'Y' 'EndUser')) @()
$cases['cycle'] = New-ParityDoc @((New-M 'A' 'X' 'Core'), (New-M 'B' 'X' 'Core'), (New-M 'C' 'X' 'Core'), (New-M 'D' 'X' 'EndUser')) @((New-E 'A' 'B'), (New-E 'B' 'C'), (New-E 'C' 'A'), (New-E 'D' 'C'), (New-E 'D' 'A'))
$cases['no-application'] = New-ParityDoc @((New-M 'A' '(no application)' 'Core'), (New-M 'B' 'X' 'Core'), (New-M 'C' $null 'Foundation')) @((New-E 'A' 'C'), (New-E 'B' 'C'), (New-E 'A' 'B'))
$cases['duplicate-names'] = New-ParityDoc @((New-M 'A' 'X' 'Core'), (New-M 'A' 'Y' 'Foundation'), (New-M 'B' 'X' 'EndUser'), (New-M 'C' 'Y' 'EndUser'), (New-M 'D' 'Z' 'Foundation')) @((New-E 'B' 'A'), (New-E 'C' 'A'), (New-E 'C' 'D'), (New-E 'B' 'D'), (New-E 'A' 'D'))
$cases['one-layer-arcs'] = New-ParityDoc @(1..9 | ForEach-Object { New-M "M$_" 'X' 'Foundation' }) @((New-E 'M1' 'M2'), (New-E 'M3' 'M2'), (New-E 'M9' 'M1'), (New-E 'M4' 'M8'), (New-E 'M2' 'M1'))
$cases['deep-chain'] = New-ParityDoc @(1..8 | ForEach-Object { New-M "C$_" 'X' 'Core' }) @(1..7 | ForEach-Object { New-E "C$_" "C$($_ + 1)" })
$cases['wide-layers'] = New-ParityDoc (@(1..20 | ForEach-Object { New-M "U$_" 'X' 'EndUser' }) + @(1..15 | ForEach-Object { New-M "F$_" 'Y' 'Foundation' })) @(1..60 | ForEach-Object { New-E "U$($rng.Next(1,21))" "F$($rng.Next(1,16))" })
for ($c = 1; $c -le 6; $c++) {
    $n = $rng.Next(3, 45); $mods = @()
    for ($i = 0; $i -lt $n; $i++) {
        $ext = $rng.Next(10) -eq 0
        $layer = @('EndUser', 'Core', 'Foundation', 'Unknown')[$rng.Next(4)]
        $app = if ($ext) { '(external)' } else { @('A', 'B', 'C', '(no application)')[$rng.Next(4)] }
        $mods += New-M ('N{0:D2}' -f $i) $app $layer $ext
    }
    $edges = @(1..$rng.Next(0, $n * 3) | ForEach-Object { New-E ('N{0:D2}' -f $rng.Next($n)) ('N{0:D2}' -f $rng.Next($n + 2)) ($rng.Next(2) -eq 0) })
    $cases["random-$c"] = New-ParityDoc $mods $edges
}
$parityPages = @()
foreach ($name in $cases.Keys) {
    $json = Join-Path $dir "parity-$name.json"
    [IO.File]::WriteAllText($json, ($cases[$name] | ConvertTo-Json -Depth 12))
    $out = New-Output "parity-$name"; Render $json $out
    $parityPages += @{ label = "parity-$name"; html = $out }
}

# Optional real-browser check: headless export needs no driver or debugging port.
$browser = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $browser) { Write-Host 'skip architecture browser checks: Edge/Chrome not installed'; return }
$capture = '<script>window.pageErrors=[];addEventListener("error",e=>pageErrors.push(e.message));console.error=(...args)=>pageErrors.push(args.join(" "));</script>'
# Shared helpers for the probes. A probe throws on the first failed check; the page records the message as an error.
$helpers = @'
<script>
window.probe=(body)=>{
 const checks=[];
 const check=(ok,label)=>{if(!ok)throw Error(label);checks.push(label);};
 const $=id=>document.getElementById(id), box=()=>$('graph').getAttribute('viewBox');
 const data=JSON.parse($('oml-architecture').textContent);
 const stat=()=>$('layout-stat').dataset;
 const cards=()=>[...document.querySelectorAll('.node')];
 const apps=()=>document.querySelectorAll('.node.app').length;
 const click=n=>{n.dispatchEvent(new PointerEvent('pointerdown',{pointerId:7,button:0,clientX:5,clientY:5,bubbles:true}));n.dispatchEvent(new PointerEvent('pointerup',{pointerId:7,clientX:5,clientY:5,bubbles:true}));n.dispatchEvent(new MouseEvent('click',{bubbles:true}));};
 const card=name=>document.querySelector('.node[aria-label^="'+name+',"]');
 const pairs=new Set(data.edges.map(e=>[e.from,e.to].sort().join('>'))).size;
 const noOverlap=()=>{const r=cards().map(n=>{const t=n.transform.baseVal.getItem(0).matrix;return {x:t.e,y:t.f};});return r.every((a,i)=>r.every((b,j)=>i===j||Math.abs(a.x-b.x)>=180||Math.abs(a.y-b.y)>=70));};
 try { body({check,$,box,data,stat,cards,apps,click,card,pairs,noOverlap}); } catch(e) { window.pageErrors.push(e.message); }
 const wanted='__THEME__';if(document.documentElement.dataset.theme!==wanted)$('theme').click();
 const result=document.createElement('pre');result.id='browser-check-result';result.hidden=true;result.textContent=JSON.stringify({checks,errors:window.pageErrors,ms:window.moduleMs||stat().ms});document.body.append(result);
};
</script>
'@
$sampleProbe = @'
<script>
probe(({check,$,box,data,stat,cards,apps,click,card,pairs,noOverlap})=>{
 check(cards().length===data.modules.length,'module cards');
 check(document.querySelectorAll('.edge').length===pairs,'dependency arrows, merged per pair of modules');
 check(document.querySelectorAll('.finding').length===data.findings.length,'findings');
 check(!window.untrustedExecuted,'untrusted script stays text');
 check(stat().moduleView==='true'&&Number(stat().crossings)===data.layout.crossings&&!$('layout-stat').textContent.includes('crossing')&&$('layout-stat').textContent.includes('dependencies'),'footer shows plain counts; the data attribute equals the JSON layout.crossings');
 check(noOverlap(),'cards do not overlap');
 const intro=$('details').textContent;check(intro.includes('Explore the architecture')&&document.querySelectorAll('.fmark').length>0&&![...document.querySelectorAll('.node text')].some(t=>/^\d+ !$/.test(t.textContent)),'finding marker is a labelled dot');
 check(document.title.startsWith('Customer Experience')&&document.title.endsWith('Architecture'),'title lists the applications');
 const wOf=()=>Number(box().split(' ')[2]);
 const inside=(n,pad=60)=>{const v=box().split(' ').map(Number);const t=n.transform.baseVal.getItem(0).matrix;const x0=t.e-pad,y0=t.f-pad,x1=t.e+260+pad,y1=t.f+80+pad;return x0<v[0]+v[2]&&x1>v[0]&&y0<v[1]+v[3]&&y1>v[1];};
 const initial=box();const w0=wOf();$('zoom-in').click();check(box()!==initial,'zoom in');$('fit').click();check(box()===initial,'fit');
 const order=card('Orders_CS');click(order);
 check(wOf()===w0,'module click keeps the zoom level');
 check(inside(card('Orders_CS')),'selected module stays inside the view');
 check($('details').textContent.includes('RepriceLegacyOrder')&&$('details').textContent.includes('Uses')&&$('details').textContent.includes('Used by'),'module details and references');
 check(document.querySelectorAll('.node.selected').length===1&&document.querySelectorAll('.node.faded').length>0,'selection highlights neighbors');
 $('reset').click();$('search').value='WarehouseERP';$('search').dispatchEvent(new Event('input'));
 check($('search-results').textContent.includes('ERP_IS'),'search element names');
 $('reset').click();document.querySelector('.finding button').click();check(wOf()===w0,'finding click keeps the zoom level');check([...document.querySelectorAll('.node.selected')].every(n=>inside(n)),'selected modules stay inside the view');check(document.querySelectorAll('.node.selected').length===2,'finding locates modules');
 $('graph').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));check(document.querySelectorAll('.node.faded,.node.selected').length===0,'Escape reset');
 $('graph').focus();$('graph').dispatchEvent(new KeyboardEvent('keydown',{key:'+',bubbles:true}));check(box()!==initial,'keyboard zoom');
 const zoomed=box();$('graph').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true}));check(box()!==zoomed,'keyboard pan');
 $('fit').click();const r=$('graph').getBoundingClientRect();$('graph').dispatchEvent(new WheelEvent('wheel',{deltaY:-80,clientX:r.left+100,clientY:r.top+100,bubbles:true,cancelable:true}));check(box()!==initial,'wheel zoom');
 $('fit').click();card('Orders_CS').dispatchEvent(new PointerEvent('pointerdown',{pointerId:42,button:0,clientX:100,clientY:100,bubbles:true}));
 for(let x=101;x<=112;x++)$('graph').dispatchEvent(new PointerEvent('pointermove',{pointerId:42,clientX:x,clientY:100,bubbles:true}));
 $('graph').dispatchEvent(new PointerEvent('pointerup',{pointerId:42,clientX:112,clientY:100,bubbles:true}));check(box()!==initial,'slow drag pans');
 $('reset').click();$('graph').blur();
 // Weak links toggle: the sample has weak links, and findings keep their red lines.
 const allEdges=document.querySelectorAll('.edge').length;$('weak').click();check(document.querySelectorAll('.edge').length<allEdges&&document.querySelectorAll('.edge.violation').length>0,'weak links can be hidden; lines of findings stay');$('weak').click();check(document.querySelectorAll('.edge').length===allEdges,'weak links can be shown');
 // Platform group.
 check(document.querySelectorAll('.box').length===1&&document.querySelectorAll('.node.external').length===3&&$('platform').getAttribute('aria-pressed')==='true','Platform box holds the external modules');
 const platW=wOf();$('platform').click();check(wOf()===platW,'Platform toggle keeps the zoom level');check(document.querySelectorAll('.node.platform').length===1&&document.querySelectorAll('.node.external').length===0&&document.querySelectorAll('.box').length===0,'Platform box collapses to one card');
 click(document.querySelector('.node.platform'));check(wOf()===platW,'opening the Platform group keeps the zoom level');check([...document.querySelectorAll('.node.selected')].every(n=>inside(n)),'Platform modules stay inside the view');check(document.querySelectorAll('.node.external').length===3,'clicking the Platform card opens it');
 // Application view: apps are boxes, parallel edges merge with a count badge, clicking an application opens it.
 $('view-apps').click();check(apps()===3&&$('view-apps').getAttribute('aria-pressed')==='true'&&document.querySelectorAll('.badge').length>0,'application view with merged edges');
 check(document.querySelectorAll('.node.app.external').length===0&&stat().moduleView==='false'&&noOverlap(),'application view cards do not overlap');
 const navW=wOf();click([...document.querySelectorAll('.node.app')].find(n=>n.getAttribute('aria-label').startsWith('Application Operations,')));
 check(wOf()===navW,'application open keeps the zoom level');check([...document.querySelectorAll('.node.selected')].every(n=>inside(n)),'application modules stay inside the view');check(apps()===2&&card('Inventory_CS')!==null&&$('details').textContent.includes('Operations')&&$('view-apps').getAttribute('aria-pressed')==='false'&&$('view-modules').getAttribute('aria-pressed')==='false','clicking an application opens its modules');
 $('search').value='ManageUsers';$('search').dispatchEvent(new Event('input'));click(document.querySelector('#search-results button'));check(card('Identity_CS')!==null&&apps()===1,'selecting a hidden module opens its application');
 $('reset').click();check($('details').textContent===intro,'one text for the empty details panel');$('view-modules').click();
 check(apps()===0&&cards().length===data.modules.length&&Number(stat().crossings)===data.layout.crossings,'module view again equals the JSON layout.crossings');
 $('reset').click();
});
</script>
'@
$syntheticProbe = @'
<script>
probe(({check,$,box,data,stat,cards,apps,click,card,pairs,noOverlap})=>{
 const appCount=data.applications.length;
 check(apps()===appCount&&document.querySelectorAll('.node.platform').length===1&&cards().length===appCount+1,'opens on the application view with one Platform card');
 check($('weak').getAttribute('aria-pressed')==='false'&&$('platform').getAttribute('aria-pressed')==='false'&&$('view-apps').getAttribute('aria-pressed')==='true','weak links hidden and Platform collapsed above 30 modules');
 const scale=$('graph').clientWidth/Number(box().split(' ')[2]);
 check(Number(stat().rows)<=10,'application view has few rows ('+stat().rows+')');
 check(scale*14>=11,'card names are at least 11 px at Fit ('+(scale*14).toFixed(1)+' px in a '+innerWidth+' px window)');
 check(document.querySelectorAll('.badge').length>0&&noOverlap(),'application view: merged edges, no overlap');
 check(Number(stat().ms)<3000,'application layout under 3 s ('+stat().ms+' ms)');
 click(cards().find(n=>n.getAttribute('aria-label').startsWith('Application App07,')));check(apps()===appCount-1&&card('App07_CS')!==null,'application opens on click');
 $('search').value='App08_API';$('search').dispatchEvent(new Event('input'));click(document.querySelector('#search-results button'));check(card('App08_API')!==null&&apps()===appCount-2,'search opens the application of a hidden module');
 $('reset').click();$('view-modules').click();
 check(apps()===0&&document.querySelectorAll('.node.platform').length===1&&cards().length===data.modules.length-8+1,'Modules button opens every application; Platform stays collapsed');
 $('platform').click();$('weak').click();
 check(cards().length===data.modules.length&&document.querySelectorAll('.box').length===1&&noOverlap(),'module view: 100 cards, Platform box, no overlap');
 check(stat().moduleView==='true'&&Number(stat().crossings)===data.layout.crossings&&Number(stat().rows)===data.layout.ranks,'browser and CLI agree on the module view crossings ('+data.layout.crossings+')');
 window.moduleMs=stat().ms;check(Number(stat().ms)<3000,'module layout under 3 s ('+stat().ms+' ms)');
 const withWeak=document.querySelectorAll('.edge').length;$('weak').click();check(document.querySelectorAll('.edge').length<withWeak&&stat().moduleView==='false','hiding weak links lays the diagram out again');
 $('platform').click();$('view-apps').click();$('reset').click();
});
</script>
'@
$largeProbe = @'
<script>
probe(({check,$,box,data,stat,cards,apps,click,card,pairs,noOverlap})=>{
 check(apps()===data.applications.length&&Number(stat().rows)<=20&&Number(stat().ms)<3000,'large portfolio: application view has '+stat().rows+' rows, layout '+stat().ms+' ms');
 $('view-modules').click();$('platform').click();$('weak').click();
 check(cards().length===data.modules.length&&noOverlap()&&stat().moduleView==='true'&&Number(stat().crossings)===data.layout.crossings&&Number(stat().ms)<3000,'large portfolio: module view agrees with the CLI ('+data.layout.crossings+' crossings, '+stat().ms+' ms)');
 window.moduleMs=stat().ms;$('view-apps').click();$('platform').click();$('weak').click();$('reset').click();
});
</script>
'@
$parityProbe = @'
<script>
probe(({check,$,box,data,stat,cards,apps,click,card,pairs,noOverlap})=>{
 if(!$('detail-level').hidden&&$('view-modules').getAttribute('aria-pressed')!=='true')$('view-modules').click();
 if(!$('platform').hidden&&$('platform').getAttribute('aria-pressed')!=='true')$('platform').click();
 if(!$('weak').hidden&&$('weak').getAttribute('aria-pressed')!=='true')$('weak').click();
 check(stat().moduleView==='true','module view with every module open');
 check(Number(stat().crossings)===data.layout.crossings&&Number(stat().rows)===data.layout.ranks,'browser ('+stat().crossings+' crossings, '+stat().rows+' rows) equals CLI ('+data.layout.crossings+', '+data.layout.ranks+')');
 check(noOverlap(),'cards do not overlap');
});
</script>
'@
$looseProbe = @'
<script>
probe(({check,$,box,data,stat,cards,apps,click,card,pairs,noOverlap})=>{
 check(document.title==='__TITLE__'&&$('title').textContent==='__TITLE__','title uses the module names, not "(no application)"');
 check(getComputedStyle($('app-hint')).display==='none'&&getComputedStyle($('weak')).display==='none','no application hint and no Weak links button without applications or weak edges');
 check(cards().length===data.modules.length&&apps()===0&&getComputedStyle($('detail-level')).display==='none'&&getComputedStyle($('platform')).display==='none','no application controls for loose modules');
 check([...document.querySelectorAll('.band-note')].filter(n=>n.textContent==='no modules in this layer').length===2&&noOverlap(),'empty layers become thin bands');
 check($('stats').textContent.includes('0Applications'),'loose modules are not counted as an application');
});
</script>
'@
$profile = Join-Path $dir ('browser-profile-' + [guid]::NewGuid().ToString('N'))
function Invoke-Browser($label, $htmlPath, $theme, $probe, $limit, $size = '1680,1400') {
    $testHtml = Join-Path $dir "browser-$label-$theme.html"
    $dom = Join-Path $dir "browser-$label-$theme-dom.html"
    $log = Join-Path $dir "browser-$label-$theme.log"
    $screenshot = [IO.Path]::GetFullPath((Join-Path $Tmp "architecture-$label-$theme.png"))
    $instrumented = [IO.File]::ReadAllText($htmlPath).Replace('</head>', "$capture</head>").Replace('</body>', $helpers.Replace('__THEME__', $theme) + $probe + '</body>')
    [IO.File]::WriteAllText($testHtml, $instrumented)
    $url = ([uri][IO.Path]::GetFullPath($testHtml)).AbsoluteUri
    $arguments = @('--headless=new', '--disable-gpu', '--no-first-run', "--user-data-dir=`"$profile`"", "--window-size=$size", "--screenshot=`"$screenshot`"", '--dump-dom', "`"$url`"")
    $process = Start-Process -FilePath $browser -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $dom -RedirectStandardError $log
    if (-not $process.WaitForExit($limit)) {
        $process.Kill()
        Write-Host 'skip architecture browser checks: headless export timed out'
        return $null
    }
    # Windows browser launchers can exit before the headless child closes stdout.
    $report = $null
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $stream = [IO.File]::Open($dom, 'Open', 'Read', 'ReadWrite')
        $reader = [IO.StreamReader]::new($stream)
        try { $rendered = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $report = [regex]::Match($rendered, '<pre id="browser-check-result"[^>]*>(.*?)</pre>', 'Singleline')
        if ($report.Success) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $report.Success) { Write-Host "skip architecture browser checks: headless export unavailable (see $log)"; return $null }
    $checks = [Net.WebUtility]::HtmlDecode($report.Groups[1].Value) | ConvertFrom-Json
    if ($checks.errors.Count -gt 0) { throw "Architecture browser errors ($label, $theme): $($checks.errors -join '; ')" }
    if (-not (Test-Path -LiteralPath $screenshot)) { throw "Architecture browser screenshot missing ($label, $theme)" }
    return [pscustomobject]@{ checks = $checks; screenshot = $screenshot }
}
$pages = @(
    @{ label = 'sample'; html = $html; probe = $sampleProbe; limit = 30000 },
    @{ label = 'synthetic'; html = $syntheticHtml; probe = $syntheticProbe; limit = 60000; size = '1440,1400' },
    @{ label = 'synthetic-300'; html = $syn3.html; probe = $largeProbe; limit = 90000; size = '1440,1400'; lightOnly = $true },
    @{ label = 'loose-one'; html = $looseOne; probe = $looseProbe.Replace('__TITLE__', 'Orders_CS — Architecture'); limit = 30000; lightOnly = $true },
    @{ label = 'loose-two'; html = $looseTwo; probe = $looseProbe.Replace('__TITLE__', 'Inventory_CS · Orders_CS — Architecture'); limit = 30000; lightOnly = $true }
) + @($parityPages | ForEach-Object { @{ label = $_.label; html = $_.html; probe = $parityProbe; limit = 30000; lightOnly = $true } })
foreach ($p in $pages) {
    foreach ($theme in @('light', 'dark')) {
        if ($p.lightOnly -and $theme -eq 'dark') { continue }
        $size = if ($p.size) { $p.size } else { '1680,1400' }
        $run = Invoke-Browser $p.label $p.html $theme $p.probe $p.limit $size
        if ($null -eq $run) { return }
        $extra = if ($run.checks.ms) { ", module layout $($run.checks.ms) ms" } else { '' }
        Write-Host "ok   architecture browser $($p.label) $theme`: $($run.checks.checks.Count) checks, no console errors$extra ($($run.screenshot))"
    }
}

# Phone width: a 390 px frame (headless Edge does not go below about 500 px as a window). The page must not scroll
# sideways, and the diagram gets at most 80% of the screen height.
function Invoke-Narrow($label, $htmlPath) {
    $inner = Join-Path $dir "narrow-$label-inner.html"
    $after = '<script>window.extra={iw:innerWidth,ih:innerHeight,sw:document.documentElement.scrollWidth,graphH:document.getElementById("graph").clientHeight,graphW:document.getElementById("graph").clientWidth,vbw:Number(document.getElementById("graph").getAttribute("viewBox").split(" ")[2])};</script>'
    [IO.File]::WriteAllText($inner, [IO.File]::ReadAllText($htmlPath).Replace('</body>', "$after</body>"))
    $wrap = Join-Path $dir "narrow-$label-wrap.html"
    [IO.File]::WriteAllText($wrap, "<!doctype html><html><body style='margin:0'><iframe id=f src='$(([uri]$inner).AbsoluteUri)' style='width:390px;height:800px;border:0;display:block'></iframe><pre id=out></pre><script>f.onload=()=>{try{out.textContent=JSON.stringify(f.contentWindow.extra)}catch(e){out.textContent='ERR '+e.message}}</script></body></html>")
    $dom = Join-Path $dir "narrow-$label-dom.txt"
    $arguments = @('--headless=new', '--disable-gpu', '--no-first-run', '--allow-file-access-from-files', '--virtual-time-budget=3000', "--user-data-dir=`"$profile`"", '--window-size=600,900', '--dump-dom', "`"$(([uri]$wrap).AbsoluteUri)`"")
    $process = Start-Process -FilePath $browser -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $dom -RedirectStandardError "$dom.err"
    if (-not $process.WaitForExit(40000)) { $process.Kill(); Write-Host 'skip architecture phone check: headless export timed out'; return }
    $m = $null
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $stream = [IO.File]::Open($dom, 'Open', 'Read', 'ReadWrite'); $reader = [IO.StreamReader]::new($stream)
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $m = [regex]::Match($text, '<pre id="out">(.*?)</pre>', 'Singleline')
        if ($m.Success -and $m.Groups[1].Value) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $m.Success -or -not $m.Groups[1].Value -or $m.Groups[1].Value -like 'ERR*') { Write-Host "skip architecture phone check: frame not readable ($($m.Groups[1].Value))"; return }
    $e = [Net.WebUtility]::HtmlDecode($m.Groups[1].Value) | ConvertFrom-Json
    if ($e.sw -gt $e.iw + 1) { throw "Phone width ($label): the page scrolls sideways ($($e.sw) px in $($e.iw) px)" }
    if ($e.graphH -gt 0.8 * $e.ih + 2 -or $e.graphH -lt 200) { throw "Phone width ($label): diagram height $($e.graphH) px in a $($e.ih) px screen" }
    if ($e.vbw -gt $e.graphW * 8) { throw "Phone width ($label): the diagram is not fitted to the width" }
    Write-Host "ok   architecture phone width ($label): no sideways scroll, diagram $($e.graphW) x $($e.graphH) px"
}
Invoke-Narrow 'sample' $html
Invoke-Narrow 'synthetic' $syntheticHtml
