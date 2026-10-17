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
if ((Get-FileHash $producer).Hash -ne $hash) { throw 'producer changed' }
Write-Host 'ok   reference extension, wiring, repeat, refusal, producer hash'
