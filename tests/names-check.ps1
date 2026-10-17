# Qualified Module.Element names: ambiguity refused with candidates, qualified names resolve, oml find lists both.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($ok, $message) { if (-not $ok) { throw $message } }
function Apply($source, $json, $out) {
  $spec = Join-Path $Tmp 'names-spec.json'; Set-Content -Path $spec -Value $json -Encoding utf8
  Remove-Item $out -ErrorAction SilentlyContinue
  & $Oml apply $source $spec $out 2>$null | ConvertFrom-Json
}
$dir = Join-Path $Tmp 'names'; New-Item -ItemType Directory -Force $dir | Out-Null
foreach ($p in 'NamesCore','NamesLegacy') { & $Oml new (Join-Path $Tmp "$p-blank.oml") --name $p --kind CrossDevice | Out-Null }
$core = Join-Path $dir 'NamesCore.oml'; $legacy = Join-Path $dir 'NamesLegacy.oml'
$r = Apply (Join-Path $Tmp 'NamesCore-blank.oml') '{ "entities": [{ "name": "Customer", "public": true, "attributes": [{ "name": "Name" }] }], "serverActions": [{ "name": "FetchCustomer", "public": true, "inputs": [{ "name": "CustomerId", "type": "Customer Identifier" }] }], "blocks": [{ "name": "Badge", "widgets": [{ "type": "Text", "text": "core" }] }], "edits": [{ "op": "set", "element": "Badge", "properties": { "public": true } }] }' $core
Require $r.saved 'NamesCore producer'
$r = Apply (Join-Path $Tmp 'NamesLegacy-blank.oml') '{ "serverActions": [{ "name": "FetchCustomer", "public": true, "inputs": [{ "name": "Email", "type": "Email" }] }, { "name": "Badge", "public": true }] }' $legacy
Require $r.saved 'NamesLegacy producer'
& $Oml new (Join-Path $Tmp 'names-blank.oml') --name NamesShop --kind CrossDevice | Out-Null
$consumer = Join-Path $Tmp 'names-consumer.oml'
$refs = @{ references = @(@{ producer = $core; elements = @('Customer','Badge') }, @{ producer = $legacy; elements = @('FetchCustomer','Badge') }) } | ConvertTo-Json -Depth 5
Require (Apply (Join-Path $Tmp 'names-blank.oml') $refs $consumer).saved 'consumer references'
$r = Apply $consumer '{ "serverActions": [{ "name": "Use", "flow": [{ "kind": "execute", "action": "Badge" }] }] }' (Join-Path $Tmp 'names-a.oml')
Require (-not $r.saved -and ($r.problems -join ' ') -match 'NamesCore\.Badge' -and ($r.problems -join ' ') -match 'NamesLegacy\.Badge') 'ambiguous name not refused with candidates'
$r = Apply $consumer '{ "serverActions": [{ "name": "Use", "locals": [{ "name": "C", "type": "NamesCore.Customer Identifier" }, { "name": "L", "type": "List of NamesCore.Customer" }], "flow": [{ "kind": "execute", "action": "NamesLegacy.Badge" }] }] }' (Join-Path $Tmp 'names-q.oml')
Require $r.saved 'qualified names did not resolve'
$r = Apply $consumer '{ "serverActions": [{ "name": "Use", "flow": [{ "kind": "execute", "action": "Billing.FetchCustomer" }] }] }' (Join-Path $Tmp 'names-u.oml')
Require (-not $r.saved -and ($r.problems -join ' ') -match "no module 'Billing'" -and ($r.problems -join ' ') -match 'Manage Dependencies') 'unknown module not explained'
$f = & $Oml find FetchCustomer $consumer --in $dir 2>$null | ConvertFrom-Json
Require (@($f.matches | Where-Object { $_.qualified -eq 'NamesCore.FetchCustomer' -and $_.source -eq 'producer file' }).Count -eq 1) 'find missed NamesCore.FetchCustomer'
Require (@($f.matches | Where-Object { $_.qualified -eq 'NamesLegacy.FetchCustomer' -and $_.source -eq 'referenced' }).Count -eq 1) 'find missed the referenced NamesLegacy.FetchCustomer'
Require ($f.hint -match '2 modules') 'find gave no ambiguity hint'
