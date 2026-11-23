# Checks `oml apply <module.oml> <spec.json>` without a staged path: refused unless the project lets install run without
# asking (autoApprove.install on, commands.install not off); then the module itself changes after a backup, a failing
# spec leaves it as it was, a staged path still works, and `oml apply --batch` takes items without "staged".
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Tmp = New-TestFolder $Tmp 'inplace'
try {
function Require($condition, $message) { if (-not $condition) { throw $message } }
function Hash($path) { (Get-FileHash $path -Algorithm SHA256).Hash }
function Oml-Json([string[]]$Arguments) {
  $text = & $Oml @Arguments 2>&1
  $json = $null; try { $json = ($text -join "`n") | ConvertFrom-Json } catch { }
  [pscustomobject]@{ Code = $LASTEXITCODE; Json = $json; Text = ($text -join "`n") }
}
function Baks($path) { @(Get-ChildItem (Split-Path $path) -Filter ((Split-Path $path -Leaf) + '.*.bak')) }

$dir = Join-Path $Tmp ('p-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $dir | Out-Null
Push-Location $dir
try {
  $m = Join-Path $dir 'Shop.oml'
  & $Oml new $m --name InPlaceShop --kind CrossDevice | Out-Null
  Set-Content (Join-Path $dir 'a.json') '{ "entities": [ { "name": "Basket", "attributes": [ { "name": "Label", "type": "Text" } ] } ] }' -Encoding utf8
  Set-Content (Join-Path $dir 'b.json') '{ "entities": [ { "name": "Coupon", "attributes": [ { "name": "Code", "type": "Text" } ] } ] }' -Encoding utf8
  Set-Content (Join-Path $dir 'bad.json') '{ "entitiez": [] }' -Encoding utf8

  # 1. No project config: install needs the user's yes, so no staged path is refused and nothing is written.
  $h0 = Hash $m
  $r = Oml-Json @('apply', $m, 'a.json')
  Require ($r.Code -eq 2 -and $r.Text -match 'autoApprove.install is off' -and (Hash $m) -eq $h0 -and (Baks $m).Count -eq 0) "no config: in place was not refused: $($r.Code) $($r.Text)"
  Write-Host 'ok   apply in place: refused while install needs the user''s yes'

  # 2. autoApprove.install on: the module itself changes, after a backup of what it was.
  & $Oml config set autoApprove.install on | Out-Null
  $r = Oml-Json @('apply', $m, 'a.json')
  Require ($r.Code -eq 0 -and $r.Json.summary.saved -eq $true -and $r.Json.installed -eq $m -and $null -eq $r.Json.staged) "in place: $($r.Text)"
  $bak = $r.Json.backup
  Require ((Test-Path $bak) -and (Hash $bak) -eq $h0 -and (Hash $m) -ne $h0 -and (Baks $m).Count -eq 1) 'in place: the backup is not the module as it was, or the module did not change'
  Require ($r.Json.next -match 'oml review' -and $r.Json.next -notmatch 'oml install') "in place: next $($r.Json.next)"
  & $Oml describe $m Basket | Out-Null
  Require ($LASTEXITCODE -eq 0) 'in place: Basket is not in the module'
  $rv = Oml-Json @('review', $bak, $m)
  Require ($rv.Code -eq 0) "review <backup> <module>: exit $($rv.Code) $($rv.Text)"
  Write-Host 'ok   apply in place: the module changes, the backup is the old module, review shows the change'

  # 3. A failing spec leaves the module as it was, with no new backup.
  $h1 = Hash $m
  $r = Oml-Json @('apply', $m, 'bad.json')
  Require ($r.Code -eq 1 -and $r.Json.summary.saved -eq $false -and (Hash $m) -eq $h1 -and (Baks $m).Count -eq 1) "in place, failing spec: $($r.Text)"
  Write-Host 'ok   apply in place: a failing spec changes nothing'

  # 4. A staged path still writes a staged copy and leaves the module alone.
  $staged = Join-Path $dir 'Shop.staged.oml'
  $r = Oml-Json @('apply', $m, 'b.json', $staged)
  Require ($r.Code -eq 0 -and $r.Json.staged -eq $staged -and $null -eq $r.Json.installed -and (Hash $m) -eq $h1 -and (Test-Path $staged)) "staged path with in place allowed: $($r.Text)"
  Write-Host 'ok   apply with a staged path still stages'

  # 5. Batch: an item without "staged" changes its module in place.
  $n = Join-Path $dir 'Other.oml'
  & $Oml new $n --name InPlaceOther --kind CrossDevice | Out-Null
  $hn = Hash $n
  Set-Content (Join-Path $dir 'index.json') (@{ items = @(@{ source = 'Other.oml'; spec = 'b.json' }) } | ConvertTo-Json -Depth 5) -Encoding utf8
  $r = Oml-Json @('apply', '--batch', (Join-Path $dir 'index.json'))
  Require ($r.Code -eq 0 -and $r.Json.summary.saved -eq 1 -and $r.Json.items[0].installed -eq $n -and (Test-Path $r.Json.items[0].backup) -and (Hash $n) -ne $hn -and $r.Json.next -match 'changed in place') "batch in place: $($r.Text)"
  Write-Host 'ok   batch apply: an item without staged changes its module in place'

  # 6. Install turned off: in place is refused again.
  & $Oml config set commands.install off | Out-Null
  $h2 = Hash $m
  $r = Oml-Json @('apply', $m, 'b.json')
  Require ($r.Code -eq 2 -and (Hash $m) -eq $h2) "commands.install off: in place was not refused: $($r.Text)"
  $r = Oml-Json @('apply', '--batch', (Join-Path $dir 'index.json'))
  Require ($r.Code -eq 2) "commands.install off: batch in place was not refused: $($r.Text)"
  Write-Host 'ok   apply in place: refused while install is turned off'
}
finally { Pop-Location }
}
finally { Remove-TestFolder $Tmp }
