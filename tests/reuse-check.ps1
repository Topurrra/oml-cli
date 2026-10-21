# An existing element listed in a creation list: new parts are added (as addParts and addVariables add them),
# changed settings are problems that name the key and the op, and the same spec applied again saves.
# Rule 4 (inputs and outputs of existing public elements) is checked on all three paths: creation list,
# addVariables and addParts.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $Tmp ('reuse-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir -Force | Out-Null
function Require($condition, $message) { if (-not $condition) { throw "reuse: $message" } }
$n = 0
function Apply-Spec($source, $json) {
  $script:n++
  $spec = Join-Path $dir "s$script:n.json"; $out = Join-Path $dir "o$script:n.oml"
  Set-Content -LiteralPath $spec -Value $json -Encoding utf8
  $r = & $Oml apply $source $spec $out 2>$null | ConvertFrom-Json
  [pscustomobject]@{ Exit = $LASTEXITCODE; Report = $r; Out = $out; Problems = (@($r.problems) -join "`n"); Text = ($r | ConvertTo-Json -Depth 8 -Compress) }
}
# A refused change: exit 1, not saved, and a problem with every given text.
function Refused($source, $json, [string[]]$texts, $what) {
  $r = Apply-Spec $source $json
  Require ($r.Exit -eq 1 -and -not $r.Report.saved) "$what was not refused: $($r.Text)"
  foreach ($t in $texts) { Require ($r.Problems.Contains($t)) "$what`: no problem with '$t': $($r.Problems)" }
}
# An accepted change: saved, and every given entry in created.
function Accepted($source, $json, [string[]]$created, $what) {
  $r = Apply-Spec $source $json
  Require ($r.Exit -eq 0 -and $r.Report.saved) "$what was refused: $($r.Text)"
  foreach ($c in $created) { Require (@($r.Report.created) -contains $c) "$what`: '$c' not in created: $($r.Text)" }
  $r
}

$base = @'
{
  "exceptions": ["MyErr"],
  "roles": [{ "name": "Manager" }],
  "siteProperties": [{ "name": "Limit", "type": "Integer", "default": "10" }],
  "clientVariables": [{ "name": "Mode", "default": "\"a\"" }],
  "entities": [{ "name": "Customer", "attributes": [{ "name": "Name", "length": 50, "mandatory": true }, { "name": "Code" },
    { "name": "Flag", "type": "Boolean", "default": "False" }, { "name": "Label", "default": "\"a b\"" }] }],
  "structures": [{ "name": "Dto", "attributes": [{ "name": "A", "type": "Integer" }] }],
  "staticEntities": [{ "name": "Status", "records": [{ "identifier": "Active", "label": "Active" }] }],
  "serverActions": [{ "name": "Calc", "inputs": [{ "name": "X", "type": "Integer" }], "outputs": [{ "name": "Y", "type": "Integer" }],
      "flow": [{ "kind": "assign", "name": "Set", "assign": { "Y": "X + 1" } }] },
    { "name": "PubCalc", "public": true, "description": "Adds one.", "inputs": [{ "name": "X", "type": "Integer", "description": "A number." }],
      "outputs": [{ "name": "Y", "type": "Integer", "description": "X + 1." }], "flow": [{ "kind": "assign", "name": "Set", "assign": { "Y": "X + 1" } }] },
    { "name": "Check", "inputs": [{ "name": "N", "type": "Integer" }], "flow": [
      { "kind": "comment", "text": "Fails on a negative number" },
      { "kind": "if", "name": "Negative", "condition": "N < 0", "true": "Fail", "false": "End" },
      { "kind": "raiseException", "name": "Fail", "exception": "MyErr", "message": "\"Negative\"" }] }],
  "clientActions": [{ "name": "PubClient", "public": true, "inputs": [{ "name": "A", "type": "Text", "mandatory": false }, { "name": "B", "type": "Text" }] },
    { "name": "Boot" }, { "name": "Boot2" }],
  "systemEvents": { "onApplicationReady": "Boot" },
  "restClients": [{ "name": "Api", "baseUrl": "https://api.example.com", "actions": [{ "name": "GetX", "method": "GET", "url": "/x" }] }],
  "blocks": [{ "name": "Picker", "events": [{ "name": "OnPick", "mandatory": false }],
      "actions": [{ "name": "Pick", "flow": [{ "kind": "triggerEvent", "name": "Fire", "event": "OnPick" }] }],
      "widgets": [{ "type": "Button", "onClick": "Pick", "children": [{ "type": "Text", "text": "Pick" }] }] },
    { "name": "Shared", "public": true, "inputs": [{ "name": "BI", "type": "Integer", "mandatory": false }, { "name": "BM", "type": "Integer" }],
      "events": [{ "name": "OnE", "mandatory": false, "inputs": [{ "name": "V", "type": "Integer" }] }],
      "widgets": [{ "type": "Text", "text": "Shared" }] }],
  "screens": [{ "name": "Home", "locals": [{ "name": "N", "type": "Integer" }], "styleSheet": ".a { color: red; }",
      "aggregates": [{ "name": "GetCustomers", "sources": ["Customer"], "maxRecords": 10 }],
      "actions": [{ "name": "Reload", "flow": [{ "kind": "refreshData", "name": "Again", "dataSource": "GetCustomers" }] }],
      "widgets": [{ "type": "Expression", "value": "GetCustomers.List.Length" }] },
    { "name": "Open", "access": { "anonymous": true }, "widgets": [{ "type": "Text", "text": "Open" }] },
    { "name": "Members", "access": { "registered": true }, "widgets": [{ "type": "Text", "text": "Members" }] }]
}
'@
$blank = Join-Path $dir 'blank.oml'
& $Oml new $blank --kind CrossDevice | Out-Null
$built = Apply-Spec $blank $base
Require ($built.Exit -eq 0 -and $built.Report.saved) "base did not save: $($built.Text)"
$m = $built.Out
Require ((& $Oml describe $m Shared | ConvertFrom-Json).text -match 'public') 'a new block with public true is not public'

# The same spec again saves and adds nothing (unnamed Raise Exception and Refresh Data nodes, a comment, access the
# model widens). What cannot be compared is in warnings, not newWarnings.
$again = Apply-Spec $m $base
Require ($again.Exit -eq 0 -and $again.Report.saved -and -not $again.Report.created) "re-apply: $($again.Text)"
Require ((@($again.Report.warnings) -join ' ') -match 'screen Home exists: its widgets' -and -not ($again.Report.newWarnings | Where-Object id -eq 'ExistingElementNotChanged')) "re-apply warnings: $($again.Text)"
Require (-not ((@($again.Report.warnings) -join ' ') -match 'aggregate')) "a restated existing aggregate must not be a warning: $($again.Text)"

# Entities: a new attribute is added; a changed length, type or default of an existing one is refused.
$null = Accepted $m '{ "entities": [{ "name": "Customer", "attributes": [{ "name": "Name", "length": 50 }, { "name": "Email" }] }] }' @('attribute Customer.Email') 'entity: new attribute'
Refused $m '{ "entities": [{ "name": "Customer", "attributes": [{ "name": "Name", "length": 100 }] }] }' @('Customer.Name: attribute exists', 'length (100; module: 50)', 'rule 5') 'entity: changed length'
Refused $m '{ "entities": [{ "name": "Customer", "attributes": [{ "name": "Code", "default": "\"X\"" }] }] }' @('default differs', 'edits "set"') 'entity: changed default'
# Defaults: case and spaces outside text literals do not count; inside a literal every character does.
$null = Accepted $m '{ "entities": [{ "name": "Customer", "attributes": [{ "name": "Flag", "type": "boolean", "default": "false" }] }] }' @() 'entity: same default, other case'
Refused $m '{ "entities": [{ "name": "Customer", "attributes": [{ "name": "Label", "default": "\"ab\"" }] }] }' @('Customer.Label: attribute exists', 'default differs') 'entity: text literal without its space'
# Structures: same rules.
$null = Accepted $m '{ "structures": [{ "name": "Dto", "attributes": [{ "name": "A", "type": "Integer" }, { "name": "B" }] }] }' @('attribute Dto.B') 'structure: new attribute'
Refused $m '{ "structures": [{ "name": "Dto", "attributes": [{ "name": "A", "type": "Text" }] }] }' @('Dto.A: attribute exists', 'type (Text; module: Integer)') 'structure: changed type'
# Static entities: a new record is added; a changed record is refused.
$null = Accepted $m '{ "staticEntities": [{ "name": "Status", "records": [{ "identifier": "Active", "label": "Active" }, { "identifier": "Closed" }] }] }' @('record Status.Closed') 'static entity: new record'
Refused $m '{ "staticEntities": [{ "name": "Status", "records": [{ "identifier": "Active", "label": "On" }] }] }' @('Status.Active: static record exists', 'label differs') 'static entity: changed record'

# Actions: an input type, function and a different flow are refused. Public is allowed (tests/public-check.ps1).
Refused $m '{ "serverActions": [{ "name": "Calc", "inputs": [{ "name": "X", "type": "Text" }] }] }' @('Calc.inputs.X exists', 'type (Text; module: Integer)') 'action: input type'
Refused $m '{ "serverActions": [{ "name": "Calc", "function": true }] }' @('serverAction Calc exists', 'function differs') 'action: function'
Refused $m '{ "serverActions": [{ "name": "Calc", "flow": [{ "kind": "assign", "name": "Other", "assign": { "Y": "1" } }] }] }' @('flow (node Other is not in it)', 'insertNodes') 'action: flow'
Refused $m '{ "serverActions": [{ "name": "Calc", "flow": [{ "kind": "assign", "name": "Set", "assign": { "Y": "X + 1" } }, { "kind": "assign", "assign": { "Y": "2" } }] }] }' @('flow (2 nodes; module: 1)') 'action: flow with one more node'

# Rule 4 on public elements, through the creation list, addVariables and addParts.
$r = Accepted $m '{ "serverActions": [{ "name": "PubCalc", "inputs": [{ "name": "Z", "type": "Integer", "mandatory": false, "description": "Optional." }], "outputs": [{ "name": "W", "type": "Integer", "description": "More." }] }] }' @('input serverAction PubCalc.Z', 'output serverAction PubCalc.W') 'rule 4: optional input and output on a public server action'
$null = Accepted $m '{ "serverActions": [{ "name": "PubCalc", "inputs": [{ "name": "X", "type": "Integer", "mandatory": false }] }] }' @() 'rule 4: input made optional on a public server action'
Refused $m '{ "serverActions": [{ "name": "PubCalc", "inputs": [{ "name": "Z", "type": "Integer" }] }] }' @("refused: 'PubCalc' is an existing public server or service action. A new mandatory input 'Z'", 'rule 4') 'rule 4: creation list, new mandatory input'
Refused $m '{ "edits": [{ "op": "addVariables", "action": "PubCalc", "inputs": [{ "name": "Z", "type": "Integer", "mandatory": true }] }] }' @("A new mandatory input 'Z'") 'rule 4: addVariables, new mandatory input'
Refused $m '{ "edits": [{ "op": "set", "element": "PubClient/A", "properties": { "IsMandatory": true } }] }' @("Making input 'A' mandatory") 'rule 4: set, input made mandatory'
Refused $m '{ "clientActions": [{ "name": "PubClient", "inputs": [{ "name": "A", "type": "Text", "mandatory": true }] }] }' @("Making input 'A' mandatory") 'rule 4: creation list, input made mandatory'
Refused $m '{ "clientActions": [{ "name": "PubClient", "inputs": [{ "name": "C", "type": "Text", "mandatory": false }] }] }' @("existing public client action. Any new input, even an optional one ('C')") 'rule 4: creation list, optional input on a public client action'
Refused $m '{ "clientActions": [{ "name": "PubClient", "inputs": [{ "name": "B", "type": "Text", "mandatory": false }] }] }' @("Making input 'B' optional") 'rule 4: input made optional on a public client action'
Refused $m '{ "clientActions": [{ "name": "PubClient", "outputs": [{ "name": "W", "type": "Integer" }] }] }' @("A new output 'W'", 'only public server and service actions') 'rule 4: creation list, output on a public client action'
Refused $m '{ "edits": [{ "op": "addVariables", "action": "PubClient", "outputs": [{ "name": "W", "type": "Integer" }] }] }' @("A new output 'W'") 'rule 4: addVariables, output on a public client action'
Refused $m '{ "blocks": [{ "name": "Shared", "inputs": [{ "name": "BJ", "type": "Integer", "mandatory": false }] }] }' @("existing public Reactive or Mobile block or screen. Any new input, even an optional one ('BJ')") 'rule 4: creation list, optional input on a public block'
Refused $m '{ "edits": [{ "op": "addParts", "screen": "MainFlow/Shared", "inputs": [{ "name": "BJ", "type": "Integer", "mandatory": false }] }] }' @("Any new input, even an optional one ('BJ')") 'rule 4: addParts, optional input on a public block'
Refused $m '{ "edits": [{ "op": "addParts", "screen": "MainFlow/Shared", "inputs": [{ "name": "BK", "type": "Integer" }] }] }' @("A new mandatory input 'BK'", 'Create a new element instead') 'rule 4: addParts, mandatory input on a public block'
Refused $m '{ "blocks": [{ "name": "Shared", "inputs": [{ "name": "BI", "type": "Integer", "mandatory": true }] }] }' @("Making input 'BI' mandatory") 'rule 4: input made mandatory on a public block'
Refused $m '{ "blocks": [{ "name": "Shared", "inputs": [{ "name": "BM", "type": "Integer", "mandatory": false }] }] }' @("Making input 'BM' optional") 'rule 4: input made optional on a public block'
Refused $m '{ "edits": [{ "op": "set", "element": "PubCalc/X", "properties": { "DataType": "Text" } }] }' @("Changing the type of input 'X' (Integer to Text)") 'rule 4: set, input type on a public action'

# Blocks: a new optional event is added; a new mandatory event and any new event on a public block are refused.
$null = Accepted $m '{ "blocks": [{ "name": "Picker", "events": [{ "name": "OnPick", "mandatory": false }, { "name": "OnClose", "mandatory": false }] }] }' @('event Picker.OnClose') 'block: new optional event'
Refused $m '{ "blocks": [{ "name": "Picker", "events": [{ "name": "OnPick" }, { "name": "OnNew" }] }] }' @("'OnPick' is an existing optional event", 'new mandatory event') 'block: mandatory events'
Refused $m '{ "blocks": [{ "name": "Shared", "events": [{ "name": "OnLater", "mandatory": false }] }] }' @('existing public block') 'block: event on a public block'
# Screens: new parts are added and listed as created; access changes are refused; a styleSheet is replaced (UI).
$r = Accepted $m '{ "screens": [{ "name": "Home", "locals": [{ "name": "M", "type": "Text" }], "aggregates": [{ "name": "GetMore", "sources": ["Customer"], "maxRecords": 5 }], "actions": [{ "name": "Go", "flow": [] }] }] }' @('local screen Home.M', 'aggregate Home/GetMore', 'screenAction Home/Go') 'screen: new parts'
$null = Accepted $m '{ "screens": [{ "name": "Home", "styleSheet": ".a{ color: red; }\n" }, { "name": "Open", "access": { "anonymous": true, "registered": true } }] }' @() 'screen: same styleSheet and access, written differently'
Refused $m '{ "screens": [{ "name": "Home", "access": { "anonymous": true } }] }' @('access differs', 'setAccess') 'screen: access'
Refused $m '{ "screens": [{ "name": "Members", "access": { "roles": ["Manager"] } }] }' @('screen Members exists', 'access differs') 'screen: registered to a role'
$r = Accepted $m '{ "screens": [{ "name": "Home", "styleSheet": ".a { color: blue; }" }] }' @() 'screen: changed styleSheet'
Require ((@($r.Report.changed) -join '|') -eq 'styleSheet of screen Home') "screen: a changed styleSheet is replaced and in changed: $($r.Text)"
$r = Accepted $m '{ "screens": [{ "name": "Home", "styleSheet": ".a{color:red;}" }] }' @() 'screen: same styleSheet without spaces'
Require (-not $r.Report.changed) "screen: the same styleSheet without spaces is not a change: $($r.Text)"
# A restated existing aggregate: the same values pass, changed ones are refused.
$null = Accepted $m '{ "screens": [{ "name": "Home", "aggregates": [{ "name": "GetCustomers", "sources": ["Customer"], "maxRecords": 10 }] }] }' @() 'screen: same aggregate'
Refused $m '{ "screens": [{ "name": "Home", "aggregates": [{ "name": "GetCustomers", "sources": ["Customer"], "maxRecords": 99 }] }] }' @('screen Home/GetCustomers exists', 'maxRecords differs', 'addToAggregate') 'screen: changed aggregate'
# Events: a new input of an existing event is added (as addVariables adds it); its type on a public block is rule 4.
$null = Accepted $m '{ "blocks": [{ "name": "Picker", "events": [{ "name": "OnPick", "mandatory": false, "inputs": [{ "name": "W", "type": "Text", "mandatory": false }] }] }] }' @('input block Picker.events.OnPick.W') 'block: new input of an existing event'
Refused $m '{ "edits": [{ "op": "set", "element": "MainFlow/Shared/OnE/V", "properties": { "DataType": "Text" } }] }' @("Changing the type of input 'V' of event 'OnE' (Integer to Text)", 'rule 4') 'rule 4: event input type on a public block'
# System events: the same handler again is reused; another one is refused.
Refused $m '{ "systemEvents": { "onApplicationReady": "Boot2" } }' @('OnApplicationReady already exists and does not just call Boot2') 'system event: another handler'

# Platform elements, REST and module variables.
Refused $m '{ "siteProperties": [{ "name": "Limit", "type": "Integer", "default": "20" }] }' @('siteProperty Limit exists', 'default differs') 'site property: default'
Refused $m '{ "clientVariables": [{ "name": "Mode", "default": "\"b\"" }] }' @('clientVariable Mode exists', 'default differs') 'client variable: default'
Refused $m '{ "restClients": [{ "name": "Api", "actions": [{ "name": "GetX", "method": "GET", "url": "/y" }] }] }' @('restClient Api.GetX exists', 'url differs') 'REST: changed url'
$null = Accepted $m '{ "restClients": [{ "name": "Api", "actions": [{ "name": "GetZ", "method": "GET", "url": "/z" }] }] }' @('restClient Api.GetZ') 'REST: new method on an existing client'
Write-Host 'ok   reuse: listed existing elements add new parts and refuse changes; rule 4 on creation lists, addVariables, addParts and set'

# The field case: an existing block listed again with a new screen action gets it, listed in created (F12).
$r = Accepted $m '{ "blocks": [{ "name": "Picker", "actions": [{ "name": "Refresh", "flow": [] }] }] }' @('screenAction Picker/Refresh') 'block: new screen action'
Require ((& $Oml describe $r.Out Picker/Refresh | ConvertFrom-Json).element -match 'Refresh') "the new screen action is not in the saved block: $($r.Text)"
# onParametersChanged on an existing block: set when unset, through the creation list and addParts; another
# handler when one is set is refused; the same spec again changes nothing (F11).
$loader = Apply-Spec $blank '{ "blocks": [{ "name": "Loader", "inputs": [{ "name": "Status", "type": "Text" }], "locals": [{ "name": "Css", "type": "Text" }],
  "widgets": [{ "type": "Container", "style": "Css", "children": [{ "type": "Expression", "value": "Status" }] }] }] }'
Require ($loader.Exit -eq 0) "Loader: $($loader.Text)"
$rerun = '{ "blocks": [{ "name": "Loader", "onParametersChanged": "SetX", "actions": [{ "name": "SetX", "flow": [{ "kind": "assign", "name": "Pick", "assign": { "Css": "If(Status = \"Done\", \"green\", \"grey\")" } }] }] }] }'
$r = Accepted $loader.Out $rerun @('screenAction Loader/SetX') 'block: onParametersChanged and its new action, creation list'
Require ((& $Oml describe $r.Out Loader | ConvertFrom-Json).text.Contains('OnParametersChanged -> SetX')) "creation list: OnParametersChanged not set: $($r.Text)"
Require ((@($r.Report.changed) -join '|') -eq 'onParametersChanged of block Loader -> SetX') "creation list: the handler is not in changed: $($r.Text)"
$again = Apply-Spec $r.Out $rerun
Require ($again.Exit -eq 0 -and -not $again.Report.created -and -not $again.Report.changed) "onParametersChanged re-apply: $($again.Text)"
Refused $r.Out '{ "blocks": [{ "name": "Loader", "onParametersChanged": "Other", "actions": [{ "name": "Other", "flow": [] }] }] }' @('block Loader exists', 'onParametersChanged differs') 'block: another onParametersChanged handler'
$parts = Accepted $loader.Out '{ "edits": [{ "op": "addParts", "screen": "MainFlow/Loader", "onParametersChanged": "SetY", "actions": [{ "name": "SetY", "flow": [{ "kind": "javaScript", "name": "Run", "javaScript": "console.log(1);" }] }] }] }' @() 'block: onParametersChanged through addParts'
Require ((& $Oml describe $parts.Out Loader | ConvertFrom-Json).text.Contains('OnParametersChanged -> SetY')) "addParts: OnParametersChanged not set: $($parts.Text)"
# The spec.md example ("re-run SetX when input Status changes") applies as written.
$doc = Get-Content -Raw (Join-Path (Split-Path $PSScriptRoot -Parent) 'skill/reference/spec.md')
$example = [regex]::Match($doc, 'Re-run `SetX` when input `Status` changes[^\n]*\r?\n\r?\n```json\r?\n(.*?)```', 'Singleline').Groups[1].Value
Require $example 'spec.md: the onParametersChanged example is missing'
$badge = Accepted $blank $example @() 'spec.md onParametersChanged example'
$text = (& $Oml describe $badge.Out StatusBadge | ConvertFrom-Json).text
Require ($text.Contains('OnParametersChanged -> SetX') -and $text.Contains('OnReady -> SetX')) "spec.md example: handlers not set: $text"
# An existing Traditional Web block listed with Reactive lifecycle keys: refused as when it is created.
$webBlank = Join-Path $dir 'web.oml'
& $Oml new $webBlank --kind WebResponsive | Out-Null
$webBlock = Apply-Spec $webBlank '{ "blocks": [{ "name": "WB", "widgets": [{ "type": "Text", "text": "x" }] }] }'
Require ($webBlock.Exit -eq 0) "web block: $($webBlock.Text)"
Refused $webBlock.Out '{ "blocks": [{ "name": "WB", "onParametersChanged": "Nope" }] }' @('block WB: events and onParametersChanged are supported for Reactive and Mobile blocks only') 'web block: onParametersChanged'
Refused $webBlock.Out '{ "blocks": [{ "name": "WB", "onReady": "Nope" }] }' @('block WB: Traditional Web uses preparation instead of Reactive lifecycle events') 'web block: onReady'
Write-Host 'ok   reuse: a new screen action on an existing block; onParametersChanged through creation list (in changed), addParts and the spec.md example; refused on Traditional Web'

