# 1.21.1: REST structures, callbacks and security, JSON names, flow edits with a Switch, in-place replaceNode, End
# resolution, extension references, entity options and spec check hints. Writes only under $Tmp.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'improvements'
$blank = Join-Path $Fixtures 'r0.oml'
Require-TestFile $blank 'fixture module r0.oml'
try {
function Require($ok, $message) { if (-not $ok) { throw $message } }
function Nodes($node) { $node; foreach ($child in $node.children) { Nodes $child } }
function Apply($source, $name, $spec) {
  $path = Join-Path $Tmp "$name.json"; $out = Join-Path $Tmp "$name.oml"
  Set-Content -LiteralPath $path -Value $spec
  $text = (& $Oml apply $source $path $out) -join "`n"
  [pscustomobject]@{ Exit = $LASTEXITCODE; Report = ($text | ConvertFrom-Json); Text = $text; Out = $out }
}
function Tree($module, $path) { @(Nodes ((& $Oml tree $module $path) -join "`n" | ConvertFrom-Json)) }
function Edge($nodes, $from, $to) { @($nodes | Where-Object { $_.source -eq $from -and $_.target -eq $to }).Count -ge 1 }

# REST: variables of REST structures (#54), JSON names (#55), callbacks (#65), security (#64), locals (#66).
$rest = Apply $blank 'rest' @'
{ "structures": [ { "name": "ChatRequest", "attributes": [ { "name": "MaxTokens", "type": "Integer", "jsonName": "max_tokens", "sendDefault": false } ] },
    { "name": "Lonely", "attributes": [ { "name": "Value", "sendDefault": true } ] } ],
  "restClients": [ { "name": "Mistral", "baseUrl": "https://example.com", "actions": [
      { "name": "Chat", "method": "POST", "url": "/chat", "inputs": [ { "name": "Body", "type": "ChatRequest", "location": "body" } ], "outputs": [ { "name": "Response", "type": "Text" } ] } ],
      "onBeforeRequest": { "flow": [ { "kind": "assign", "name": "Copy", "assign": { "CustomizedRequest": "Request" } } ] },
      "onAfterResponse": { "locals": [ { "name": "Note" } ], "flow": [ { "kind": "assign", "name": "CopyR", "assign": { "CustomizedResponse": "Response", "Note": "\"x\"" } } ] } } ],
  "serverActions": [ { "name": "UseIt", "locals": [ { "name": "Req", "type": "MistralChatRequest" }, { "name": "Req2", "type": "Mistral.MistralChatRequest" } ],
    "flow": [ { "kind": "assign", "name": "Set", "assign": { "Req.MaxTokens": "5", "Req2.MaxTokens": "6" } } ] } ],
  "restServices": [
    { "name": "OpenApi", "actions": [ { "name": "Ping", "url": "/ping" } ] },
    { "name": "InnerApi", "internalAccessOnly": true, "actions": [ { "name": "Ping", "url": "/ping" } ] },
    { "name": "BasicApi", "authentication": "basic", "actions": [ { "name": "Ping", "url": "/ping", "locals": [ { "name": "Tmp" } ], "outputs": [ { "name": "R" } ],
      "flow": [ { "kind": "assign", "name": "A", "assign": { "Tmp": "\"ok\"", "R": "Tmp" } } ] } ] },
    { "name": "CustomApi", "authentication": "custom", "onAuthentication": { "locals": [ { "name": "Token" } ], "flow": [ { "kind": "assign", "name": "ReadHeader", "assign": { "Token": "\"x\"" } } ] }, "actions": [ { "name": "Ping", "url": "/ping" } ] } ] }
'@
Require ($rest.Exit -eq 0 -and $rest.Report.saved) "rest apply failed: $($rest.Text)"
$ids = @($rest.Report.newWarnings | ForEach-Object { $_.id })
$open = @(($rest.Report.newWarnings | Where-Object id -eq 'ExposedRestApiWithoutAuthentication').samples)
Require ($open.Count -eq 1 -and $open[0].owner -eq 'OpenApi') "only OpenApi should warn about authentication: $($rest.Text)"
Require ($ids -contains 'RestAuthenticationNotImplemented') "basic without Users should warn: $($rest.Text)"
Require (($rest.Report.warnings -join ' ') -match 'Lonely.Value: sendDefault was not applied') "unused sendDefault: $($rest.Text)"
$client = Tree $rest.Out 'Mistral'
Require (Edge $client 'Start' 'Copy') "onBeforeRequest flow lost"
Require (Edge $client 'Start' 'CopyR') "onAfterResponse flow lost"
Require (@($client | Where-Object { $_.kind -eq 'LocalVariable' -and $_.name -eq 'Note' }).Count -eq 1) 'callback local lost'
Require (@(Tree $rest.Out 'BasicApi/OnAuthentication' | Where-Object name -in 'Username','Password').Count -eq 2) 'Basic OnAuthentication inputs missing'
Require (Edge (Tree $rest.Out 'CustomApi/OnAuthentication') 'Start' 'ReadHeader') 'custom onAuthentication flow lost'
Require (@(Tree $rest.Out 'BasicApi/Ping' | Where-Object { $_.kind -eq 'LocalVariable' -and $_.name -eq 'Tmp' }).Count -eq 1) 'exposed method local lost'
$described = (& $Oml describe $rest.Out ChatRequest) -join "`n"
Require ($described -match 'JSON: max_tokens') "Name in JSON lost: $described"
$bad = Apply $blank 'rest-bad' '{ "restServices": [ { "name": "A", "authentication": "oauth", "actions": [] } ] }'
Require ($bad.Exit -eq 1 -and ($bad.Report.problems -join ' ') -match 'none, basic or custom') "bad authentication: $($bad.Text)"
$again = Apply $rest.Out 'rest-again' '{ "restServices": [ { "name": "OpenApi", "authentication": "basic" } ] }'
Require ($again.Exit -eq 1 -and ($again.Report.problems -join ' ') -match 'rule 7') "existing API security changed: $($again.Text)"
Write-Host 'ok   REST structures in variables, JSON names, callbacks, security and locals'

# Flows: Switch connectors (#58), in-place replaceNode (#61), End (#69), changed reports every edit.
$flow = Apply $blank 'flow' @'
{ "structures": [ { "name": "Dto", "attributes": [ { "name": "V" } ] } ],
  "serverActions": [ { "name": "Sw", "inputs": [ { "name": "N", "type": "Integer" } ], "outputs": [ { "name": "O" } ], "flow": [
    { "kind": "assign", "name": "First", "assign": { "O": "\"a\"" } },
    { "kind": "switch", "name": "Pick", "cases": [ { "condition": "N = 1", "next": "One" }, { "condition": "N = 2", "next": "Two" } ], "otherwise": "Other" },
    { "kind": "assign", "name": "One", "assign": { "O": "\"1\"" }, "next": "Last" },
    { "kind": "assign", "name": "Two", "assign": { "O": "\"2\"" }, "next": "Last" },
    { "kind": "assign", "name": "Other", "assign": { "O": "\"x\"" }, "next": "Last" },
    { "kind": "assign", "name": "Last", "assign": { "O": "O + \"!\"" }, "next": "End" } ] },
   { "name": "Reader", "outputs": [ { "name": "O" } ], "flow": [
    { "kind": "jsonDeserialize", "name": "Parse", "jsonString": "\"{}\"", "dataType": "Dto" },
    { "kind": "assign", "name": "Use", "assign": { "O": "Parse.Data.V" }, "next": "End" },
    { "kind": "end", "name": "End" },
    { "kind": "exceptionHandler", "name": "OnErr", "exception": "AllExceptions" },
    { "kind": "assign", "name": "Note", "assign": { "O": "OnErr.ExceptionMessage" } } ] } ] }
'@
Require ($flow.Exit -eq 0 -and $flow.Report.saved) "flow apply failed: $($flow.Text)"
$editsSpec = @'
{ "edits": [
  { "op": "replaceNode", "element": "Sw/First", "node": { "kind": "assign", "name": "First", "assign": { "O": "\"b\"" } } },
  { "op": "replaceNode", "element": "Sw/Two", "node": { "kind": "assign", "name": "TwoNew", "assign": { "O": "\"22\"" } } },
  { "op": "replaceNode", "element": "Sw/Other", "node": { "kind": "assign", "name": "OtherNew", "assign": { "O": "\"y\"" } } },
  { "op": "replaceNode", "element": "Reader/Parse", "node": { "kind": "jsonDeserialize", "name": "Parse", "jsonString": "\"[]\"" } },
  { "op": "replaceNode", "element": "Reader/Use", "node": { "kind": "assign", "name": "Use", "assign": { "O": "Parse.Data.V + \"?\"" }, "next": "End" } },
  { "op": "replaceNode", "element": "Reader/Note", "node": { "kind": "assign", "name": "NoteNew", "assign": { "O": "OnErr.ExceptionMessage + \".\"" }, "next": "End" } },
  { "op": "insertNodes", "action": "Reader", "after": "Use", "ifMissing": true, "flow": [ { "kind": "if", "name": "Extra", "condition": "O = \"\"", "true": "ExtraEnd", "false": "End" }, { "kind": "end", "name": "ExtraEnd" } ] },
  { "op": "set", "element": "Reader/Parse", "properties": { "jsonString": "\"[1]\"" } } ] }
'@
$edited = Apply $flow.Out 'flow-edit' $editsSpec
Require ($edited.Exit -eq 0 -and $edited.Report.saved -and -not $edited.Report.newErrors) "flow edits failed: $($edited.Text)"
$changed = @($edited.Report.changed) -join '|'
foreach ($want in 'node Sw/First', 'node Reader/Parse', 'nodes after Use in Reader', 'Reader/Parse: jsonString') { Require ($changed -match [regex]::Escape($want)) "changed lacks '$want': $changed" }
$sw = Tree $edited.Out 'Sw'
Require (Edge $sw 'Pick' 'TwoNew') 'Switch condition branch not rewired'
Require (Edge $sw 'Pick' 'OtherNew') 'Otherwise branch not rewired'
Require (Edge $sw 'Pick' 'One') 'other Switch branch lost'
$reader = Tree $edited.Out 'Reader'
Require (@($reader | Where-Object name -eq 'Parse').Count -eq 1 -and (Edge $reader 'Use' 'Extra')) 'in-place node or insert lost'
Require (Edge $reader 'OnErr' 'NoteNew') 'handler path lost'
# insertNodes ifMissing after a save: End nodes have lost their names, and the edit must still see the nodes as there.
$once = Apply $edited.Out 'flow-once' '{ "edits": [ { "op": "insertNodes", "action": "Reader", "after": "Use", "ifMissing": true, "flow": [ { "kind": "if", "name": "Extra", "condition": "O = \"\"", "true": "ExtraEnd", "false": "End" }, { "kind": "end", "name": "ExtraEnd" } ] } ] }'
Require ($once.Exit -eq 0 -and (@($once.Report.existing) -join ' ') -match 'already there' -and -not $once.Report.changed) "ifMissing fooled by End names: $($once.Text)"
# An equal replaceNode changes nothing.
$equal = Apply $edited.Out 'flow-equal' '{ "edits": [ { "op": "replaceNode", "element": "Sw/First", "node": { "kind": "assign", "name": "First", "assign": { "O": "\"b\"" } } } ] }'
Require ($equal.Exit -eq 0 -and -not $equal.Report.changed) "equal replaceNode reported a change: $($equal.Text)"
Write-Host 'ok   Switch connectors, in-place replaceNode, End on the main path, ifMissing, changed'

# Extensions (#60): a .xif producer; a reference copied from another module; platform refusal text is unit-sized (self-test).
$xifSpec = Join-Path $Tmp 'ext.json'; $xif = Join-Path $Tmp 'MyExt.xif'
Set-Content -LiteralPath $xifSpec -Value '{ "actions": [ { "name": "Hello", "function": true, "inputs": [ { "name": "Who", "type": "Text" } ], "outputs": [ { "name": "Out", "type": "Text" } ] }, { "name": "Other" } ] }'
& $Oml extension new $xif --name MyExt --spec $xifSpec | Out-Null
Require ($LASTEXITCODE -eq 0) 'extension new failed'
$ref = Apply $blank 'ext-ref' (@{ references = @(@{ producer = $xif; elements = @('Hello') }); serverActions = @(@{ name = 'C2'; outputs = @(@{ name = 'O' }); flow = @(@{ kind = 'execute'; name = 'H'; action = 'Hello'; args = @{ Who = '"a"' } }, @{ kind = 'assign'; name = 'A'; assign = @{ O = 'H.Out' } }) }) } | ConvertTo-Json -Depth 8)
Require ($ref.Exit -eq 0 -and $ref.Report.saved -and (@($ref.Report.created) -join ' ') -match 'reference MyExt.Hello') "xif reference failed: $($ref.Text)"
$fromOml = Apply $blank 'ext-copy' (@{ references = @(@{ producer = $ref.Out; extension = 'MyExt'; elements = @('Hello') }) } | ConvertTo-Json -Depth 6)
Require ($fromOml.Exit -eq 0 -and (@($fromOml.Report.created) -join ' ') -match 'reference MyExt.Hello') "reference from another .oml failed: $($fromOml.Text)"
$missing = Apply $blank 'ext-missing' (@{ references = @(@{ producer = $ref.Out; extension = 'Nope'; elements = @('Hello') }) } | ConvertTo-Json -Depth 6)
Require ($missing.Exit -eq 1 -and ($missing.Report.problems -join ' ') -match "no reference to an extension named 'Nope'") "missing extension: $($missing.Text)"
Write-Host 'ok   extension references from .xif and from another module'

# Entity audit (#70).
$entity = Apply $blank 'entity' @'
{ "staticEntities": [ { "name": "Kind", "attributes": [ { "name": "Id", "type": "Text", "length": 20 } ], "records": [ { "identifier": "A", "label": "a" } ] } ],
  "entities": [ { "name": "P", "multiTenant": true, "exposeProcessEvents": true, "attributes": [ { "name": "Name", "length": 100, "label": "Full name" } ] },
    { "name": "C", "public": true, "exposeReadOnly": true, "updateBehavior": "changedAttributes", "attributes": [ { "name": "PId", "type": "P Identifier", "deleteRule": "Delete" }, { "name": "K" } ], "indexes": [ { "attributes": [ "K", "PId" ], "unique": true } ] } ] }
'@
Require ($entity.Exit -eq 0 -and $entity.Report.saved) "entity apply failed: $($entity.Text)"
$text = (& $Oml describe $entity.Out Kind C) -join "`n"
Require ($text -match 'Id: Text' -and $text -match 'Id = .{1,2}A') "static Text Id lost: $text"
Require ($text -match 'delete rule: Delete' -and $text -match 'update behavior: changed' -and $text -match 'index IX_K_PId') "entity options lost: $text"
foreach ($case in @(
    @('{ "entities": [ { "name": "X", "attributes": [ { "name": "A", "deleteRule": "Delete" } ] } ] }', 'only to a reference attribute'),
    @('{ "entities": [ { "name": "Y", "attributes": [ { "name": "A", "type": "Y Identifier", "deleteRule": "Maybe" } ] } ] }', 'not Protect, Delete or Ignore'),
    @('{ "staticEntities": [ { "name": "Z", "attributes": [ { "name": "Id", "type": "Decimal" } ] } ] }', 'Integer, Long Integer or Text'))) {
  $r = Apply $blank "entity-bad-$(Get-Random)" $case[0]
  Require ($r.Exit -eq 1 -and ($r.Report.problems -join ' ') -match $case[1]) "entity refusal '$($case[1])': $($r.Text)"
}
$existing = Apply $entity.Out 'entity-existing' '{ "entities": [ { "name": "P", "multiTenant": false } ] }'
Require ($existing.Exit -eq 1 -and ($existing.Report.problems -join ' ') -match 'multi-tenant') "existing multi-tenant changed: $($existing.Text)"
Write-Host 'ok   entity options, static identifiers and delete rules'

# Spec check (#56) and the hint (#57, #68).
$spec = Join-Path $Tmp 'lint.json'
Set-Content -LiteralPath $spec -Value '{ "serverActions": [ { "name": "A", "outputs": [ { "name": "O" } ], "flow": [ { "kind": "assign", "name": "Do", "assign": { "O": "\"a\"" }, "next": "End" }, { "kind": "exceptionHandler", "name": "OnErr", "exception": "AllExceptions" }, { "kind": "assign", "name": "Note", "assign": { "O": "AllExceptions.ExceptionMessage" } } ] } ] }'
$lint = (& $Oml spec check $spec) -join "`n"
Require ($lint -match 'OnErr\.ExceptionMessage') "spec check did not name the handler: $lint"
Set-Content -LiteralPath $spec -Value '{ "serverActions": [ { "name": "A", "outputs": [ { "name": "O" } ], "flow": [ { "kind": "assign", "name": "Do", "assign": { "O": "\"a\"" }, "next": "End" }, { "kind": "exceptionHandler", "name": "AllExceptions", "exception": "AllExceptions" }, { "kind": "assign", "name": "Note", "assign": { "O": "AllExceptions.ExceptionMessage" } } ] } ] }'
$fine = (& $Oml spec check $spec) -join "`n"
Require ($fine -notmatch 'not a name in this flow') "a handler named AllExceptions was flagged: $fine"
$hint = Apply $blank 'hint' '{ "serverActions": [ { "name": "A", "outputs": [ { "name": "O", "type": "Date Time" } ], "flow": [ { "kind": "assign", "name": "Do", "assign": { "O": "CurrentDateTime()" } } ] } ] }'
$row = @($hint.Report.newErrors | Where-Object id -eq 'ExpressionUnknownFunction')
Require ($row.Count -eq 1 -and $row[0].hint -match 'CurrDateTime' -and $row[0].hint -match 'GenerateGuid') "hint: $($hint.Text)"
Write-Host 'ok   spec check handler names and the unknown function hint'
} finally { Remove-TestWorkFolder $Tmp }
