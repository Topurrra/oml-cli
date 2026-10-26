# Checks `oml apply --batch <index.json>` and `oml install --batch <index.json>`: all saved, one failing item,
# an existing staged file, a bad index (exit 2, nothing written), the same fields as a single apply, a batch inside
# `oml run` (the run keeps its module sharing), and an install batch that is all or nothing on its checks.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($condition, $message) { if (-not $condition) { throw $message } }
function Hash($path) { (Get-FileHash $path -Algorithm SHA256).Hash }
function Oml-Json([string[]]$Arguments) {
  $text = & $Oml @Arguments 2>$null
  [pscustomobject]@{ Code = $LASTEXITCODE; Json = (($text -join "`n") | ConvertFrom-Json) }
}
$dir = Join-Path $Tmp ('batch-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $dir | Out-Null

# Three small modules and the specs that add one entity each. Paths in the index are relative to its folder.
$names = 'A', 'B', 'C'
foreach ($n in $names) {
  & $Oml new (Join-Path $dir "$n.oml") --name "Batch$n" --kind CrossDevice | Out-Null
  Set-Content (Join-Path $dir "$n.json") ('{ "entities": [ { "name": "Widget' + $n + '", "attributes": [ { "name": "Label", "type": "Text" } ] } ] }') -Encoding utf8
}
function Write-Index($file, $items) { Set-Content (Join-Path $dir $file) (@{ items = $items } | ConvertTo-Json -Depth 5) -Encoding utf8 }
function Item($n, $staged) { @{ source = "$n.oml"; spec = "$n.json"; staged = $staged } }

# 1. All saved, with the same fields as a single apply.
Write-Index 'all.json' @((Item 'A' 'out/A1.oml'), (Item 'B' 'out/B1.oml'), (Item 'C' 'out/C1.oml'))
New-Item -ItemType Directory -Force (Join-Path $dir 'out') | Out-Null
$single = Oml-Json @('apply', (Join-Path $dir 'A.oml'), (Join-Path $dir 'A.json'), (Join-Path $dir 'A-single.oml'))
$all = Oml-Json @('apply', '--batch', (Join-Path $dir 'all.json'))
Require ($all.Code -eq 0) "batch: exit $($all.Code), expected 0"
$s = $all.Json.summary
Require ($s.items -eq 3 -and $s.saved -eq 3 -and $s.failed -eq 0 -and $s.newErrors -eq 0 -and $s.created -eq (3 * @($single.Json.created).Count) -and $s.changed -eq 0) "batch: summary $($s | ConvertTo-Json -Compress)"
Require (@($all.Json.items).Count -eq 3) 'batch: item count'
foreach ($n in $names) { Require (Test-Path (Join-Path $dir "out/${n}1.oml")) "batch: staged ${n}1.oml missing" }
$first = $all.Json.items[0]
Require ($first.source -eq (Join-Path $dir 'A.oml') -and $first.staged -eq (Join-Path $dir 'out/A1.oml').Replace('/', '\') -and $first.saved -eq $true) "batch: item 1 source and staged $($first | ConvertTo-Json -Compress)"
Require ((($first.created | ConvertTo-Json -Compress) -ceq ($single.Json.created | ConvertTo-Json -Compress)) -and $first.summary.newErrors -eq $single.Json.summary.newErrors -and $first.baselineErrors -eq $single.Json.baselineErrors) 'batch: item 1 differs from a single apply'
Require ($single.Json.next -and $null -eq $first.next -and $all.Json.next -like 'Run oml review*install --batch*') "batch: one next for the batch, none per item: $($all.Json.next)"
Write-Host 'ok   batch apply: 3 items saved, summary, same fields as a single apply'

# 2. One failing item (an unknown spec key): the others are still saved; exit 1.
Set-Content (Join-Path $dir 'bad.json') '{ "entitiez": [] }' -Encoding utf8
Write-Index 'one-bad.json' @((Item 'A' 'out/A2.oml'), @{ source = 'B.oml'; spec = 'bad.json'; staged = 'out/B2.oml' }, (Item 'C' 'out/C2.oml'))
$bad = Oml-Json @('apply', '--batch', (Join-Path $dir 'one-bad.json'))
Require ($bad.Code -eq 1 -and $bad.Json.summary.saved -eq 2 -and $bad.Json.summary.failed -eq 1 -and $bad.Json.items[1].saved -eq $false) "batch: one failing item: exit $($bad.Code), $($bad.Json.summary | ConvertTo-Json -Compress)"
Require ((Test-Path (Join-Path $dir 'out/A2.oml')) -and (Test-Path (Join-Path $dir 'out/C2.oml')) -and -not (Test-Path (Join-Path $dir 'out/B2.oml'))) 'batch: the items around the failing one must be saved, the failing one not'
# A missing source is an item error (exit 1 for the batch), not a bad index.
Write-Index 'no-source.json' @((Item 'A' 'out/A3.oml'), @{ source = 'none.oml'; spec = 'B.json'; staged = 'out/B3.oml' })
$none = Oml-Json @('apply', '--batch', (Join-Path $dir 'no-source.json'))
Require ($none.Code -eq 1 -and $none.Json.summary.saved -eq 1 -and $none.Json.items[1].error) "batch: a missing source: exit $($none.Code)"
Write-Host 'ok   batch apply: a failing item is recorded and the rest are saved'

# 3. An existing staged file is never overwritten (same refusal as a single apply).
$keep = Join-Path $dir 'out/A1.oml'; $before = Hash $keep
Write-Index 'exists.json' @((Item 'A' 'out/A1.oml'), (Item 'B' 'out/B4.oml'))
$ex = Oml-Json @('apply', '--batch', (Join-Path $dir 'exists.json'))
Require ($ex.Code -eq 1 -and $ex.Json.items[0].error -match 'already exists' -and $ex.Json.items[1].saved -eq $true -and (Hash $keep) -eq $before) 'batch: an existing staged file was touched or not refused'
Write-Host 'ok   batch apply: an existing staged file is refused and unchanged'

# 4. A bad index is exit 2 and writes nothing.
Set-Content (Join-Path $dir 'broken.json') '{ "items": [ ' -Encoding utf8
Write-Index 'dup.json' @((Item 'A' 'out/D1.oml'), (Item 'B' 'out/D1.oml'))
Write-Index 'nokey.json' @(@{ source = 'A.oml'; staged = 'out/D2.oml' })
Write-Index 'extra.json' @(@{ source = 'A.oml'; spec = 'A.json'; staged = 'out/D3.oml'; stage = 'x' })
Write-Index 'empty.json' @()
Write-Index 'clash.json' @((Item 'A' 'out/D4.oml'), @{ source = 'out/D4.oml'; spec = 'B.json'; staged = 'out/D5.oml' })
foreach ($file in 'broken.json', 'dup.json', 'nokey.json', 'extra.json', 'empty.json', 'clash.json', 'missing-index.json') {
  $r = Oml-Json @('apply', '--batch', (Join-Path $dir $file))
  Require ($r.Code -eq 2 -and $r.Json.error) "batch: bad index $file : exit $($r.Code)"
}
Require (@(Get-ChildItem $dir -Recurse -Filter 'D*.oml').Count -eq 0) 'batch: a bad index wrote a file'
Write-Host 'ok   batch apply: bad index (bad JSON, duplicate staged, missing key, unknown key, empty, staged is a source, missing file) exits 2 and writes nothing'

# 5. Inside `oml run`: allowed, and the run keeps working (sharing is not ended by the batch).
Write-Index 'run.json' @((Item 'A' 'out/A5.oml'), (Item 'B' 'out/B5.oml'))
$stepsFile = Join-Path $dir 'steps.json'
Set-Content $stepsFile (@{ steps = @(@('inspect', (Join-Path $dir 'A.oml'), '--only', 'entities'), @('apply', '--batch', (Join-Path $dir 'run.json')), @('inspect', (Join-Path $dir 'out/A5.oml'), '--only', 'entities'), @('inspect', (Join-Path $dir 'A.oml'), '--only', 'entities')) } | ConvertTo-Json -Depth 5) -Encoding utf8
$run = Oml-Json @('run', $stepsFile)
$res = $run.Json.results
Require ($run.Code -eq 0 -and $res.Count -eq 4 -and $res[1].exitCode -eq 0 -and $res[1].output.summary.saved -eq 2 -and $res[2].exitCode -eq 0 -and $res[3].exitCode -eq 0) "batch: in oml run: exit $($run.Code) $((($res | ForEach-Object exitCode) -join ','))"
Write-Host 'ok   batch apply: allowed in oml run, later steps still work'

# 6. Install batch. Targets are copies inside this folder; nothing outside -Tmp is touched.
foreach ($n in $names) { Copy-Item (Join-Path $dir "$n.oml") (Join-Path $dir "T$n.oml") }
$hashes = @{}; foreach ($n in $names) { $hashes[$n] = Hash (Join-Path $dir "T$n.oml") }
function Install-Index($file, $items) { Set-Content (Join-Path $dir $file) (@{ items = $items } | ConvertTo-Json -Depth 5) -Encoding utf8 }
# One staged file is not a module: nothing may be installed, every refusal is listed.
Set-Content (Join-Path $dir 'notmodule.oml') 'not a module' -Encoding utf8
Copy-Item (Join-Path $dir 'A.oml') (Join-Path $dir 'TX.oml')
(Get-Item (Join-Path $dir 'TC.oml')).IsReadOnly = $true
Install-Index 'inst-bad.json' @(@{ staged = 'out/A1.oml'; target = 'TA.oml' }, @{ staged = 'notmodule.oml'; target = 'TB.oml' }, @{ staged = 'out/C1.oml'; target = 'TC.oml' })
$ib = Oml-Json @('install', '--batch', (Join-Path $dir 'inst-bad.json'))
(Get-Item (Join-Path $dir 'TC.oml')).IsReadOnly = $false
Require ($ib.Code -eq 1 -and $ib.Json.error -match 'Nothing installed' -and @($ib.Json.items).Count -eq 2 -and $ib.Json.summary.installed -eq 0 -and $ib.Json.summary.refused -eq 2) "install batch: refusal: exit $($ib.Code) $($ib.Json | ConvertTo-Json -Compress -Depth 5)"
foreach ($n in $names) { Require ((Hash (Join-Path $dir "T$n.oml")) -eq $hashes[$n]) "install batch: target T$n changed although an item was refused" }
Require (@(Get-ChildItem $dir -Filter '*.bak').Count -eq 0) 'install batch: a backup was made although nothing was installed'
Write-Host 'ok   batch install: one refused item -> nothing installed, every refusal listed, targets unchanged'
# A bad index (duplicate target) is exit 2.
Install-Index 'inst-dup.json' @(@{ staged = 'out/A1.oml'; target = 'TA.oml' }, @{ staged = 'out/B1.oml'; target = 'TA.oml' })
$id = Oml-Json @('install', '--batch', (Join-Path $dir 'inst-dup.json'))
Require ($id.Code -eq 2 -and (Hash (Join-Path $dir 'TA.oml')) -eq $hashes['A']) 'install batch: duplicate target was not a bad index'
# All pass.
Install-Index 'inst-ok.json' @(@{ staged = 'out/A1.oml'; target = 'TA.oml' }, @{ staged = 'out/B1.oml'; target = 'TB.oml' }, @{ staged = 'out/C1.oml'; target = 'TC.oml' })
$ok = Oml-Json @('install', '--batch', (Join-Path $dir 'inst-ok.json'))
Require ($ok.Code -eq 0 -and $ok.Json.summary.items -eq 3 -and $ok.Json.summary.installed -eq 3 -and $ok.Json.summary.failed -eq 0) "install batch: all pass: exit $($ok.Code) $($ok.Json | ConvertTo-Json -Compress -Depth 5)"
foreach ($n in $names) {
  $item = $ok.Json.items | Where-Object { $_.target -eq (Join-Path $dir "T$n.oml") }
  Require ($item -and (Hash (Join-Path $dir "T$n.oml")) -eq (Hash (Join-Path $dir "out/${n}1.oml")) -and $item.sha256 -eq (Hash (Join-Path $dir "T$n.oml")) -and (Hash $item.backup) -eq $hashes[$n]) "install batch: T$n is not the staged file, or its backup is not the old target"
}
Write-Host 'ok   batch install: all items installed with a backup each'
# A write that fails stops the batch: earlier items stay installed, the failing target is unchanged, later items are skipped.
foreach ($n in $names) { Copy-Item (Join-Path $dir "$n.oml") (Join-Path $dir "U$n.oml") }
$uhash = @{}; foreach ($n in $names) { $uhash[$n] = Hash (Join-Path $dir "U$n.oml") }
Install-Index 'inst-lock.json' @(@{ staged = 'out/A1.oml'; target = 'UA.oml' }, @{ staged = 'out/B1.oml'; target = 'UB.oml' }, @{ staged = 'out/C1.oml'; target = 'UC.oml' })
$lock = [IO.File]::Open((Join-Path $dir 'UB.oml'), 'Open', 'Read', 'Read')
try { $il = Oml-Json @('install', '--batch', (Join-Path $dir 'inst-lock.json')) } finally { $lock.Dispose() }
$sl = $il.Json.summary
Require ($il.Code -eq 1 -and $sl.installed -eq 1 -and $sl.failed -eq 1 -and $sl.skipped -eq 1 -and $il.Json.items[0].installed -and $il.Json.items[1].error -and $il.Json.items[2].skipped -eq $true) "install batch: write failure: exit $($il.Code) $($il.Json | ConvertTo-Json -Compress -Depth 5)"
Require ((Hash (Join-Path $dir 'UA.oml')) -eq (Hash (Join-Path $dir 'out/A1.oml')) -and (Hash (Join-Path $dir 'UB.oml')) -eq $uhash['B'] -and (Hash (Join-Path $dir 'UC.oml')) -eq $uhash['C']) 'install batch: after a failed write, UA must be installed and UB and UC unchanged'
Write-Host 'ok   batch install: a failed write stops the batch, the rest are skipped'
# The single forms still work and the unchanged-output form is kept.
$one = Oml-Json @('install', (Join-Path $dir 'out/A1.oml'), (Join-Path $dir 'TX.oml'))
Require ($one.Code -eq 0 -and $one.Json.installed -eq (Join-Path $dir 'TX.oml') -and $one.Json.backup -and $null -eq $one.Json.items) 'install: single form changed'
Write-Host 'ok   batch: single install unchanged'
