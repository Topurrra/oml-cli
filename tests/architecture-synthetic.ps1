# Writes a synthetic architecture document (schema 1): per copy 92 input modules in 16 applications, 8 external
# modules in all. -Copies 1 gives the 100-module sample, -Copies 3 about 300 modules. It is deterministic (own random
# generator), so layout numbers can be compared between runs. Used by architecture-page-check.ps1. Not real data.
param([Parameter(Mandatory)][string]$Out, [int]$Copies = 1)
$ErrorActionPreference = 'Stop'
$script:state = [long]12345
function Next-Number([int]$Max) { $script:state = ($script:state * 1103515245 + 12345) % 2147483648; return [int](($script:state / 65536) % $Max) }
$strong = @('ServerAction', 'ClientAction', 'Block', 'Theme', 'Script', 'Image', 'Resource', 'Role', 'Process')
function New-Module($name, $app, $layer, $kind) {
    $screens = if ($layer -eq 'EndUser') { 4 } else { 0 }
    $entities = if ($layer -eq 'Core') { 6 } else { 0 }
    [ordered]@{
        name = $name; application = $app; external = $false; kind = $kind; layer = $layer; layerReason = 'suffix'; description = "Synthetic module $name."
        counts = [ordered]@{ entities = $entities; staticEntities = 1; structures = 2; serverActions = 12; serviceActions = 0; clientActions = 3; screens = $screens; blocks = 2; restExposed = 0; restConsumed = 0; timers = 0; processes = 0; siteProperties = 1; roles = 1 }
        public = [ordered]@{ entities = @(); actions = @("Action_$name"); blocks = @(); structures = @(); staticEntities = @() }
        entities = @(); screens = @(1..$screens | ForEach-Object { "Screen$_" }); restExposed = @(); restConsumed = @(); timers = @(); processes = @()
    }
}
$mods = New-Object System.Collections.ArrayList
$copyOf = @{}
for ($c = 0; $c -lt $Copies; $c++) {
    # 16 applications that keep to one layer, as good practice asks: 5 End-user, 6 Core, 5 Foundation.
    for ($a = 1; $a -le 16; $a++) {
        $app = 'App{0:D2}' -f ($c * 16 + $a)
        $plan = if ($a -le 5) { @('UI', 'Portal', 'App', 'Web', 'Mobile') | ForEach-Object { , @($_, 'EndUser', 'Reactive') } }
        elseif ($a -le 11) { @('CS', 'BL', 'API', 'Svc', 'Data', 'IS', 'Core') | ForEach-Object { , @($_, 'Core', 'Service') } }
        else { @('Lib', 'Th', 'Util', 'Ext', 'Drv') | ForEach-Object { , @($_, 'Foundation', 'Library') } }
        foreach ($p in $plan) { $name = "${app}_$($p[0])"; [void]$mods.Add((New-Module $name $app $p[1] $p[2])); $copyOf[$name] = $c }
    }
}
$platform = @('OutSystemsUI', 'System', 'Users', 'OutSystemsCharts', 'RichWidgets', 'Excel_Ext', 'Mailer.xif', 'Crypto.xif')
foreach ($p in $platform) { [void]$mods.Add([ordered]@{ name = $p; application = '(external)'; external = $true; layer = 'Foundation'; layerReason = 'content: platform module' }) }
function Layer-List($copy, $layer) { return @($mods | Where-Object { -not $_.external -and $_.layer -eq $layer -and $copyOf[$_.name] -eq $copy } | ForEach-Object { $_.name }) }
$edges = New-Object System.Collections.ArrayList; $seen = @{}
function Add-Edge($from, $to) {
    if ($from -eq $to -or $seen.ContainsKey("$from>$to")) { return }
    $seen["$from>$to"] = 1
    $kinds = @('ServerAction', 'Entity', 'Block', 'Structure', 'Screen', 'ServiceAction', 'Theme', 'StaticEntity', 'ClientAction')
    $count = 1 + (Next-Number 3)
    $elements = @(1..$count | ForEach-Object { $k = $kinds[(Next-Number $kinds.Count)]; [ordered]@{ kind = $k; name = "${k}_$_" } })
    $isStrong = [bool]@($elements | Where-Object { $strong -contains $_.kind }).Count
    [void]$edges.Add([ordered]@{ from = $from; to = $to; strong = $isStrong; elements = $elements })
}
function Pick($list) { return $list[(Next-Number $list.Count)] }
$ext = @($platform)
for ($c = 0; $c -lt $Copies; $c++) {
    $endUser = Layer-List $c 'EndUser'; $core = Layer-List $c 'Core'; $foundation = Layer-List $c 'Foundation'
    foreach ($m in $endUser) {
        1..(2 + (Next-Number 3)) | ForEach-Object { Add-Edge $m (Pick $core) }
        Add-Edge $m (Pick $foundation)
        Add-Edge $m 'OutSystemsUI'
        if ((Next-Number 100) -lt 20) { Add-Edge $m (Pick $endUser) }
    }
    for ($i = 0; $i -lt $core.Count; $i++) {
        if ($i -gt 0) { 1..(1 + (Next-Number 2)) | ForEach-Object { Add-Edge $core[$i] $core[(Next-Number $i)] } }
        1..(1 + (Next-Number 2)) | ForEach-Object { Add-Edge $core[$i] (Pick $foundation) }
        if ((Next-Number 100) -lt 30) { Add-Edge $core[$i] (Pick $ext) }
    }
    for ($i = 0; $i -lt $foundation.Count; $i++) {
        if ($i -gt 0 -and (Next-Number 100) -lt 60) { Add-Edge $foundation[$i] $foundation[(Next-Number $i)] }
        if ((Next-Number 100) -lt 50) { Add-Edge $foundation[$i] (Pick $ext) }
    }
    # A few dependencies reach into the previous copy, as a larger tenant would have.
    if ($c -gt 0) { 1..5 | ForEach-Object { Add-Edge (Pick $core) (Pick (Layer-List ($c - 1) 'Foundation')) } }
}
$endUser = Layer-List 0 'EndUser'; $core = Layer-List 0 'Core'; $foundation = Layer-List 0 'Foundation'
# Planted problems (copy 0): cycles between Core modules, upward references, a side reference between End-user modules.
$findings = New-Object System.Collections.ArrayList
function Add-Finding($id, $severity, $title, $names, $detail, $fix) { [void]$findings.Add([ordered]@{ id = $id; severity = $severity; title = $title; modules = @($names); detail = $detail; fix = $fix }) }
foreach ($i in 10, 20, 30) { Add-Edge $core[$i - 4] $core[$i]; Add-Edge $core[$i] $core[$i - 4]; Add-Finding 'cycle' 'high' 'Modules form a dependency cycle' @($core[$i - 4], $core[$i]) "Dependency cycle: $($core[$i - 4]) to $($core[$i])." 'Move shared contracts lower.' }
foreach ($i in 3, 9) { Add-Edge $foundation[$i] $core[$i]; Add-Finding 'upward-reference' 'high' 'A module references a higher layer' @($foundation[$i], $core[$i]) "$($foundation[$i]) references $($core[$i])." 'Move the shared contract lower.' }
$eu = $endUser; Add-Edge $eu[0] $eu[1]
$side = $edges | Where-Object { $_.from -eq $eu[0] -and $_.to -eq $eu[1] }; $side.strong = $true; $side.elements = @([ordered]@{ kind = 'Block'; name = 'Card' })
Add-Finding 'side-reference-enduser' 'high' 'EndUser modules reference each other' @($eu[0], $eu[1]) "$($eu[0]) uses elements from $($eu[1])." 'Move the shared elements into a Core module.'
Add-Finding 'core-ui' 'medium' 'A Core module contains screens' @($core[2]) 'Core screens couple logic to a UI.' 'Move screens to EndUser.'
Add-Finding 'large-module' 'low' 'A module has more than 150 actions, screens and entities' @($core[5]) 'Large.' 'Split it.'
Add-Finding 'missing-description' 'low' 'Public elements have no description' @($foundation[1]) 'Missing descriptions.' 'Describe them.'
$appList = @($mods | Where-Object { -not $_.external } | Group-Object { $_.application } | Sort-Object Name | ForEach-Object { [ordered]@{ name = $_.Name; modules = @($_.Group | ForEach-Object { $_.name } | Sort-Object) } })
$doc = [ordered]@{
    schema = 1; generatedAt = '2026-01-01T00:00:00Z'; omlVersion = '0.0.0'; input = [ordered]@{ sources = @('synthetic'); apps = @(); team = $null }
    applications = $appList; modules = @($mods | Sort-Object { $_.name }); edges = @($edges | Sort-Object { $_.from }, { $_.to }); findings = @($findings)
}
[IO.File]::WriteAllText($Out, ($doc | ConvertTo-Json -Depth 12))
