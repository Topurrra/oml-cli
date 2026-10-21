# A name the model would rename: the problem names the element that holds the name (kind, owner, path), and
# the path works with describe. The user's case: a local named like an input of the same block.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $Tmp ('clash-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir -Force | Out-Null
function Require($condition, $message) { if (-not $condition) { throw "clash: $message" } }
$n = 0
function Clash($source, $json, [string]$text, $what) {
  $script:n++
  $spec = Join-Path $dir "s$script:n.json"; $out = Join-Path $dir "o$script:n.oml"
  Set-Content -LiteralPath $spec -Value $json -Encoding utf8
  $r = & $Oml apply $source $spec $out 2>$null | ConvertFrom-Json
  $problems = @($r.problems) -join "`n"
  Require ($LASTEXITCODE -eq 1 -and -not $r.saved -and $problems.Contains($text)) "$what`: expected '$text', got $($r | ConvertTo-Json -Depth 6 -Compress)"
}
$blank = Join-Path $dir 'blank.oml'
& $Oml new $blank --name Clash --kind CrossDevice | Out-Null
$spec = Join-Path $dir 'base.json'; $m = Join-Path $dir 'base.oml'
Set-Content $spec -Encoding utf8 '{ "entities": [{ "name": "Order", "attributes": [{ "name": "Name" }] }], "roles": [{ "name": "Mgr" }], "serverActions": [{ "name": "Calc" }],
  "blocks": [{ "name": "LatticeLoader", "inputs": [{ "name": "Status", "type": "Text" }], "widgets": [{ "type": "Text", "text": "x" }] }],
  "screens": [{ "name": "WHome", "locals": [{ "name": "Name", "type": "Text" }], "widgets": [{ "type": "Container", "name": "Box", "children": [{ "type": "Input", "name": "NameInput", "variable": "Name" }] }] }] }'
$r = & $Oml apply $blank $spec $m | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0) "base: $($r | ConvertTo-Json -Depth 6 -Compress)"

$holder = 'name taken by input parameter Status of block LatticeLoader (path MainFlow/LatticeLoader/Status)'
Clash $m '{ "blocks": [{ "name": "LatticeLoader", "locals": [{ "name": "status", "type": "Text" }] }] }' "block LatticeLoader: local status: $holder; the model would rename it status2" 'block local like a block input (creation list)'
Clash $m '{ "edits": [{ "op": "addParts", "screen": "MainFlow/LatticeLoader", "locals": [{ "name": "Status", "type": "Text" }] }] }' $holder 'block local like a block input (addParts)'
Clash $m '{ "blocks": [{ "name": "LatticeLoader", "actions": [{ "name": "SetX", "locals": [{ "name": "Status", "type": "Text" }], "flow": [] }] }] }' "block LatticeLoader/SetX: local Status: $holder" 'screen action local like a block input'
Clash $m '{ "serverActions": [{ "name": "CreateOrder" }] }' 'serverAction CreateOrder: name taken by entity action CreateOrder of entity Order (path Order/CreateOrder)' 'server action like a CRUD action'
Clash $m '{ "serverActions": [{ "name": "CheckMgrRole" }] }' 'name taken by action CheckMgrRole of role Mgr (path Mgr/CheckMgrRole)' 'server action like a role action'
Clash $m '{ "structures": [{ "name": "order", "attributes": [{ "name": "A" }] }] }' 'structure order: name taken by entity Order of module Clash (path Order); the model renamed it order2' 'structure like an entity, other case'
Clash $m '{ "serverActions": [{ "name": "Other" }], "edits": [{ "op": "rename", "element": "Other", "to": "Calc" }] }' "name 'Calc' already exists in this parent: server action Calc of module Clash (path Calc)" 'rename to a taken name'
Clash $m '{ "serverActions": [{ "name": "Two", "flow": [{ "kind": "assign", "name": "Same", "assign": {} }, { "kind": "assign", "name": "Same", "assign": {} }] }] }' "node name 'Same' already exists in this flow: assign node Same of server action Two (path Two/Same)" 'two nodes with one name'
Clash $m '{ "staticEntities": [{ "name": "Col", "attributes": [{ "name": "Code" }, { "name": "code" }], "records": [{ "identifier": "A" }] }] }' 'attribute code: name taken by entity attribute Code of static entity Col (path Col/Code)' 'static entity attribute, other case'
Clash $m '{ "entities": [{ "name": "Ent", "attributes": [{ "name": "N" }], "indexes": [{ "name": "IX", "attributes": ["N"] }, { "name": "ix", "attributes": ["N"] }] }] }' 'index ix: name taken by index IX of entity Ent (index names ignore case)' 'index, other case'
Clash $m '{ "screens": [{ "name": "WHome", "locals": [{ "name": "NameInput", "type": "Text" }] }] }' 'screen WHome: local NameInput: name taken by input NameInput of screen WHome (path MainFlow/WHome/Box/NameInput)' 'screen local like a nested widget'
# A name a referenced element holds: the reference, not a flow node that calls it. A local may clash with a node.
$prodBlank = Join-Path $dir 'prod-blank.oml'; $prod = Join-Path $dir 'prod.oml'; $cons = Join-Path $dir 'cons.oml'
& $Oml new $prodBlank --name ClashProd --kind CrossDevice | Out-Null
Set-Content $spec -Encoding utf8 '{ "serverActions": [{ "name": "PA", "public": true }] }'
$null = & $Oml apply $prodBlank $spec $prod
Set-Content $spec -Encoding utf8 ('{ "references": [{ "producer": ' + (ConvertTo-Json $prod) + ', "elements": ["PA"] }], "serverActions": [{ "name": "Local", "flow": [{ "kind": "execute", "action": "ClashProd.PA" }] }] }')
$r = & $Oml apply $m $spec $cons | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0) "consumer: $($r | ConvertTo-Json -Depth 6 -Compress)"
Clash $cons '{ "serverActions": [{ "name": "PA" }] }' 'serverAction PA: name taken by server action PA of reference ClashProd (path ClashProd/PA)' 'server action like a referenced action'
Clash $cons '{ "serverActions": [{ "name": "Local", "locals": [{ "name": "PA", "type": "Text" }] }] }' 'serverAction Local: local PA: name taken by execute server action node PA of server action Local (path Local/PA)' 'local like a flow node'
# Every path in a message works with describe.
foreach ($p in 'MainFlow/LatticeLoader/Status', 'Order/CreateOrder', 'Mgr/CheckMgrRole', 'Order', 'Calc', 'MainFlow/WHome/Box/NameInput') {
  & $Oml describe $m $p | Out-Null
  Require ($LASTEXITCODE -eq 0) "describe '$p' failed"
}
Write-Host 'ok   clash: taken names name the holder (kind, owner, path); the user case of a local named like a block input'
