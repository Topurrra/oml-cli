param($Oml, $Tmp, $Source)
$ErrorActionPreference = 'Stop'
$producer = Join-Path $Tmp 'references-producer.oml'
$hash = (Get-FileHash $producer).Hash
$spec = Join-Path $Tmp 'references-check.json'
@{ references = @(@{ producer = $producer; elements = @('Customer') }) } | ConvertTo-Json -Depth 8 | Set-Content $spec
$partial = Join-Path $Tmp 'references-partial.oml'
$r = & $Oml apply $Source $spec $partial | ConvertFrom-Json
if (-not $r.saved) { throw 'partial import failed' }
$extended = Join-Path $Tmp 'references-extended.oml'
$r = & $Oml apply $partial (Join-Path $PSScriptRoot 'references.json') $extended | ConvertFrom-Json
if (-not $r.saved -or $r.existing -notcontains 'reference ReferenceProducer.Customer') { throw 'extension failed' }
$inspection = & $Oml inspect $extended | ConvertFrom-Json
$refs = @($inspection.references | Where-Object module -eq 'ReferenceProducer')
if ($refs.Count -ne 1) { throw 'producer duplicated' }
foreach ($name in @('Customer','OrderDto','GetCustomerById','CustomerCard','SupportRole','ClientHello')) {
  if ($refs[0].elements.name -notcontains $name) { throw "missing reference $name" }
}
$tree = & $Oml tree $extended UseProducer | ConvertFrom-Json | ConvertTo-Json -Depth 80
foreach ($name in @('GetCustomerById','CheckSupportRoleRole')) {
  if ($tree -notmatch ('"ref":\s*"' + $name + '"')) { throw "missing execute target $name" }
}
$repeat = Join-Path $Tmp 'references-full-repeat.oml'
$r = & $Oml apply $extended (Join-Path $PSScriptRoot 'references.json') $repeat | ConvertFrom-Json
if (-not $r.saved -or $r.created.Count -ne 0) { throw 'repeat of full spec added elements' }
@{ references = @(@{ producer = $producer; elements = '*' }) } | ConvertTo-Json -Depth 8 | Set-Content $spec
$again = Join-Path $Tmp 'references-again.oml'
$r = & $Oml apply $extended $spec $again | ConvertFrom-Json
if (-not $r.saved -or $r.created.Count -ne 0 -or $r.existing.Count -lt 6) { throw 'repeat imported new elements' }
foreach ($name in @('PrivateAction','MissingElement')) {
  @{ references = @(@{ producer = $producer; elements = @($name) }) } | ConvertTo-Json -Depth 8 | Set-Content $spec
  $refused = Join-Path $Tmp "references-refused-$name.oml"
  $r = & $Oml apply $Source $spec $refused | ConvertFrom-Json
  if ($r.saved -or (Test-Path $refused) -or $r.problems.Count -eq 0) { throw "invalid reference $name saved" }
}
# Elements of a referenced module cannot be changed from the consumer: every write path refuses with one message, and nothing is staged.
$consumerHash = (Get-FileHash $extended).Hash
$cases = @(
  @{ key = 'entities'; json = '[{ "name": "Customer", "attributes": [{ "name": "Extra" }] }]' },
  @{ key = 'structures'; json = '[{ "name": "OrderDto", "attributes": [{ "name": "Extra" }] }]' },
  @{ key = 'serverActions'; json = '[{ "name": "GetCustomerById", "description": "changed", "flow": [] }]' },
  @{ key = 'clientActions'; json = '[{ "name": "ClientHello", "description": "changed", "flow": [] }]' },
  @{ key = 'roles'; json = '[{ "name": "SupportRole" }]' },
  @{ key = 'blocks'; json = '[{ "name": "CustomerCard", "widgets": [{ "type": "Text", "text": "x" }] }]' },
  @{ key = 'edits'; json = '[{ "op": "set", "element": "GetCustomerById", "properties": { "description": "changed" } }]' },
  @{ key = 'edits'; json = '[{ "op": "rename", "element": "ClientHello", "to": "Hello" }]' },
  @{ key = 'edits'; json = '[{ "op": "set", "element": "Customer/Name", "properties": { "description": "changed" } }]' },
  @{ key = 'edits'; json = '[{ "op": "addWidgets", "screen": "CustomerCard", "widgets": [{ "type": "Text", "text": "x" }] }]' },
  @{ key = 'edits'; json = '[{ "op": "delete", "element": "OrderDto" }]' },
  @{ key = 'edits'; json = '[{ "op": "set", "element": "ReferenceProducer.Customer", "properties": { "Description": "changed" } }]' },
  @{ key = 'edits'; json = '[{ "op": "set", "element": "OrderDto/CustomerId", "properties": { "description": "changed" } }]' },
  @{ key = 'edits'; json = '[{ "op": "set", "element": "GetCustomerById/Id", "properties": { "description": "changed" } }]' },
  @{ key = 'entities'; json = '[{ "name": "Customer", "description": "changed" }]' },
  @{ key = 'structures'; json = '[{ "name": "OrderDto", "attributes": [{ "name": "CustomerId", "description": "changed" }] }]' },
  @{ key = 'serverActions'; json = '[{ "name": "GetCustomerById", "inputs": [{ "name": "Id", "description": "changed" }] }]' })
$n = 0
foreach ($case in $cases) {
  $n++
  Set-Content $spec ('{ "' + $case.key + '": ' + $case.json + ' }')
  $refused = Join-Path $Tmp "references-consumed-$n.oml"
  $r = & $Oml apply $extended $spec $refused | ConvertFrom-Json
  if ($r.saved -or (Test-Path $refused) -or ($r.problems -join ' ') -notmatch "belongs to the referenced module ReferenceProducer; it cannot be changed here\. Change it in the producer module\.") {
    throw "change of a referenced element was not refused (case $n, $($case.key)): $($r | ConvertTo-Json -Depth 5 -Compress)"
  }
}
if ((Get-FileHash $extended).Hash -ne $consumerHash) { throw 'consumer changed' }
if ((Get-FileHash $producer).Hash -ne $hash) { throw 'producer changed' }
# The apply report starts with a summary and puts the decision lists before the long ones; --report writes the full report to a file.
Set-Content $spec '{ "serverActions": [{ "name": "WithUnused", "locals": [{ "name": "Spare", "type": "Text" }], "flow": [] }] }'
$raw = (& $Oml apply $extended $spec (Join-Path $Tmp 'report-1.oml')) -join ''
if ($raw -notmatch '^\{"summary":\{"saved":true,"newErrors":0,"reopenNewErrors":0,"newWarnings":\d+,"problems":0\},"saved":true') { throw "apply report does not start with the summary: $($raw.Substring(0, 200))" }
if ($raw.IndexOf('"newWarnings":[') -lt 0 -or $raw.IndexOf('"newWarnings":[') -gt $raw.IndexOf('"created"')) { throw 'newWarnings is not before created' }
$reportFile = Join-Path $Tmp 'report-2.json'
$short = (& $Oml apply $extended $spec (Join-Path $Tmp 'report-2.oml') --report $reportFile) -join '' | ConvertFrom-Json
$full = Get-Content -Raw $reportFile | ConvertFrom-Json
if (-not $short.summary.saved -or $short.created -or $short.fullReport -notlike "*report-2.json*" -or $full.created -notcontains 'serverAction WithUnused' -or $full.summary.newWarnings -ne $short.summary.newWarnings -or -not $short.newWarnings) { throw "--report: $($short | ConvertTo-Json -Depth 4 -Compress)" }
$null = & $Oml apply $extended $spec (Join-Path $Tmp 'report-3.oml') --report $reportFile 2>&1
if ($LASTEXITCODE -ne 2 -or (Test-Path (Join-Path $Tmp 'report-3.oml'))) { throw 'an existing --report file was accepted' }
Set-Content $spec '{ "entities": [{ "name": "Customer" }] }'
$raw = (& $Oml apply $extended $spec (Join-Path $Tmp 'report-4.oml')) -join ''
if ($raw -notmatch '^\{"summary":\{"saved":false,"newErrors":0,"reopenNewErrors":0,"newWarnings":0,"problems":1\}') { throw "refused apply summary: $raw" }
if ($raw -match '"created"|Customer2') { throw "refused apply keeps created or the renamed copy: $raw" }
# A screen, a static entity or a rename that takes a referenced element's name is refused too, without a renamed copy.
$prod2 = Join-Path $Tmp 'consumed-producer.oml'
& $Oml new (Join-Path $Tmp 'consumed-blank.oml') --name ScreenProd --kind CrossDevice | Out-Null
Set-Content $spec '{ "screens": [{ "name": "ShadowScreen", "flow": "ProdFlow", "widgets": [{ "type": "Text", "text": "x" }] }], "serverActions": [{ "name": "ProdAction", "public": true, "flow": [] }], "staticEntities": [{ "name": "ProdStatic", "public": true, "attributes": [{ "name": "Code" }], "records": [{ "identifier": "A", "values": { "Code": "\"a\"" } }] }],
  "edits": [{ "op": "set", "element": "ShadowScreen", "properties": { "public": true } }] }'
$r = & $Oml apply (Join-Path $Tmp 'consumed-blank.oml') $spec $prod2 | ConvertFrom-Json
if (-not $r.saved) { throw "screen producer: $($r | ConvertTo-Json -Depth 5 -Compress)" }
$consumerSpec = @{ references = @(@{ producer = $prod2; elements = '*' }) } | ConvertTo-Json -Depth 5
Set-Content $spec $consumerSpec
$consumer2 = Join-Path $Tmp 'consumed-consumer.oml'
$r = & $Oml apply $Source $spec $consumer2 | ConvertFrom-Json
if (-not $r.saved) { throw "screen consumer: $($r | ConvertTo-Json -Depth 5 -Compress)" }
$n = 0
foreach ($case in @(
  '{ "screens": [{ "name": "ShadowScreen", "flow": "Other", "widgets": [{ "type": "Text", "text": "x" }] }] }',
  '{ "staticEntities": [{ "name": "ProdStatic", "attributes": [{ "name": "Code" }], "records": [{ "identifier": "B", "values": { "Code": "\"b\"" } }] }] }',
  '{ "serverActions": [{ "name": "Mine", "flow": [] }], "edits": [{ "op": "rename", "element": "Mine", "to": "ProdAction" }] }')) {
  $n++
  Set-Content $spec $case
  $refused = Join-Path $Tmp "consumed-case-$n.oml"
  $r = & $Oml apply $consumer2 $spec $refused | ConvertFrom-Json
  if ($r.saved -or (Test-Path $refused) -or ($r.problems -join ' ') -notmatch 'belongs to the referenced module ScreenProd; it cannot be changed here\. Change it in the producer module\.' -or $r.created -or (ConvertTo-Json $r) -match 'ProdStatic2|ShadowScreen2') {
    throw "consumed case $n was not refused cleanly: $($r | ConvertTo-Json -Depth 5 -Compress)"
  }
}
# --report must be a new file: not the source, the staged output or a spec.
foreach ($same in @($Source, $spec)) {
  $null = & $Oml apply $Source $spec (Join-Path $Tmp 'report-same.oml') --report $same 2>&1
  if ($LASTEXITCODE -ne 2) { throw "--report $same was accepted" }
}
Write-Host 'ok   reference extension, wiring, repeat, refusal, changes to referenced elements refused, producer hash'
