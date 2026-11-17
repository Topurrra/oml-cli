# Checks `oml export`: a Traditional screen and block of tests/web.json and a Reactive screen of tests/logic.json as
# structured JSON (element facts, flows in the spec's format, the widget tree), several names, a bad name, a step of
# `oml batch`, and the otherContent section of `oml inspect`.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Tmp = New-TestFolder $Tmp 'export'
try {
function Require($condition, $message) { if (!$condition) { throw $message } }
function Read-Oml([string[]]$cliArgs) {
    $text = & $Oml @cliArgs
    Require ($LASTEXITCODE -eq 0) "Command failed: $cliArgs $text"
    return (($text -join "`n") | ConvertFrom-Json)
}
function Has($object, [string]$key) { return $null -ne $object -and $null -ne $object.PSObject.Properties[$key] }
# Every widget of a tree: children, If branches, table header and row, placeholders.
function All-Widgets($list) {
    foreach ($w in @($list)) {
        if ($null -eq $w) { continue }
        $w
        foreach ($key in 'children', 'true', 'false', 'header', 'row') { if (Has $w $key) { All-Widgets $w.$key } }
        if (Has $w 'placeholders') { foreach ($slot in $w.placeholders.PSObject.Properties) { All-Widgets $slot.Value } }
    }
}
# True when one of the widget's properties holds this expression (the property's name depends on the widget kind).
function Shows($widget, [string]$expression) {
    return (Has $widget 'props') -and @($widget.props.PSObject.Properties | Where-Object { $_.Value -ceq $expression }).Count -gt 0
}

# Traditional: tests/web.json on a blank WebResponsive module.
$blank = Join-Path $Tmp 'export-blank.oml'
$web = Join-Path $Tmp 'export-web.oml'
Read-Oml @('new',$blank,'--kind','WebResponsive','--name','ExportWeb') | Out-Null
$r = Read-Oml @('apply',$blank,(Join-Path $PSScriptRoot 'web.json'),$web)
Require ($r.saved -and !$r.newErrors -and !$r.reopenNewErrors) 'export fixture did not save cleanly'

$e = Read-Oml @('export',$web,'WebItems')
Require ($e.kind -eq 'Screen' -and $e.name -eq 'WebItems' -and $e.flow -eq 'MainFlow' -and $e.path -eq 'MainFlow/WebItems' -and $e.moduleKind -eq 'Traditional') "element facts: $($e | ConvertTo-Json -Depth 3 -Compress)"
Require ($e.title -eq '"Web items"') "title: $($e.title)"
$locals = @($e.locals)
Require (($locals | Where-Object name -eq 'ItemName').type -eq 'Text' -and ($locals | Where-Object name -eq 'Item').type -eq 'WebItem') "locals: $($e.locals | ConvertTo-Json -Compress)"
Require ((Has $e 'access') -and (Has $e 'widgets') -and (Has $e 'actions')) 'element keys'

# Preparation: one aggregate node in the spec's format, ending at End.
$prep = @($e.preparation.nodes)
Require ($prep.Count -eq 1 -and $prep[0].kind -eq 'aggregate' -and $prep[0].name -eq 'FetchItems' -and @($prep[0].sources) -contains 'WebItem' -and $prep[0].maxRecords -eq '10' -and $prep[0].next -eq 'End') "preparation: $($e.preparation | ConvertTo-Json -Depth 6 -Compress)"
Require ($e.preparation.complete -eq $true -and $prep[0]._path.StartsWith('MainFlow/WebItems/')) "preparation complete and _path: $($e.preparation | ConvertTo-Json -Depth 6 -Compress)"

# Screen action Save: assign, then the entity action, both in the spec's format.
$save = @($e.actions) | Where-Object name -eq 'Save'
$nodes = @($save.flow.nodes)
Require ($nodes.Count -eq 2 -and $nodes[0].kind -eq 'assign' -and $nodes[0].name -eq 'SetName' -and $nodes[0].assign.'Item.Name' -eq 'ItemName') "Save assign: $($save.flow | ConvertTo-Json -Depth 6 -Compress)"
$exec = $nodes[1]
Require ($exec.kind -eq 'execute' -and $exec.action -eq 'CreateOrUpdateWebItem' -and $exec._ref -eq 'CreateOrUpdateWebItem' -and $exec.args.Source -eq 'Item' -and $exec.next -eq 'End') "Save execute: $($exec | ConvertTo-Json -Depth 4 -Compress)"
Require ($nodes[0].next -eq $exec.name -and $save.flow.complete -eq $true) "Save connectors and complete: $($save.flow | ConvertTo-Json -Depth 6 -Compress)"

# The widget tree: names, properties, block instance, If branches, table header and row, link arguments.
$all = @(All-Widgets $e.widgets)
$editor = $all | Where-Object name -eq 'Editor'
Require ($editor -and (@($editor.children | ForEach-Object name) -join ',') -eq 'NameInput,SaveButton' -and ((Shows $editor '"editor"') -or $editor.props.style -eq 'editor'))"Editor: $($editor | ConvertTo-Json -Depth 5 -Compress)"
Require (Shows ($all | Where-Object name -eq 'NameInput') 'ItemName') 'NameInput variable'
Require (($all | Where-Object name -eq 'SaveButton').props.onClick -eq 'Save') 'SaveButton onClick'
$summary = $all | Where-Object name -eq 'Summary'
Require ($summary.sourceBlock -eq 'WebCard' -and $summary.args.Caption -eq '"Summary"') "Summary: $($summary | ConvertTo-Json -Depth 5 -Compress)"
$if = @($all | Where-Object { (Has $_ 'true') -or (Has $_ 'false') })
Require ($if.Count -eq 1 -and (Shows $if[0] 'not FetchItems.List.Empty') -and @($if[0].false).Count -ge 1) "If: $($if | ConvertTo-Json -Depth 4 -Compress)"
$table = $all | Where-Object name -eq 'ItemsTable'
Require ($table -and (Has $table 'header') -and (Has $table 'row') -and (Shows $table 'FetchItems.List')) "ItemsTable: $($table | ConvertTo-Json -Depth 8 -Compress)"
$link = $all | Where-Object name -eq 'OpenDetail'
Require ($link.props.onClick -eq 'WebDetail' -and $link.props.destination -eq 'WebDetail' -and $link.args.ItemId -eq 'FetchItems.List.Current.WebItem.Id') "OpenDetail: $($link | ConvertTo-Json -Depth 5 -Compress)"
Require (@(All-Widgets $table.row) | Where-Object name -eq 'OpenDetail') 'the link is in the table row'
foreach ($w in $all) { Require ($w.kind -and $w.path.StartsWith('MainFlow/WebItems/')) "widget kind and path: $($w | ConvertTo-Json -Depth 2 -Compress)" }

# A block, by Kind:Name: its preparation assign.
$block = Read-Oml @('export',$web,'Block:WebCard')
Require ($block.kind -eq 'Block' -and $block.flow -eq 'Common' -and @($block.preparation.nodes)[0].kind -eq 'assign' -and @($block.preparation.nodes)[0].assign.Ready -eq 'True') "block: $($block | ConvertTo-Json -Depth 6 -Compress)"

# Several names: an array in the order asked; a bad name gives an error entry in its place and exit code 1.
$text = & $Oml export $web WebDetail NoSuchScreen WebItems
Require ($LASTEXITCODE -eq 1) "export with a bad name: exit $LASTEXITCODE, expected 1"
$many = @(($text -join "`n") | ConvertFrom-Json)
Require ($many.Count -eq 3 -and $many[0].name -eq 'WebDetail' -and $many[1].element -eq 'NoSuchScreen' -and $many[1].error -and $many[2].name -eq 'WebItems') "several names: $($text -join ' ')"
Require (@($many[0].inputs)[0].name -eq 'ItemId' -and @($many[0].inputs)[0].type -eq 'WebItem Identifier') 'WebDetail input'
$text = & $Oml export $web WebItem
Require ($LASTEXITCODE -eq 1 -and (($text -join "`n") | ConvertFrom-Json).error -match 'screens, blocks and emails') "export of an entity: exit $LASTEXITCODE $text"
$text = & $Oml export $web WebItems WebDetail
Require ($LASTEXITCODE -eq 0 -and @(($text -join "`n") | ConvertFrom-Json).Count -eq 2) 'two good names'

# A step of `oml batch`.
$steps = Join-Path $Tmp 'export-steps.json'
Set-Content $steps ('{ "steps": [["export", ' + ($web | ConvertTo-Json) + ', "WebDetail"]] }') -Encoding utf8
$run = Read-Oml @('batch',$steps)
Require ($run.results[0].exitCode -eq 0 -and $run.results[0].output.kind -eq 'Screen' -and $run.results[0].output.name -eq 'WebDetail') "batch step: $($run | ConvertTo-Json -Depth 4 -Compress)"

# Reactive: tests/logic.json on a blank CrossDevice module.
$rblank = Join-Path $Tmp 'export-reactive-blank.oml'
$reactive = Join-Path $Tmp 'export-reactive.oml'
Read-Oml @('new',$rblank,'--kind','CrossDevice','--name','ExportReactive') | Out-Null
$r = Read-Oml @('apply',$rblank,(Join-Path $PSScriptRoot 'logic.json'),$reactive)
Require ($r.saved) 'Reactive fixture did not save'
$o = Read-Oml @('export',$reactive,'Orders')
Require ($o.kind -eq 'Screen' -and $o.moduleKind -eq 'Reactive' -and $null -eq $o.preparation -and $o.onInitialize -eq 'Init') "Reactive element: $($o | ConvertTo-Json -Depth 3 -Compress)"
$agg = @($o.aggregates) | Where-Object name -eq 'GetOrders'
Require ($agg -and @($agg.sources) -contains 'Order' -and @($agg.filters) -contains 'Order.CustomerId = CustomerId' -and $agg.maxRecords -eq '50') "Reactive aggregate: $($agg | ConvertTo-Json -Depth 4 -Compress)"
$call = @((@($o.actions) | Where-Object name -eq 'Save').flow.nodes)[0]
Require ($call.kind -eq 'execute' -and $call.action -eq 'CreateOrderFor' -and $call.args.Total -eq 'NewTotal' -and $call.next -eq 'End') "Reactive execute: $($call | ConvertTo-Json -Depth 4 -Compress)"
$rall = @(All-Widgets $o.widgets)
Require ((Shows ($rall | Where-Object name -eq 'TotalInput') 'NewTotal') -and ($rall | Where-Object name -eq 'SaveButton').props.onClick -eq 'Save') 'Reactive widgets'
$hasOrders = $rall | Where-Object name -eq 'HasOrders'
Require ($hasOrders -and @(All-Widgets $hasOrders.true | Where-Object name -eq 'OrdersTable').Count -eq 1) "Reactive If branches: $($hasOrders | ConvertTo-Json -Depth 4 -Compress)"

# inspect: otherContent lists what no other section lists, here the UI flow's exception action.
$handled = Join-Path $Tmp 'export-web-handled.oml'
$handler = Join-Path $Tmp 'export-onexception.json'
Set-Content $handler '{ "onException": { "name": "OnException", "flow": "MainFlow", "nodes": [ { "kind": "exceptionHandler", "name": "OnAllErrors", "exception": "AllExceptions", "logError": true }, { "kind": "destination", "screen": "WebItems" } ] } }' -Encoding utf8
$r = Read-Oml @('apply',$web,$handler,$handled)
Require ($r.saved) 'onException fixture did not save'
$i = Read-Oml @('inspect',$handled,'--pretty')
Require (Has $i 'otherContent') 'inspect has no otherContent'
$listed = @($i.screens | ForEach-Object name) + @($i.blocks | ForEach-Object name) + @($i.entities | ForEach-Object name)
foreach ($row in @($i.otherContent)) { Require ($row.kind -and $row.name -and $row.name -notin $listed) "otherContent lists an element another section lists: $($row | ConvertTo-Json -Compress)" }
Require (@($i.otherContent | Where-Object name -eq 'OnException').Count -eq 1) "otherContent misses the exception action: $($i.otherContent | ConvertTo-Json -Compress)"
$only = Read-Oml @('inspect',$handled,'--only','otherContent','--pretty')
Require (@($only.PSObject.Properties.Name | Where-Object { $_ -ne 'unreadable' }) -join ',' -eq 'otherContent') "inspect --only otherContent: $($only.PSObject.Properties.Name -join ',')"
$exported = Read-Oml @('export',$handled,'WebItems')
Require (@($exported.preparation.nodes)[0].name -eq 'FetchItems') 'export after onException'
Write-Host 'ok   export: Traditional and Reactive screens, block, several names, batch step, inspect otherContent'
} finally { Remove-TestWorkFolder $Tmp }
