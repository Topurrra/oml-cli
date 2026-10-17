# Semantic checks for saved edits, ordered applies, paths and refusal behavior.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = "Stop"
function Nodes($node) { $node; foreach ($child in $node.children) { Nodes $child } }
function Tree($source, $path) {
  $result = & $Oml tree $source $path | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) { throw "tree failed: $path" }
  return $result
}
function Require($ok, $message) { if (-not $ok) { throw $message } }
$built = Join-Path $Tmp 'out-edits.json.oml'
$action = @(Nodes (Tree $built 'CreateOrderFor'))
Require (@($action | Where-Object { $_.source -eq 'Prepare' -and $_.target -eq 'RememberTotal' }).Count -eq 1) 'insert predecessor lost'
Require (@($action | Where-Object { $_.source -eq 'RememberTotal' -and $_.target -eq 'CreateOrder' }).Count -eq 1) 'insert successor lost'
Require (@($action | Where-Object { $_.source -eq 'ValidTotal' -and $_.type -eq 'True' -and $_.target -eq 'BranchAudit' }).Count -eq 1) 'true connector insertion failed'
Require (@($action | Where-Object { $_.source -eq 'BranchAudit' -and $_.target -eq 'Prepare' }).Count -eq 1) 'explicit next target failed'
$sum = @(Nodes (Tree $built 'SumCustomerOrders'))
Require (@($sum | Where-Object { $_.source -eq 'Loop' -and $_.type -eq 'Cycle' -and $_.target -eq 'CycleAudit' }).Count -eq 1) 'cycle connector insertion failed'
Require (@($sum | Where-Object { $_.source -eq 'CycleAudit' -and $_.target -eq 'Add' }).Count -eq 1) 'cycle successor lost'
Require (@($sum | Where-Object { $_.name -eq 'ResetCount' }).Count -eq 0) 'deleted node remains'
Require (@($sum | Where-Object { $_.source -eq 'Loop' -and $_.type -eq 'Sequence' -and $_.target -eq 'SetCount' }).Count -eq 1) 'delete successor lost'
$header = Tree $built 'Orders/Header'
Require ($header.widgetOrder[1] -eq 'EditCaption' -and $header.widgetOrder[2] -eq 'EditHint') 'widget index did not round-trip'
Require (@(Nodes $header | Where-Object { $_.text -eq '"Total orders: " + GetOrders.Count' }).Count -eq 1) 'widget property did not round-trip'
$unnamed = @(Nodes $header | Where-Object { $_.kind -eq 'Text' -and -not $_.name })[0]
Require ((Tree $built $unnamed.path).kind -eq 'Text') 'unnamed widget path failed'
# Each edit must also save and reopen independently, targeting the previous output.
$source = Join-Path $Tmp 'out-logic.json.oml'
$edits = (Get-Content (Join-Path $PSScriptRoot 'edits.json') -Raw | ConvertFrom-Json).edits
for ($i = 0; $i -lt $edits.Count; $i++) {
  $spec = Join-Path $Tmp "edit-step-$i.json"
  @{ edits = @($edits[$i]) } | ConvertTo-Json -Depth 30 | Set-Content $spec
  $out = Join-Path $Tmp "edit-step-$i.oml"
  $r = & $Oml apply $source $spec $out | ConvertFrom-Json
  Require ($LASTEXITCODE -eq 0 -and $r.saved -and $r.reopenNewErrors.Count -eq 0) "edit step $i failed: $($r | ConvertTo-Json -Depth 10)"
  $source = $out
}
$screen = @(Nodes (Tree $built 'Orders'))
Require (@($screen | Where-Object { $_.name -eq 'OnReady' -or $_.type -eq 'OnReady' } | Where-Object { $_.ref -eq 'RefreshCaption' }).Count -eq 1) 'lifecycle handler did not round-trip'
Require (@($screen | Where-Object { $_.text -eq '3' -and $_.path -like 'Orders/LatestOrders/*' }).Count -eq 1) 'aggregate property did not round-trip'
Require (@($screen | Where-Object { $_.source -eq 'Start' -and $_.target -eq 'ClearCaption' }).Count -eq 1) 'screen action insertion failed'
$refusals = @(
  @('{"op":"delete","element":"Order"}', 'refused:'),
  @('{"op":"delete","element":"Order/Total"}', 'refused:'),
  @('{"op":"delete","element":"CreateOrderFor/ValidTotal"}', 'exactly one'),
  @('{"op":"insertNodes","action":"CreateOrderFor","after":"ValidTotal","flow":[{"kind":"assign","name":"X"}]}', 'requires connector'),
  @('{"op":"rename","element":"CreateOrderFor/Prepare","to":"SetOut"}', 'already exists'),
  @('{"op":"set","element":"Missing","properties":{"description":"test"}}', 'no element'),
  @('{"op":"addWidgets","screen":"Orders","parent":"Header","index":999,"widgets":[]}', 'index must'),
  @('{"op":"addWidgets","screen":"Orders"}', 'given key')
)
for ($i = 0; $i -lt $refusals.Count; $i++) {
  $spec = Join-Path $Tmp "edit-refusal-$i.json"
  '{"edits":[' + $refusals[$i][0] + ']}' | Set-Content $spec
  $out = Join-Path $Tmp "edit-refusal-$i.oml"
  $r = & $Oml apply (Join-Path $Tmp 'out-logic.json.oml') $spec $out | ConvertFrom-Json
  Require ($LASTEXITCODE -eq 1 -and -not $r.saved -and -not (Test-Path $out) -and ($r.problems -join ' ') -match $refusals[$i][1]) "refusal $i failed: $($r | ConvertTo-Json -Depth 10)"
}

$spec = Join-Path $Tmp 'edit-ambiguous.json'
'{"edits":[{"op":"set","element":"Total","properties":{"description":"Ambiguous"}}]}' | Set-Content $spec
$out = Join-Path $Tmp 'edit-ambiguous.oml'
$r = & $Oml apply (Join-Path $Tmp 'out-logic.json.oml') $spec $out | ConvertFrom-Json
Require ($LASTEXITCODE -eq 1 -and -not $r.saved -and -not (Test-Path $out) -and ($r.problems -join ' ') -match 'ambiguous') 'ambiguous path was not refused'
Write-Host 'ok   edits topology, widget order, paths, sequential saves and refusals'
