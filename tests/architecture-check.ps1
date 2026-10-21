param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $Tmp ('architecture-' + [guid]::NewGuid().ToString('N'))
$inputs = Join-Path $dir 'input'
New-Item -ItemType Directory $inputs -Force | Out-Null
function Run-ArchitectureCommand([string[]]$Arguments) {
    $result = & $Oml @Arguments | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "oml $($Arguments -join ' '): $($result | ConvertTo-Json -Depth 12 -Compress)" }
    return $result
}
function Check-Architecture($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function New-ArchitectureModule($Name, $Spec, $Output) {
    $blank = Join-Path $dir ($Name + '-blank.oml')
    Run-ArchitectureCommand @('new', $blank, '--name', $Name, '--kind', 'CrossDevice') | Out-Null
    return Apply-ArchitectureModule $blank $Spec $Output
}
function Apply-ArchitectureModule($Source, $Spec, $Output) {
    $specFile = Join-Path $dir 'spec.json'
    $Spec | ConvertTo-Json -Depth 40 | Set-Content $specFile
    $r = Run-ArchitectureCommand @('apply', $Source, $specFile, $Output)
    Check-Architecture ($r.saved -and $r.reopenNewErrors.Count -eq 0) "Fixture did not save: $($r | ConvertTo-Json -Depth 10)"
    return $Output
}
$th = New-ArchitectureModule 'Shop_Th' @{
    themes = @(@{name='ShopTheme';styleSheet='.shop {color:blue;}'})
    blocks = @(@{name='Header'})
    roles = @(@{name='ShopViewer';public=$true})
    edits = @(@{op='set';element='ShopTheme';properties=@{public=$true}})
} (Join-Path $inputs 'Shop_Th.oml')
$lib0 = New-ArchitectureModule 'Shop_Lib' @{serverActions=@(@{name='Utility';public=$true;flow=@()})} (Join-Path $dir 'lib0.oml')
$cs = New-ArchitectureModule 'Shop_CS' @{
    entities = @(@{name='Customer';public=$true;attributes=@(@{name='Name'})},@{name='Order';public=$true;attributes=@(@{name='CustomerId';type='Customer Identifier'})})
    staticEntities = @(@{name='Status';public=$true})
    structures = @(@{name='OrderDto';public=$true;attributes=@(@{name='Value'})})
    serverActions = @(@{name='PlaceOrder';public=$true;flow=@()},@{name='Unused';public=$true;flow=@()})
    clientActions = @(@{name='ClientHello';public=$true;flow=@()})
    references = @(@{producer=$lib0;elements=@('Utility')})
    screens = @(@{name='MisplacedScreen'})
    restClients = @(@{name='Weather';baseUrl='https://example.test';actions=@(@{name='Forecast';url='/forecast'})})
    restServices = @(@{name='OrdersApi';actions=@(@{name='ListOrders';url='/orders'})})
} (Join-Path $dir 'Shop_CS.oml')
$lib = Apply-ArchitectureModule $lib0 @{
    references=@(@{producer=$cs;elements=@('PlaceOrder')})
    entities=@(@{name='MisplacedEntity';attributes=@(@{name='Value'})})
} (Join-Path $inputs 'Shop_Lib.oml')
$cw = New-ArchitectureModule 'Shop_UI' @{
    references=@(@{producer=$cs;elements=@('Customer','Status','OrderDto','PlaceOrder','ClientHello')},@{producer=$th;elements=@('ShopTheme','ShopViewer')})
    screens=@(@{name='Orders';widgets=@(@{type='Text';text='Orders'})})
    blocks=@(@{name='OrderCard'})
    edits=@(@{op='set';element='OrderCard';properties=@{public=$true}},@{op='set';element='Orders';properties=@{public=$true}})
} (Join-Path $dir 'Shop_UI.oml')
$admin = New-ArchitectureModule 'Admin_UI' @{references=@(@{producer=$cw;elements=@('OrderCard')});screens=@(@{name='Admin';flow='AdminFlow'})} (Join-Path $inputs 'Admin_UI.oml')
# A screen destination is a weak link between End-user modules, not a side reference.
$portal = New-ArchitectureModule 'Portal_UI' @{references=@(@{producer=$cw;elements=@('Orders')});screens=@(@{name='Portal';flow='PortalFlow'})} (Join-Path $inputs 'Portal_UI.oml')
# Entities, static entities and structures alone are a weak dependency.
$large = New-ArchitectureModule 'Large_CS' @{references=@(@{producer=$cs;elements=@('Customer','Status','OrderDto')});serverActions=@(1..151 | ForEach-Object {@{name="Action$_";flow=@()}})} (Join-Path $inputs 'Large_CS.oml')
Run-ArchitectureCommand @('app','pack',(Join-Path $inputs 'shop.oap'),'--name','Shop',$cs,$cw) | Out-Null
# A loose copy must still get its package application, and must only appear once.
Copy-Item $cs (Join-Path $inputs 'Shop_CS.oml')
[IO.File]::WriteAllText((Join-Path $inputs 'unreadable.oml'), 'not a module')
$hashes = @{}; Get-ChildItem $inputs -File | ForEach-Object { $hashes[$_.FullName] = (Get-FileHash $_.FullName).Hash }
$html = Join-Path $dir 'architecture.html'; $json = Join-Path $dir 'architecture.json'
$beforeTemps = @(Get-ChildItem $env:TEMP -Directory -Filter 'oml-architecture-*' | ForEach-Object FullName)
$report = Run-ArchitectureCommand @('architecture','--in',$inputs,'--out',$html,'--json',$json,'--team-label','Shop Team')
$data = Get-Content $json -Raw | ConvertFrom-Json
Check-Architecture ($data.schema -eq 1 -and $data.generatedAt -and $data.omlVersion -and $data.input.team -eq 'Shop Team') 'Schema header or team label'
Check-Architecture ($data.layout.crossings -is [long] -and $data.layout.crossings -ge 0 -and $data.layout.ranks -ge 3) 'Layout object (crossings, ranks)'
Check-Architecture (@($data.modules | Where-Object {-not $_.external}).Count -eq 7 -and $data.applications.Count -eq 2) 'Module/application totals'
Check-Architecture (($data.applications | Where-Object name -eq Shop).modules.Count -eq 2) 'Package application membership'
Check-Architecture ($report.warnings -join ' ' -match 'unreadable.oml' -and $report.warnings -join ' ' -match 'duplicate module') 'Unreadable or duplicate warning'
foreach ($name in @('Shop_Th','Shop_Lib','Shop_CS','Shop_UI','Admin_UI','Portal_UI','Large_CS')) {
    $m = $data.modules | Where-Object name -eq $name
    $expected = if ($name -match '_UI$') {'EndUser'} elseif ($name -match '_CS$') {'Core'} else {'Foundation'}
    Check-Architecture ($m.layer -eq $expected -and $m.layerReason -like 'suffix *') "Layer or reason for $name"
    foreach ($field in @('name','application','external','kind','layer','layerReason','description','counts','public','entities','screens','restExposed','restConsumed','timers','processes')) {
        Check-Architecture ($m.PSObject.Properties.Name -contains $field) "Missing module field $name.$field"
    }
    foreach ($field in @('entities','staticEntities','structures','serverActions','serviceActions','clientActions','screens','blocks','restExposed','restConsumed','timers','processes','siteProperties','roles')) {
        Check-Architecture ($m.counts.PSObject.Properties.Name -contains $field -and $m.counts.$field -is [long]) "Missing/noninteger count $name.$field"
    }
    foreach ($field in @('entities','actions','blocks','structures','staticEntities')) { Check-Architecture ($m.public.PSObject.Properties.Name -contains $field) "Missing public field $name.$field" }
}
$core = $data.modules | Where-Object name -eq Shop_CS
Check-Architecture ($core.counts.entities -eq 2 -and $core.counts.staticEntities -eq 1 -and $core.counts.screens -eq 1) 'Own content counts'
Check-Architecture (($core.entities | Where-Object name -eq Order).references -contains 'Customer') 'Entity foreign key'
Check-Architecture ($core.restExposed[0].methods -eq 1 -and $core.restConsumed[0].baseUrl -eq 'https://example.test') 'REST metadata'
$edge = $data.edges | Where-Object { $_.from -eq 'Shop_UI' -and $_.to -eq 'Shop_CS' }
Check-Architecture $edge.strong 'Data/logic dependency should be strong'
foreach ($kind in @('Entity','StaticEntity','Structure','ServerAction','ClientAction')) { Check-Architecture ($edge.elements.kind -contains $kind) "Missing reference kind $kind" }
$themed = $data.edges | Where-Object { $_.from -eq 'Shop_UI' -and $_.to -eq 'Shop_Th' }
Check-Architecture ($themed.strong -and $themed.elements.kind -contains 'Theme' -and $themed.elements.kind -contains 'Role' -and $themed.elements.kind -notcontains 'ServerAction') 'Theme and role reference should be strong, without generated role helper actions'
$weak = $data.edges | Where-Object { $_.from -eq 'Large_CS' -and $_.to -eq 'Shop_CS' }
Check-Architecture (-not $weak.strong -and (@($weak.elements.kind | Sort-Object -Unique) -join ',') -eq 'Entity,StaticEntity,Structure') "Entity/structure-only reference should be weak: $($weak | ConvertTo-Json -Depth 5 -Compress)"
$side = $data.edges | Where-Object { $_.from -eq 'Admin_UI' -and $_.to -eq 'Shop_UI' }
Check-Architecture ($side.strong -and $side.elements.kind -contains 'Block') 'Block reference should be strong'
$screenOnly = $data.edges | Where-Object { $_.from -eq 'Portal_UI' -and $_.to -eq 'Shop_UI' }
Check-Architecture (-not $screenOnly.strong -and (@($screenOnly.elements.kind | Sort-Object -Unique) -join ',') -eq 'Screen') "Screen-only reference should be weak: $($screenOnly | ConvertTo-Json -Depth 5 -Compress)"
Check-Architecture (@($data.findings | Where-Object { $_.id -eq 'side-reference-enduser' -and $_.modules -contains 'Portal_UI' }).Count -eq 0) 'A screen-only End-user reference must not be a side reference'
Check-Architecture (@($data.findings | Where-Object { $_.id -eq 'side-reference-enduser' -and $_.modules -contains 'Admin_UI' }).Count -eq 1) 'A block End-user reference must be a side reference'
foreach ($id in @('upward-reference','side-reference-enduser','cycle','core-ui','foundation-business','unused-public','large-module','missing-description')) {
    $found = @($data.findings | Where-Object id -eq $id)
    Check-Architecture ($found.Count -gt 0) "Missing finding $id"
    foreach ($f in $found) { Check-Architecture ($f.severity -in @('high','medium','low') -and $f.title -and $f.modules.Count -gt 0 -and $f.detail -and $f.fix) "Finding schema $id" }
}
Check-Architecture (@($data.findings | Where-Object { $_.id -eq 'missing-description' -and $_.modules -contains 'Shop_CS' }).Count -eq 1) 'Descriptions should be counted once per module'
$page = Get-Content $html -Raw
Check-Architecture ($page -match '<script type="application/json" id="oml-architecture">([\s\S]*?)</script>') 'Embedded JSON script'
$embedded = $Matches[1] | ConvertFrom-Json
Check-Architecture (($embedded | ConvertTo-Json -Depth 80 -Compress) -eq ($data | ConvertTo-Json -Depth 80 -Compress)) 'HTML and file JSON differ'
$filteredJson = Join-Path $dir 'filtered.json'
Run-ArchitectureCommand @('architecture','--in',$inputs,'--app','Shop','--out',(Join-Path $dir 'filtered.html'),'--json',$filteredJson) | Out-Null
$filtered = Get-Content $filteredJson -Raw | ConvertFrom-Json
Check-Architecture (@($filtered.modules | Where-Object {-not $_.external}).Count -eq 2 -and $filtered.applications.Count -eq 1) 'Application filter'
foreach ($name in @('Shop_Th','Shop_Lib')) {
    $external = $filtered.modules | Where-Object name -eq $name
    Check-Architecture ($external.external -and $external.application -eq '(external)' -and @($external.PSObject.Properties).Count -eq 5) "External producer $name"
}
$layers = Join-Path $dir 'layers.json'; '{"Shop*":"Core","Shop_UI":"Foundation"}' | Set-Content $layers
$overrideJson = Join-Path $dir 'override.json'
Run-ArchitectureCommand @('architecture','--in',$inputs,'--layers',$layers,'--out',(Join-Path $dir 'override.html'),'--json',$overrideJson) | Out-Null
$overridden = Get-Content $overrideJson -Raw | ConvertFrom-Json
Check-Architecture (($overridden.modules | Where-Object name -eq Shop_UI).layer -eq 'Foundation') 'Exact layer override'
Check-Architecture (($overridden.modules | Where-Object name -eq Shop_Th).layer -eq 'Core' -and ($overridden.modules | Where-Object name -eq Shop_Th).layerReason -eq 'override') 'Wildcard layer override'
$originalHash = (Get-FileHash $html).Hash
$refused = & $Oml architecture --in $inputs --out $html | ConvertFrom-Json
Check-Architecture ($LASTEXITCODE -eq 2 -and $refused.error -like 'Output already exists:*' -and (Get-FileHash $html).Hash -eq $originalHash) 'HTML overwrite refusal'
$newHtml = Join-Path $dir 'must-not-write.html'
$refused = & $Oml architecture --in $inputs --out $newHtml --json $json | ConvertFrom-Json
Check-Architecture ($LASTEXITCODE -eq 2 -and -not (Test-Path $newHtml)) 'JSON overwrite must refuse before writing HTML'
foreach ($file in $hashes.Keys) { Check-Architecture ((Get-FileHash $file).Hash -eq $hashes[$file]) "Input was changed: $file" }
$afterTemps = @(Get-ChildItem $env:TEMP -Directory -Filter 'oml-architecture-*' | ForEach-Object FullName)
Check-Architecture (($afterTemps | Where-Object { $_ -notin $beforeTemps }).Count -eq 0) 'Package temp directory leaked'
# Read packaged extensions through the existing extension loader as well.
$xif = Join-Path $dir 'Native.xif'
Run-ArchitectureCommand @('extension','new',$xif,'--name','Native_CS') | Out-Null
$extensionPackage = Join-Path $dir 'native.oap'
Run-ArchitectureCommand @('app','pack',$extensionPackage,'--name','Native',$xif) | Out-Null
$extensionJson = Join-Path $dir 'native.json'
Run-ArchitectureCommand @('architecture','--in',$extensionPackage,'--in',$extensionPackage,'--app','Native','--app','Shop','--out',(Join-Path $dir 'native.html'),'--json',$extensionJson) | Out-Null
$extensionData = Get-Content $extensionJson -Raw | ConvertFrom-Json
Check-Architecture ($extensionData.modules.Count -eq 1 -and $extensionData.modules[0].kind -eq 'Extension' -and $extensionData.modules[0].layer -eq 'Foundation' -and $extensionData.modules[0].application -eq 'Native') 'Packaged extension, repeated inputs or repeated app filters'
Write-Host "ok   architecture: layers, grouping, edges, eight findings, filtering, overrides, schema, overwrite; $($report.elapsedSeconds)s for seven modules"
