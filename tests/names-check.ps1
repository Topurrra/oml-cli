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
$r = Apply (Join-Path $Tmp 'NamesCore-blank.oml') '{ "entities": [{ "name": "Customer", "public": true, "description": "Buyer record.\nLine two. xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "attributes": [{ "name": "Name" }] }, { "name": "Emoji", "description": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa😀 tail text", "attributes": [{ "name": "Name" }] }], "serverActions": [{ "name": "FetchCustomer", "public": true, "inputs": [{ "name": "CustomerId", "type": "Customer Identifier" }] }], "blocks": [{ "name": "Badge", "widgets": [{ "type": "Text", "text": "core" }] }], "edits": [{ "op": "set", "element": "Badge", "properties": { "public": true } }] }' $core
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

# spec check --module: the apply build runs in memory; ambiguous and unknown names are found, nothing is written.
function SpecCheck($json) {
  $spec = Join-Path $Tmp 'names-check-spec.json'; Set-Content -Path $spec -Value $json -Encoding utf8
  $out = & $Oml spec check $spec --module $consumer 2>$null
  [pscustomobject]@{ Exit = $LASTEXITCODE; Report = ($out -join "`n" | ConvertFrom-Json) }
}
$before = (Get-FileHash $consumer).Hash; $files = @(Get-ChildItem $Tmp -Filter '*.oml').Count
$c = SpecCheck '{ "serverActions": [{ "name": "Use", "flow": [{ "kind": "execute", "action": "Badge" }] }] }'
Require ($c.Exit -eq 1 -and -not $c.Report.ok -and ($c.Report.problems.message -join ' ') -match 'NamesCore\.Badge \(Block\)' -and ($c.Report.problems.message -join ' ') -match 'NamesLegacy\.Badge') "spec check --module: ambiguous name not reported with candidates: $($c.Report | ConvertTo-Json -Compress -Depth 5)"
$c = SpecCheck '{ "serverActions": [{ "name": "Use", "flow": [{ "kind": "execute", "action": "Billing.FetchCustomer" }] }] }'
Require ($c.Exit -eq 1 -and ($c.Report.problems.message -join ' ') -match "no module 'Billing'") 'spec check --module: unknown name not reported'
# A name the same spec creates is not unknown.
$c = SpecCheck '{ "serverActions": [{ "name": "Inner" }, { "name": "Outer", "flow": [{ "kind": "execute", "action": "Inner" }, { "kind": "execute", "action": "NamesLegacy.Badge" }] }] }'
Require ($c.Exit -eq 0 -and $c.Report.ok) "spec check --module: a good spec failed: $($c.Report | ConvertTo-Json -Compress -Depth 5)"
Require ((Get-FileHash $consumer).Hash -eq $before -and @(Get-ChildItem $Tmp -Filter '*.oml').Count -eq $files) 'spec check --module changed or wrote a module'
$spec = Join-Path $Tmp 'names-check-spec.json'
& $Oml spec check $spec 2>$null | Out-Null; Require ($LASTEXITCODE -eq 0) 'spec check without --module no longer works'

# inspect shows a description, cut at 120 characters; find --text matches words that only a description has.
$i = & $Oml inspect $core --own --only entities 2>$null | ConvertFrom-Json
$d = $i.entities[0].description
Require ($d -match '^Buyer record\. Line two\.' -and $d -notmatch '[\r\n]' -and $d.Length -eq 120 -and $d.EndsWith('...')) "inspect description: '$d'"
$e = ($i.entities | Where-Object name -eq 'Emoji').description
Require ($e.EndsWith('...') -and $e -notmatch '[\uD800-\uDBFF]\.\.\.$') 'inspect description cut between surrogate halves'
Require (-not $i.entities[0].kind) 'inspect: a plain entity prints a kind the section already gives'
$f = & $Oml find $core --text 'buyer record' --top 3 2>$null | ConvertFrom-Json
Require ($f.hits[0].name -eq 'Customer' -and $f.hits[0].why -match 'description') "find --text missed a description: $($f.hits | ConvertTo-Json -Compress)"

# Exit 2 (not 1) for a module that is missing or is not a module; a --module step in oml run; no leftover temp file.
$spec = Join-Path $Tmp 'names-check-spec.json'
& $Oml spec check $spec --module (Join-Path $Tmp 'no-such.oml') 2>$null | Out-Null; Require ($LASTEXITCODE -eq 2) 'spec check --module: missing module is not exit 2'
$junk = Join-Path $Tmp 'junk.oml'; Set-Content $junk 'not a module'
& $Oml spec check $spec --module $junk 2>$null | Out-Null; Require ($LASTEXITCODE -eq 2) "spec check --module: a file that is not a module gave exit $LASTEXITCODE"
Set-Content (Join-Path $Tmp 'names-run.json') (@{ steps = @(,@('spec', 'check', $spec, '--module', $consumer)) } | ConvertTo-Json -Depth 5) -Encoding utf8
$run = & $Oml run (Join-Path $Tmp 'names-run.json') 2>$null | ConvertFrom-Json
Require ($run.results[0].exitCode -eq 0 -and $run.results[0].output.ok) "spec check --module inside oml run: $($run | ConvertTo-Json -Compress -Depth 6)"
$temp = [IO.Path]::GetTempPath(); $left = @(Get-ChildItem $temp -Filter 'oml-*.oml' -ErrorAction SilentlyContinue).Count
$c = SpecCheck '{ "entities": [{ "name": "Gadget", "attributes": [{ "name": "Label" }] }], "serverActions": [{ "name": "UseGadget", "flow": [{ "kind": "execute", "action": "NamesLegacy.Badge" }] }] }'
Require ($c.Exit -eq 0 -and $c.Report.ok) "spec check --module with a data phase: $($c.Report | ConvertTo-Json -Compress -Depth 5)"
Require (@(Get-ChildItem $temp -Filter 'oml-*.oml' -ErrorAction SilentlyContinue).Count -eq $left) 'spec check --module left an oml-*.oml file in TEMP'
