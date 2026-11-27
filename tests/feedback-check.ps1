# Fixes from field feedback (2.2.0), on one small Reactive module: set onAfterFetch and a numeric startIndex on existing
# aggregates (describe shows the start), replaceWidgets at a screen warns that the layout goes, a block listed again
# without "flow" changes the one in its flow (no copy in a new MainFlow), clear messages for screen action outputs and
# HTML attributes on a block instance, describe --css --out, inspect of a .xif, and install refusing swapped arguments.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Tmp = New-TestFolder $Tmp 'feedback'
try {
function Require($condition, $message) { if (-not $condition) { throw "feedback: $message" } }
function Oml-Json([string[]]$Arguments) {
  $text = & $Oml @Arguments 2>&1
  $json = $null; try { $json = ($text -join "`n") | ConvertFrom-Json } catch { }
  [pscustomobject]@{ Code = $LASTEXITCODE; Json = $json; Text = ($text -join "`n") }
}
function Spec([string]$name, [string]$json) { $p = Join-Path $Tmp $name; Set-Content $p $json -Encoding utf8; $p }
function Apply([string]$spec, [string]$out) { Oml-Json @('apply', $base, $spec, (Join-Path $Tmp $out)) }

$blank = Join-Path $Tmp 'blank.oml'
& $Oml new $blank --name FeedbackCheck --kind CrossDevice | Out-Null
$base = Join-Path $Tmp 'base.oml'
$r = Oml-Json @('apply', $blank, (Spec 'base.json' @'
{
  "entities": [{ "name": "Item", "attributes": [{ "name": "Title", "type": "Text", "length": 50 }] }],
  "blocks": [
    { "name": "LayoutX", "flow": "Common", "widgets": [{ "type": "Container", "name": "LRoot", "children": [{ "type": "Placeholder", "name": "MainContent" }] }] },
    { "name": "Badge", "flow": "Common", "widgets": [{ "type": "Text", "text": "b" }] }
  ],
  "emails": [{ "name": "Welcome", "flow": "Mails", "subject": "\"Hi\"" }],
  "screens": [{ "name": "Home", "isDefault": true, "layout": "LayoutX", "styleSheet": ".home { color: red; }",
    "aggregates": [{ "name": "GetItems", "sources": ["Item"], "maxRecords": 10 }],
    "actions": [{ "name": "Fetched", "flow": [] }],
    "widgets": [{ "type": "BlockInstance", "name": "B1", "sourceBlock": "Badge" }, { "type": "Text", "text": "hi" }] }],
  "serverActions": [{ "name": "S1", "outputs": [{ "name": "N", "type": "Integer" }],
    "flow": [{ "kind": "aggregate", "name": "GetAll", "sources": ["Item"], "maxRecords": 5 },
             { "kind": "assign", "assign": { "N": "GetAll.List.Length" } }] }]
}
'@), $base)
Require ($r.Code -eq 0 -and $r.Json.saved) "base module: $($r.Text)"

# 1. set onAfterFetch and numeric startIndex / maxRecords on existing aggregates.
$r = Apply (Spec 'e1.json' '{"edits":[{"op":"set","element":"Home/GetItems","properties":{"onAfterFetch":"Fetched","startIndex":3,"maxRecords":7}},{"op":"set","element":"S1/GetAll","properties":{"startIndex":2}}]}') 'o1.oml'
Require ($r.Code -eq 0 -and $r.Json.saved) "set onAfterFetch and startIndex: $($r.Text)"
$d = Oml-Json @('describe', (Join-Path $Tmp 'o1.oml'), 'Home/GetItems')
Require ($d.Json.text -match 'on after fetch -> Fetched') "onAfterFetch not set: $($d.Text)"
$d = Oml-Json @('describe', (Join-Path $Tmp 'o1.oml'), 'S1')
Require ($d.Json.text -match 'max 5 start 2') "describe shows no start index: $($d.Text)"
Write-Host 'ok   set onAfterFetch and a numeric startIndex on existing aggregates; describe shows the start'

# 2. replaceWidgets at the screen warns that the layout goes and names the placeholder path.
$r = Apply (Spec 'e2.json' '{"edits":[{"op":"replaceWidgets","at":"Home","widgets":[{"type":"Text","text":"x"}]}]}') 'o2.oml'
Require ($r.Code -eq 0 -and (@($r.Json.warnings) -match 'removed the layout LayoutX.*Home/WebBlockInstance\[0\]/MainContent')) "replaceWidgets layout warning: $($r.Text)"
Write-Host 'ok   replaceWidgets at a screen warns that its layout goes'

# 3. A block listed again without "flow" is the one in Common, not a copy in a new MainFlow; with another flow, refused.
$r = Apply (Spec 'e3.json' '{"blocks":[{"name":"Badge","widgets":[{"type":"Text","text":"b"}]}]}') 'o3.oml'
Require ($r.Code -eq 0 -and (@($r.Json.existing) -contains 'block Badge') -and -not $r.Json.created) "block again: $($r.Text)"
$i = Oml-Json @('inspect', (Join-Path $Tmp 'o3.oml'), '--only', 'blocks')
Require (@($i.Json.blocks | Where-Object name -eq 'Badge').Count -eq 1) "Badge copied: $($i.Text)"
$r = Apply (Spec 'e3b.json' '{"blocks":[{"name":"Badge","flow":"Other","widgets":[]}]}') 'o3b.oml'
Require ($r.Code -ne 0 -and (@($r.Json.problems) -match 'already exists in UI flow Common')) "block in another flow: $($r.Text)"
# The same for an email outside the default "Emails" flow.
$r = Apply (Spec 'e3c.json' '{"emails":[{"name":"Welcome","subject":"\"Hi\""}]}') 'o3c.oml'
Require ($r.Code -eq 0 -and (@($r.Json.existing) -contains 'email Welcome') -and -not $r.Json.created) "email again: $($r.Text)"
Write-Host 'ok   a block or email listed again without flow changes the existing one'

# 4. Clear messages: screen action outputs, HTML attributes on a block instance.
$r = Apply (Spec 'e4.json' '{"screens":[{"name":"P2","actions":[{"name":"Calc","outputs":[{"name":"R","type":"Integer"}],"flow":[]}]}]}') 'o4.oml'
Require ($r.Code -ne 0 -and (@($r.Json.problems) -match 'a screen action cannot have outputs')) "screen action outputs: $($r.Text)"
$r = Apply (Spec 'e5.json' '{"edits":[{"op":"set","element":"Home/B1","properties":{"attributes":{"title":"\"Hi\""}}}]}') 'o5.oml'
Require ($r.Code -ne 0 -and (@($r.Json.problems) -match 'block instance takes no HTML attributes.*Container')) "block instance attributes: $($r.Text)"
Write-Host 'ok   clear messages for screen action outputs and block instance attributes'

# 5. describe --css --out writes plain CSS; inspect of a .xif points to oml extension inspect.
$css = Join-Path $Tmp 'home.css'
$r = Oml-Json @('describe', $base, 'Home', '--css', '--out', $css)
Require ($r.Code -eq 0 -and (Get-Content -Raw $css).Trim() -eq '.home { color: red; }' -and $r.Json.chars -gt 0 -and -not $r.Json.styleSheet) "describe --css --out: $($r.Text)"
$xif = Join-Path $Tmp 'Ext.xif'; Set-Content $xif 'not a module'
$r = Oml-Json @('inspect', $xif)
Require ($r.Code -ne 0 -and $r.Text -match 'oml extension inspect') "inspect .xif: $($r.Text)"
Write-Host 'ok   describe --css --out and inspect of an extension file'

# 6. install refuses swapped arguments (target in staged/, source outside) and a backup as target; nothing changes.
$modules = Join-Path $Tmp 'modules'; $staged = Join-Path $Tmp 'staged'
New-Item -ItemType Directory $modules, $staged | Out-Null
Copy-Item $base (Join-Path $modules 'A.oml'); Copy-Item (Join-Path $Tmp 'o1.oml') (Join-Path $staged 'A.oml')
$before = (Get-FileHash (Join-Path $staged 'A.oml')).Hash
$r = Oml-Json @('install', (Join-Path $modules 'A.oml'), (Join-Path $staged 'A.oml'))
Require ($r.Code -eq 2 -and $r.Text -match 'look swapped' -and (Get-FileHash (Join-Path $staged 'A.oml')).Hash -eq $before) "install swapped: $($r.Text)"
Write-Host 'ok   install refuses swapped arguments'

# 7. Fixes of 2.2.1, on a module of their own: an Input's maxLength with a variable binding, an If whose branches both
# end, an End inserted where the only End is a handler's, unchanged values kept as held by replaceNode, an event moved
# to a handler without the old one's input, tree's ref of a block instance, and the hint for a Long Integer into an Integer.
$fixBlank = Join-Path $Tmp 'fix-blank.oml'
& $Oml new $fixBlank --name FixCheck --kind CrossDevice | Out-Null
$fixed = Join-Path $Tmp 'fixed.oml'
$r = Oml-Json @('apply', $fixBlank, (Spec 'fixed.json' @'
{
  "exceptions": ["Stop"],
  "entities": [{ "name": "Contact", "attributes": [{ "name": "Name", "type": "Text", "length": 50 }] }],
  "blocks": [{ "name": "Badge", "inputs": [{ "name": "Caption", "type": "Text" }], "widgets": [{ "type": "Expression", "value": "Caption" }] }],
  "screens": [
    { "name": "Form", "locals": [{ "name": "Rec", "type": "Contact" }],
      "widgets": [{ "type": "Input", "name": "NameInput", "maxLength": 10, "variable": "Rec.Name" }] },
    { "name": "Clicks", "locals": [{ "name": "N", "type": "Integer" }],
      "actions": [{ "name": "One", "inputs": [{ "name": "X", "type": "Integer" }], "flow": [{ "kind": "assign", "name": "SetN", "assign": { "N": "X" } }] },
                  { "name": "Two", "flow": [{ "kind": "assign", "name": "SetN2", "assign": { "N": "2" } }] }],
      "widgets": [{ "type": "Button", "name": "Go", "children": [{ "type": "Text", "text": "Go" }], "onClick": { "action": "One", "args": { "X": "1" } } }] },
    { "name": "Board", "aggregates": [{ "name": "GetContacts", "sources": ["Contact"] }],
      "widgets": [{ "type": "BlockInstance", "name": "Inst", "sourceBlock": "Badge", "args": { "Caption": "GetContacts.List.Current.Contact.Name" } }] }],
  "serverActions": [
    { "name": "Take", "inputs": [{ "name": "N", "type": "Integer" }, { "name": "Sep", "type": "Text" }], "outputs": [{ "name": "Joined", "type": "Text" }],
      "flow": [{ "kind": "assign", "name": "SetJoined", "assign": { "Joined": "Sep" } }] },
    { "name": "Caller", "outputs": [{ "name": "Out", "type": "Text" }], "locals": [{ "name": "A", "type": "Integer" }, { "name": "B", "type": "Text" }],
      "flow": [{ "kind": "execute", "name": "DoTake", "action": "Take", "args": { "N": "1+2  *3", "Sep": "\",\"" } },
               { "kind": "assign", "name": "SetBoth", "assign": { "A": "1+2  *3", "B": "\"x\"" } },
               { "kind": "assign", "name": "SetOut", "assign": { "Out": "DoTake.Joined + B" } }] },
    { "name": "Ends", "inputs": [{ "name": "Items", "type": "Integer" }], "flow": [{ "kind": "if", "name": "Empty", "condition": "Items = 0", "true": "End", "false": "End" }] },
    { "name": "Guard", "inputs": [{ "name": "Items", "type": "Integer" }], "outputs": [{ "name": "Message", "type": "Text" }],
      "flow": [{ "kind": "assign", "name": "Checking", "assign": { "Message": "\"Checking\"" } },
               { "kind": "raiseException", "name": "Fail", "exception": "Stop", "message": "\"stop\"" },
               { "kind": "exceptionHandler", "name": "OnErr", "exception": "AllExceptions" },
               { "kind": "assign", "name": "Note", "assign": { "Message": "OnErr.ExceptionMessage" }, "next": "End" }] },
    { "name": "Counted", "outputs": [{ "name": "N", "type": "Integer" }],
      "flow": [{ "kind": "aggregate", "name": "GetAll", "sources": ["Contact"] }, { "kind": "assign", "name": "SetCount", "assign": { "N": "GetAll.Count" } }] }]
}
'@), $fixed)
Require ($r.Code -eq 0 -and $r.Json.saved) "2.2.1 fixture (an If with both branches to End saves): $($r.Text)"
$hint = @($r.Json.newWarnings | Where-Object id -eq 'ExpressionUnexpectedExpresssionType')[0].hint
Require ($hint -match 'LongIntegerToInteger') "the hint for a Long Integer into an Integer: $hint"
$props = (Oml-Json @('export', $fixed, 'Form')).Json.widgets[0].props
Require ($props.maxLength -eq '10') "maxLength with a variable binding: $($props | ConvertTo-Json -Compress)"
$tree = (Oml-Json @('tree', $fixed, 'Board/Inst', '--depth', '1')).Json
Require ($tree.ref -eq 'Badge') "tree ref of a block instance: $($tree.ref)"
$r = Oml-Json @('apply', $fixed, (Spec 'fixed-edits.json' @'
{ "edits": [
  { "op": "replaceNode", "element": "Caller/DoTake", "node": { "kind": "execute", "name": "DoTake", "action": "Take", "args": { "N": "1 + 2 * 3", "Sep": "\";\"" } } },
  { "op": "replaceNode", "element": "Caller/SetBoth", "node": { "kind": "assign", "name": "SetBoth", "assign": { "A": "1 + 2 * 3", "B": "\"y\"" } } },
  { "op": "insertNodes", "action": "Guard", "after": "Start", "flow": [{ "kind": "if", "name": "None", "condition": "Items = 0", "true": "End", "false": "Checking" }] },
  { "op": "set", "element": "Clicks/Go", "properties": { "onClick": "Two" } }
] }
'@), (Join-Path $Tmp 'fixed-edits.oml'))
Require ($r.Code -eq 0 -and $r.Json.saved -and @($r.Json.newWarnings | Where-Object id -eq 'UnusedArgument').Count -eq 0) "2.2.1 edits (End on the main path, no argument left on the event): $($r.Text)"
$nodes = @((Oml-Json @('export', (Join-Path $Tmp 'fixed-edits.oml'), 'Caller')).Json.flow.nodes)
$call = $nodes | Where-Object name -eq 'DoTake'; $set = $nodes | Where-Object name -eq 'SetBoth'
Require ($call.args.N -ceq '1+2  *3' -and $call.args.Sep -eq '";"' -and $set.assign.A -ceq '1+2  *3' -and $set.assign.B -eq '"y"') "unchanged values kept as held: $($call | ConvertTo-Json -Compress) $($set | ConvertTo-Json -Compress)"
$clicks = (Oml-Json @('describe', (Join-Path $Tmp 'fixed-edits.oml'), 'Clicks')).Json.text
Require ($clicks -match 'onClick=Two' -and $clicks -notmatch 'onClick=Two\(') "the old handler's argument stayed: $clicks"
Write-Host 'ok   2.2.1: maxLength after the binding, an If to End twice, End beside a handler, values kept as held, event arguments, tree ref, Long Integer hint'
}
finally { Remove-TestWorkFolder $Tmp }
