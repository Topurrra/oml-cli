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

# Per-field record mappings: a record passed to a parameter of another record type holds one mapping per target field.
# They show in tree and describe, set changes one, review compares them, and a node that a mapping reads cannot be deleted.
function Edit($source, $name, $edits) {
  $spec = Join-Path $Tmp "$name.json"; @{ edits = $edits } | ConvertTo-Json -Depth 10 | Set-Content $spec
  $out = Join-Path $Tmp "$name.oml"
  $r = & $Oml apply $source $spec $out | ConvertFrom-Json
  [pscustomobject]@{ Result = $r; Out = $out; Code = $LASTEXITCODE }
}
$mapBuilt = Join-Path $Tmp 'mappings.oml'
$r = & $Oml apply (Join-Path $Tmp 'r0.oml') (Join-Path $PSScriptRoot 'mappings.json') $mapBuilt | ConvertFrom-Json
Require ($r.saved) "mappings.json did not save: $($r | ConvertTo-Json -Depth 6 -Compress)"
$field = @(Nodes (Tree $mapBuilt 'ServerAction:Collect/Add') | Where-Object { $_.kind -eq 'FieldMapping' })
Require ($field.Count -eq 1 -and $field[0].name -eq 'LastCode' -and $field[0].ref -eq 'LastCode' -and $field[0].path -eq 'ServerAction:Collect/Add/Argument[0]/LastCode') "unmapped field is not a FieldMapping child: $($field | ConvertTo-Json -Compress)"
$fill = 'GetApplicants.List.Current.Applicant.Code'
$e = Edit $mapBuilt 'map-set' @(@{ op = 'set'; element = 'ServerAction:Collect/Add/Argument[0]/LastCode'; properties = @{ value = $fill } })
Require ($e.Code -eq 0 -and $e.Result.saved) "set on a field mapping failed: $($e.Result | ConvertTo-Json -Depth 6 -Compress)"
$mapped = @(Nodes (Tree $e.Out 'ServerAction:Collect/Add') | Where-Object { $_.path -eq 'ServerAction:Collect/Add/Argument[0]/LastCode/ParsedExpression[0]' })
Require ($mapped.Count -eq 1 -and $mapped[0].text -eq $fill) 'the mapping expression is not in tree'
$text = ((& $Oml describe $e.Out Collect | ConvertFrom-Json).text) -replace "`r", ''
Require ($text.Contains("call Reg.Take(Item = Rec { LastCode = $fill })")) "describe does not print the mapping: $text"
$bad = Edit $e.Out 'map-refused' @(@{ op = 'delete'; element = 'Collect/GetApplicants' })
Require ($bad.Code -eq 1 -and -not $bad.Result.saved -and -not (Test-Path $bad.Out) -and ($bad.Result.problems -join ' ') -match 'refused: cannot delete' -and ($bad.Result.problems -join ' ') -match 'LastCode' -and ($bad.Result.problems -join ' ').Contains($fill)) "delete of a node that a mapping reads was not refused with the mapping: $($bad.Result | ConvertTo-Json -Depth 6 -Compress)"
# A plain expression that reads the node counts the same.
$plain = Edit $e.Out 'map-plain' @(@{ op = 'delete'; element = 'Collect/GetOthers' })
Require ($plain.Code -eq 1 -and -not $plain.Result.saved -and ($plain.Result.problems -join ' ') -match 'CountOthers' -and ($plain.Result.problems -join ' ').Contains('GetOthers.Count')) "delete of a node that an expression reads was not refused: $($plain.Result | ConvertTo-Json -Depth 6 -Compress)"
$plainSpec = @{ op = 'set'; element = 'Collect/Add/Argument[0]/LastCode'; properties = @{ value = 'Rec.Code' } }
$freed = Edit $e.Out 'map-freed' @($plainSpec, @{ op = 'delete'; element = 'Collect/GetApplicants' })
Require ($freed.Code -eq 0 -and $freed.Result.saved) "delete after set did not work: $($freed.Result | ConvertTo-Json -Depth 6 -Compress)"
$flow = @(Nodes (Tree $freed.Out 'ServerAction:Collect'))
Require (@($flow | Where-Object { $_.name -eq 'GetApplicants' }).Count -eq 0 -and @($flow | Where-Object { $_.source -eq 'Start' -and $_.target -eq 'Add' }).Count -eq 1) 'the flow was not joined again after the delete'
$rv = & $Oml review $e.Out $freed.Out | ConvertFrom-Json
Require (($rv.changes.details -join ' ').Contains('argument Item.LastCode')) "review does not compare the mapping: $($rv | ConvertTo-Json -Depth 6 -Compress)"
# Only the mapping field takes "value".
$badKey = Edit $e.Out 'map-badkey' @(@{ op = 'set'; element = 'Collect/Add/Argument[0]/LastCode'; properties = @{ description = 'x' } })
Require ($badKey.Code -eq 1 -and ($badKey.Result.problems -join ' ') -match 'takes only') 'a field mapping took a property other than value'
Write-Host 'ok   field mappings: tree, describe, set, delete refusal, review'
# A node without outputs (Assign) can share its name with a parameter: `Item.Code` reads the parameter, so the delete goes through.
$sharedSpec = Join-Path $Tmp 'delete-shared-spec.json'
Set-Content $sharedSpec '{ "entities": [{ "name": "Applicant", "attributes": [{ "name": "Code", "type": "Long Integer" }] }], "serverActions": [{ "name": "Pick", "inputs": [{ "name": "Item", "type": "Applicant" }], "outputs": [{ "name": "Out", "type": "Long Integer" }], "flow": [{ "kind": "assign", "name": "Item", "assign": { "Out": "1" } }, { "kind": "assign", "name": "Use", "assign": { "Out": "Item.Code" } }] }] }'
$shared = Join-Path $Tmp 'delete-shared.oml'
$r = & $Oml apply (Join-Path $Tmp 'r0.oml') $sharedSpec $shared | ConvertFrom-Json
Require $r.saved "shared-name fixture did not save: $($r | ConvertTo-Json -Depth 6 -Compress)"
$e = Edit $shared 'delete-shared-out' @(@{ op = 'delete'; element = 'Pick/Assign:Item' })
Require ($e.Code -eq 0 -and $e.Result.saved) "delete of an Assign node that shares a parameter's name was refused: $($e.Result | ConvertTo-Json -Depth 6 -Compress)"
Write-Host 'ok   delete of a node without outputs ignores a parameter of the same name'
