# Semantic checks for saved edits, ordered applies, paths and refusal behavior.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'edits'
Require-TestFile (Join-Path $Fixtures 'out-logic.json.oml') 'fixture module out-logic.json.oml'
Require-TestFile (Join-Path $Fixtures 'r0.oml') 'fixture module r0.oml'
try {
function Nodes($node) { $node; foreach ($child in $node.children) { Nodes $child } }
function Tree($source, $path) {
  $result = & $Oml tree $source $path | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) { throw "tree failed: $path" }
  return $result
}
function Require($ok, $message) { if (-not $ok) { throw $message } }
$built = Join-Path $Fixtures 'out-edits.json.oml'; Require-TestFile $built 'fixture module out-edits.json.oml'
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
$source = Join-Path $Fixtures 'out-logic.json.oml'
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
  $r = & $Oml apply (Join-Path $Fixtures 'out-logic.json.oml') $spec $out | ConvertFrom-Json
  Require ($LASTEXITCODE -eq 1 -and -not $r.saved -and -not (Test-Path $out) -and ($r.problems -join ' ') -match $refusals[$i][1]) "refusal $i failed: $($r | ConvertTo-Json -Depth 10)"
}

$spec = Join-Path $Tmp 'edit-ambiguous.json'
'{"edits":[{"op":"set","element":"Total","properties":{"description":"Ambiguous"}}]}' | Set-Content $spec
$out = Join-Path $Tmp 'edit-ambiguous.oml'
$r = & $Oml apply (Join-Path $Fixtures 'out-logic.json.oml') $spec $out | ConvertFrom-Json
Require ($LASTEXITCODE -eq 1 -and -not $r.saved -and -not (Test-Path $out) -and ($r.problems -join ' ') -match 'ambiguous') 'ambiguous path was not refused'
Write-Host 'ok   edits topology, widget order, paths, sequential saves and refusals'

# Edits that may run again (a library synced every cycle): an equal replaceNode or replaceWidgets keeps the element and
# its key (review shows no change), insertNodes with ifMissing inserts once, and a changed replaceNode rebuilds the node.
$logic = Join-Path $Fixtures 'out-logic.json.oml'
$prepare = @{ kind = 'assign'; name = 'Prepare'; assign = @{ 'Rec.CustomerId' = 'CustomerId'; 'Rec.Total' = 'Total' } }
function Again($source, $name, $edits) {
  $spec = Join-Path $Tmp "$name.json"; @{ edits = $edits } | ConvertTo-Json -Depth 10 | Set-Content $spec
  $out = Join-Path $Tmp "$name.oml"
  $r = & $Oml apply $source $spec $out | ConvertFrom-Json
  Require ($LASTEXITCODE -eq 0 -and $r.saved) "$name did not save: $($r | ConvertTo-Json -Depth 6 -Compress)"
  [pscustomobject]@{ Result = $r; Out = $out }
}
$same = Again $logic 'again-node-same' @(@{ op = 'replaceNode'; element = 'CreateOrderFor/Prepare'; node = $prepare })
Require ((@($same.Result.existing) -join ' ') -match 'Prepare \(already as in the spec\)') "an equal replaceNode was not kept: $($same.Result | ConvertTo-Json -Depth 6 -Compress)"
$rv = & $Oml review $logic $same.Out | ConvertFrom-Json
Require ($rv.summary.changes -eq 0) "an equal replaceNode changed the module: $($rv.changes | ConvertTo-Json -Depth 6 -Compress)"
$changedNode = $prepare.Clone(); $changedNode.assign = @{ 'Rec.CustomerId' = 'CustomerId'; 'Rec.Total' = 'Total + 1' }
$diff = Again $logic 'again-node-diff' @(@{ op = 'replaceNode'; element = 'CreateOrderFor/Prepare'; node = $changedNode })
Require ((@($diff.Result.changed) -join ' ') -match 'node CreateOrderFor/Prepare') "a changed replaceNode is not in changed: $($diff.Result | ConvertTo-Json -Depth 6 -Compress)"
$text = ((& $Oml describe $diff.Out CreateOrderFor --depth 0 | ConvertFrom-Json).text) -replace "`r", ''
Require ($text.Contains('set Rec.Total = Total + 1') -and $text.Contains('set OrderId = CreateOrder.Id')) "replaceNode did not rebuild the node in place: $text"
$once = Again $logic 'again-insert' @(@{ op = 'insertNodes'; action = 'CreateOrderFor'; after = 'ValidTotal'; connector = 'true'; ifMissing = $true; flow = @(@{ kind = 'assign'; name = 'Prepare'; assign = @{ 'Rec.Total' = 'Total' } }) })
Require ((@($once.Result.existing) -join ' ') -match 'already there') "insertNodes ifMissing inserted a node that exists: $($once.Result | ConvertTo-Json -Depth 6 -Compress)"
$widgets = @(@{ type = 'Container'; name = 'AgainBox'; style = '"again"'; children = @(@{ type = 'Text'; text = 'Again' }) })
$first = Again $logic 'again-widgets-1' @(@{ op = 'replaceWidgets'; at = 'Orders/Header'; widgets = $widgets })
$second = Again $first.Out 'again-widgets-2' @(@{ op = 'replaceWidgets'; at = 'Orders/Header'; widgets = $widgets })
Require ((@($second.Result.existing) -join ' ') -match 'widgets of Orders/Header \(already as in the spec\)') "an equal replaceWidgets was not kept: $($second.Result | ConvertTo-Json -Depth 6 -Compress)"
$rv = & $Oml review $first.Out $second.Out | ConvertFrom-Json
Require ($rv.summary.changes -eq 0) "an equal replaceWidgets changed the module: $($rv.changes | ConvertTo-Json -Depth 6 -Compress)"
Write-Host 'ok   replaceNode, replaceWidgets and insertNodes ifMissing change nothing when the module already matches'

# Per-field record mappings: a record passed to a parameter of another record type holds one mapping per target field.
# They show in tree and describe, set changes one, review compares them, and a node that a mapping reads cannot be deleted.
function Edit($source, $name, $edits) {
  $spec = Join-Path $Tmp "$name.json"; @{ edits = $edits } | ConvertTo-Json -Depth 10 | Set-Content $spec
  $out = Join-Path $Tmp "$name.oml"
  $r = & $Oml apply $source $spec $out | ConvertFrom-Json
  [pscustomobject]@{ Result = $r; Out = $out; Code = $LASTEXITCODE }
}
$mapBuilt = Join-Path $Tmp 'mappings.oml'
$r = & $Oml apply (Join-Path $Fixtures 'r0.oml') (Join-Path $PSScriptRoot 'mappings.json') $mapBuilt | ConvertFrom-Json
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
$r = & $Oml apply (Join-Path $Fixtures 'r0.oml') $sharedSpec $shared | ConvertFrom-Json
Require $r.saved "shared-name fixture did not save: $($r | ConvertTo-Json -Depth 6 -Compress)"
$e = Edit $shared 'delete-shared-out' @(@{ op = 'delete'; element = 'Pick/Assign:Item' })
Require ($e.Code -eq 0 -and $e.Result.saved) "delete of an Assign node that shares a parameter's name was refused: $($e.Result | ConvertTo-Json -Depth 6 -Compress)"
Write-Host 'ok   delete of a node without outputs ignores a parameter of the same name'
# repoint: an existing call node runs another action; it keeps its name, its argument is set again by name, and the
# Assign that reads Run.Id stays valid. An argument without a matching input and a node that is not a call are refused.
$repointSpec = Join-Path $Tmp 'repoint-spec.json'
Set-Content $repointSpec '{ "serverActions": [
  { "name": "MakeA", "inputs": [{ "name": "Source", "type": "Text" }], "outputs": [{ "name": "Id", "type": "Integer" }], "flow": [{ "kind": "assign", "name": "SetA", "assign": { "Id": "Length(Source)" } }] },
  { "name": "MakeB", "inputs": [{ "name": "Source", "type": "Text" }], "outputs": [{ "name": "Id", "type": "Integer" }], "flow": [{ "kind": "assign", "name": "SetB", "assign": { "Id": "Length(Source) + 1" } }] },
  { "name": "MakeC", "inputs": [{ "name": "Other", "type": "Text" }], "outputs": [{ "name": "Id", "type": "Integer" }], "flow": [{ "kind": "assign", "name": "SetC", "assign": { "Id": "Length(Other)" } }] },
  { "name": "Caller", "outputs": [{ "name": "Result", "type": "Integer" }], "flow": [
    { "kind": "execute", "name": "Run", "action": "MakeA", "args": { "Source": "\"abc\"" } },
    { "kind": "assign", "name": "Keep", "assign": { "Result": "Run.Id" } }] }] }'
$repointBuilt = Join-Path $Tmp 'repoint.oml'
$r = & $Oml apply (Join-Path $Fixtures 'r0.oml') $repointSpec $repointBuilt | ConvertFrom-Json
Require $r.saved "repoint fixture did not save: $($r | ConvertTo-Json -Depth 6 -Compress)"
$e = Edit $repointBuilt 'repoint-ok' @(@{ op = 'repoint'; element = 'Caller/Run'; action = 'MakeB' })
Require ($e.Code -eq 0 -and $e.Result.saved -and $e.Result.reopenNewErrors.Count -eq 0 -and (@($e.Result.changed) -join ' ') -match 'node Caller/Run now calls MakeB') "repoint failed: $($e.Result | ConvertTo-Json -Depth 6 -Compress)"
$text = ((& $Oml describe $e.Out Caller --depth 0 | ConvertFrom-Json).text) -replace "`r", ''
Require ($text.Contains('MakeB(Source = "abc") -> Run') -and $text.Contains('set Result = Run.Id')) "repoint did not keep the node, its argument or its reader: $text"
$again = Edit $e.Out 'repoint-again' @(@{ op = 'repoint'; element = 'Caller/Run'; action = 'MakeB' })
Require ($again.Code -eq 0 -and $again.Result.saved -and -not $again.Result.changed -and (@($again.Result.existing) -join ' ') -match 'already calls MakeB') "a repeated repoint changed the module: $($again.Result | ConvertTo-Json -Depth 6 -Compress)"
$noInput = Edit $repointBuilt 'repoint-no-input' @(@{ op = 'repoint'; element = 'Caller/Run'; action = 'MakeC' })
Require ($noInput.Code -eq 1 -and -not $noInput.Result.saved -and -not (Test-Path $noInput.Out) -and ($noInput.Result.problems -join ' ') -match "has no input 'Source'") "repoint to an action without the argument's input was not refused: $($noInput.Result | ConvertTo-Json -Depth 6 -Compress)"
$notCall = Edit $repointBuilt 'repoint-not-call' @(@{ op = 'repoint'; element = 'Caller/Keep'; action = 'MakeB' })
Require ($notCall.Code -eq 1 -and -not $notCall.Result.saved -and ($notCall.Result.problems -join ' ') -match 'must be an Execute') "repoint of an Assign was not refused: $($notCall.Result | ConvertTo-Json -Depth 6 -Compress)"
Write-Host 'ok   repoint: same node, arguments by name, readers kept, no-op, refusals'
# replaceNode that changes only links (next) is made, never "already as in the spec": in place (Assign) and on a node that
# is built again (an aggregate equal to the spec keeps its node; the link still changes). An equal node stays a no-op.
$linkSpec = Join-Path $Tmp 'links-spec.json'
Set-Content $linkSpec '{ "entities": [{ "name": "Thing", "attributes": [{ "name": "Label" }] }], "serverActions": [
  { "name": "Steps", "outputs": [{ "name": "X", "type": "Integer" }], "flow": [{ "kind": "assign", "name": "A1", "assign": { "X": "1" } }, { "kind": "assign", "name": "A2", "assign": { "X": "2" } }, { "kind": "assign", "name": "A3", "assign": { "X": "3" } }] },
  { "name": "Query", "outputs": [{ "name": "N", "type": "Integer" }], "flow": [{ "kind": "aggregate", "name": "GetThings", "sources": ["Thing"], "maxRecords": 10 }, { "kind": "assign", "name": "S1", "assign": { "N": "GetThings.Count" } }, { "kind": "assign", "name": "S2", "assign": { "N": "N + 1" } }] }] }'
$linkBuilt = Join-Path $Tmp 'links.oml'
$r = & $Oml apply (Join-Path $Fixtures 'r0.oml') $linkSpec $linkBuilt | ConvertFrom-Json
Require $r.saved "links fixture did not save: $($r | ConvertTo-Json -Depth 6 -Compress)"
function Assign($name, $var, $value, $next) { @{ op = 'replaceNode'; element = $name; node = @{ kind = 'assign'; name = ($name -split '/')[-1]; assign = @{ $var = $value }; next = $next } } }
$e = Edit $linkBuilt 'links-inplace' @((Assign 'Steps/A2' 'X' '2' 'End'), (Assign 'Steps/A3' 'X' '3' 'A2'), (Assign 'Steps/A1' 'X' '1' 'A3'))
Require ($e.Code -eq 0 -and $e.Result.saved -and -not $e.Result.existing -and (@($e.Result.changed) -join ' ') -match 'A1: next goes to A3') "link-only replaceNode in place was not made: $($e.Result | ConvertTo-Json -Depth 6 -Compress)"
$text = ((& $Oml describe $e.Out Steps --depth 0 | ConvertFrom-Json).text) -replace "`r", ''
Require ($text -match 'set X = 1\n\s*set X = 3\n\s*set X = 2\n') "link-only replaceNode in place did not reorder the flow: $text"
$aggregate = @{ op = 'replaceNode'; element = 'Query/GetThings'; node = @{ kind = 'aggregate'; name = 'GetThings'; sources = @('Thing'); maxRecords = 10; next = 'S2' } }
$e = Edit $linkBuilt 'links-rebuilt' @((Assign 'Query/S1' 'N' 'GetThings.Count' 'End'), (Assign 'Query/S2' 'N' 'N + 1' 'S1'), $aggregate)
Require ($e.Code -eq 0 -and $e.Result.saved -and -not $e.Result.existing -and (@($e.Result.changed) -join ' ') -match 'GetThings: next goes to S2') "link-only replaceNode of an aggregate was not made: $($e.Result | ConvertTo-Json -Depth 6 -Compress)"
$text = ((& $Oml describe $e.Out Query --depth 0 | ConvertFrom-Json).text) -replace "`r", ''
Require ($text -match 'GetThings: Thing max 10\n\s*set N = N \+ 1\n\s*set N = GetThings.Count') "link-only replaceNode of an aggregate did not reorder the flow: $text"
$aggregate.node.Remove('next')
$e = Edit $linkBuilt 'links-same' @($aggregate)
Require ($e.Code -eq 0 -and $e.Result.saved -and (@($e.Result.existing) -join ' ') -match 'GetThings \(already as in the spec\)') "an equal aggregate replaceNode was not kept: $($e.Result | ConvertTo-Json -Depth 6 -Compress)"
Write-Host 'ok   replaceNode link-only changes: in place and rebuilt nodes relink, an equal node stays'
# A delete of a table's header cell takes the column's row cell with it (a model side effect): changed names both, and a
# warning names the paths that moved.
$tableSpec = Join-Path $Tmp 'table-spec.json'
Set-Content $tableSpec '{ "entities": [{ "name": "Row", "attributes": [{ "name": "Label" }, { "name": "Code" }] }], "screens": [{ "name": "Rows", "aggregates": [{ "name": "GetRows", "sources": ["Row"] }],
  "widgets": [{ "type": "TableRecords", "name": "Table", "source": "GetRows.List",
    "headerRow": [{ "type": "HeaderCell", "children": [{ "type": "Text", "text": "Label" }] }, { "type": "HeaderCell", "children": [{ "type": "Text", "text": "Code" }] }],
    "row": [{ "type": "RowCell", "children": [{ "type": "Expression", "value": "GetRows.List.Current.Row.Label" }] }, { "type": "RowCell", "children": [{ "type": "Expression", "value": "GetRows.List.Current.Row.Code" }] }] }] }] }'
$tableBuilt = Join-Path $Tmp 'table.oml'
$r = & $Oml apply (Join-Path $Fixtures 'r0.oml') $tableSpec $tableBuilt | ConvertFrom-Json
Require $r.saved "table fixture did not save: $($r | ConvertTo-Json -Depth 6 -Compress)"
$e = Edit $tableBuilt 'table-delete' @(@{ op = 'delete'; element = 'Rows/Table/HeaderCell[0]' })
Require ($e.Code -eq 0 -and $e.Result.saved -and (@($e.Result.changed) -join ' ') -match 'also removed Rows/[^ ]*RowCell\[0\]' -and (@($e.Result.warnings) -join ' ') -match 'RowCell\[1\] is now Rows/[^ ]*RowCell\[0\]') "a header cell delete did not name the row cell and the moved paths: $($e.Result | ConvertTo-Json -Depth 6 -Compress)"
Write-Host 'ok   delete: a header cell takes its column''s row cell; changed names it and a warning names the moved paths'
} finally { Remove-TestWorkFolder $Tmp }
