param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$work = Join-Path $Tmp ('flowlayout-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
function RunJson([string[]]$CliArgs) {
  $result = & $Oml @CliArgs | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) { throw ($result | ConvertTo-Json -Depth 12) }
  return $result
}
function Require($condition, [string]$message) { if (-not $condition) { throw $message } }
function Positions([string]$file, [string]$path) {
  $tree = RunJson @('tree',$file,$path)
  return @($tree.children | Where-Object { $null -ne $_.horizontalPosition } | Sort-Object path |
    Select-Object path,horizontalPosition,verticalPosition) | ConvertTo-Json -Depth 8 -Compress
}
function CheckFlow([string]$file, [string]$path) {
  $tree = RunJson @('tree', $file, $path)
  $nodes = @($tree.children | Where-Object { $null -ne $_.horizontalPosition -and $_.kind -ne 'Comment' })
  Require ($nodes.Count -ge 2) "No flow nodes: $path"
  for ($i = 0; $i -lt $nodes.Count; $i++) {
    for ($j = $i + 1; $j -lt $nodes.Count; $j++) {
      Require ([Math]::Abs($nodes[$i].horizontalPosition - $nodes[$j].horizontalPosition) -ge 914 -or
               [Math]::Abs($nodes[$i].verticalPosition - $nodes[$j].verticalPosition) -ge 928) "Overlapping nodes: $path"
    }
  }
  $start = @($nodes | Where-Object kind -eq 'Start')
  if ($start.Count -eq 1) {
    Require (@($nodes | Where-Object { $_.kind -ne 'Start' -and $_.verticalPosition -le $start[0].verticalPosition }).Count -eq 0) "Start is not on top: $path"
  }
}
# Reuse the full regression fixtures when available; also support a standalone run.
foreach ($case in @(
  @{fixture='logic.json'; kind='CrossDevice'; paths=@('CreateOrderFor','SumCustomerOrders','FormatTotal')},
  @{fixture='service.json'; kind='Service'; paths=@('AddItem')},
  @{fixture='platform.json'; kind='CrossDevice'; paths=@('ApproveOrder','ApproveOrder/RecordReview')},
  @{fixture='web.json'; kind='WebResponsive'; paths=@('MainFlow/WebItems/WebPreparation[0]','MainFlow/WebItems/Save','Common/WebCard/WebPreparation[0]')}
)) {
  $built = Join-Path $Tmp ('out-' + $case.fixture + '.oml')
  if (-not (Test-Path $built)) {
    $blank = Join-Path $work ($case.fixture + '-blank.oml')
    $built = Join-Path $work ($case.fixture + '.oml')
    RunJson @('new',$blank,'--name','LayoutCheck','--kind',$case.kind) | Out-Null
    RunJson @('apply',$blank,(Join-Path $PSScriptRoot $case.fixture),$built) | Out-Null
  }
  foreach ($path in $case.paths) { CheckFlow $built $path }
}
$blank = Join-Path $work 'blank.oml'; $built = Join-Path $work 'built.oml'
$spec = Join-Path $work 'spec.json'; $tidy = Join-Path $work 'tidy.oml'
RunJson @('new',$blank,'--name','LayoutSelection','--kind','CrossDevice') | Out-Null
Set-Content $spec '{"clientActions":[{"name":"FxOne","flow":[{"kind":"javaScript","javaScript":"void 0;"}]},{"name":"Keep"}]}'
RunJson @('apply',$blank,$spec,$built) | Out-Null
$keep = Positions $built 'Keep'
Set-Content $spec '{"tidyFlows":["Fx*"]}'
Require ((RunJson @('apply',$built,$spec,$tidy)).saved) 'tidyFlows did not save'
CheckFlow $tidy 'FxOne'
Require ((Positions $tidy 'Keep') -eq $keep) 'tidyFlows moved an unmatched flow'
Set-Content $spec '{"tidyFlows":["NoSuchFlow*"]}'
$bad = Join-Path $work 'bad.oml'
$r = & $Oml apply $built $spec $bad | ConvertFrom-Json
Require ($LASTEXITCODE -eq 1 -and -not $r.saved -and -not (Test-Path $bad)) 'Unmatched tidyFlows pattern was accepted'
Write-Host 'ok   flow layout: action kinds, preparation, BPT and nested flows, sizes, selection'
