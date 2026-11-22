# oml model-coverage: the Model API coverage map in tests/model-api is the one this oml and this Service Studio build
# give. When oml's apply code or Service Studio changes, regenerate it (oml model-coverage --out tests/model-api) and
# read the diff: members that went from covered to not-covered are a regression.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($condition, $message) { if (-not $condition) { throw "model-coverage: $message" } }

$out = Join-Path $Tmp 'model-coverage'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$result = & $Oml model-coverage --out $out | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
Require ($result.total.types -gt 300 -and $result.total.members -gt 2000) "too few types or members: $($result.total | ConvertTo-Json -Compress)"
$new = Get-Content -Raw (Join-Path $out 'coverage.json') | ConvertFrom-Json
$committed = Join-Path $PSScriptRoot 'model-api/coverage.json'
$old = Get-Content -Raw $committed | ConvertFrom-Json
if ($new.build -ne $old.build) { Write-Host "note model-coverage: committed map is for $($old.build), this build is $($new.build); regenerate it"; return }

# Every member the map says is covered must still be covered.
$was = @{}
foreach ($t in $old.types) { foreach ($m in $t.members) { if ($m.status -eq 'covered') { $was["$($t.type).$($m.signature)"] = $true } } }
$lost = @()
foreach ($t in $new.types) { foreach ($m in $t.members) { $k = "$($t.type).$($m.signature)"; if ($was.ContainsKey($k) -and $m.status -ne 'covered') { $lost += $k } } }
Require ($lost.Count -eq 0) "covered before, not now: $($lost -join '; ')"
$same = ((Get-Content -Raw (Join-Path $out 'coverage.json')) -replace "`r", '') -eq ((Get-Content -Raw $committed) -replace "`r", '')
if (-not $same) { Write-Host "note model-coverage: the map changed (nothing lost); regenerate tests/model-api with oml model-coverage --out tests/model-api" }
Write-Host "ok   model-coverage: $($new.total.covered) of $($new.total.changes - $new.total.'out-of-scope') changes covered ($($new.total.coveredPercent)%), nothing lost"
