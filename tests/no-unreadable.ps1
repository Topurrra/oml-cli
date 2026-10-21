# Fails when inspect or describe of a module that oml built lists an unreadable part: every part of what oml
# builds must be readable. Describes every screen, block, entity, structure and action of each module in one oml run.
param([string]$Oml, [string[]]$Modules, [string]$Tmp)
$ErrorActionPreference = 'Stop'
foreach ($module in $Modules) {
  $inspect = & $Oml inspect $module --own
  if ($LASTEXITCODE -ne 0) { throw "inspect $module failed: $inspect" }
  if (($inspect -join '') -match '"unreadable"') { throw "inspect $module lists unreadable parts: $inspect" }
  $i = ($inspect -join "`n") | ConvertFrom-Json
  $paths = @(@($i.screens) + @($i.blocks) | Where-Object { $_ } | ForEach-Object { "$($_.flow)/$($_.name)" })
  foreach ($k in @(@('entities', 'Entity'), @('structures', 'Structure'), @('serverActions', 'ServerAction'), @('clientActions', 'ClientAction'), @('serviceActions', 'ServiceAction'))) {
    $paths += @($i.($k[0]) | Where-Object { $_ } | ForEach-Object { "$($k[1]):$($_.name)" })
  }
  if (!$paths) { continue }
  $steps = Join-Path $Tmp 'no-unreadable-steps.json'
  @{ steps = @($paths | ForEach-Object { , @('describe', $module, $_, '--depth', '1') }) } | ConvertTo-Json -Depth 5 | Set-Content $steps
  $run = (& $Oml run $steps --continue) -join "`n" | ConvertFrom-Json
  foreach ($r in $run.results) {
    if ($r.exitCode -ne 0) { throw "describe $($r.args[2]) in $module failed: $($r.output | ConvertTo-Json -Depth 8 -Compress)" }
    if ($r.output.unreadable) { throw "describe $($r.args[2]) in $module lists unreadable parts: $($r.output.unreadable | ConvertTo-Json -Depth 8 -Compress)" }
  }
}
Write-Host "ok   no unreadable parts in $($Modules.Count) modules oml built"
