# -Part i -Parts n: run only every n-th case (i = 0..n-1), so n processes can share the cases. Cases that later cases build on always run.
param([string]$Oml, [string]$Tmp, [int]$Part = 0, [int]$Parts = 1)
$ErrorActionPreference = 'Stop'
function Require($ok, $message) { if (-not $ok) { throw $message } }
$script:index = 0
function Case($source, $spec, $allowed, $label, [switch]$Always) {
  $path = Join-Path $Tmp 'rules-case.json'
  $out = Join-Path $Tmp "rules-case-$script:index.oml"
  $script:index++
  if (-not $Always -and (($script:index - 1) % $Parts) -ne $Part) { return $out }
  $spec | ConvertTo-Json -Depth 50 | Set-Content $path
  $r = & $Oml apply $source $path $out | ConvertFrom-Json
  if ($allowed) {
    Require ($LASTEXITCODE -eq 0 -and $r.saved -and $r.reopenNewErrors.Count -eq 0) "$label failed: $($r | ConvertTo-Json -Depth 12)"
  } else {
    Require ($LASTEXITCODE -eq 1 -and -not $r.saved -and -not (Test-Path $out) -and ($r.problems -join ' ') -match 'refused:.*manually in Service Studio if you really want to\.') "$label refusal failed: $($r | ConvertTo-Json -Depth 12)"
  }
  return $out
}
$source = Join-Path $Tmp 'rules-source.oml'
$r = & $Oml apply (Join-Path $Tmp 'r0.oml') (Join-Path $PSScriptRoot 'rules.json') $source | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $r.saved) "rules source: $($r | ConvertTo-Json -Depth 12)"
$before = (Get-FileHash $source).Hash
foreach ($target in @('RuleEntity','RuleEntity/Value','RuleStatus','RuleStatus/Color','RuleDto','RuleDto/Caption',
  'RuleAction','RuleAction/Input','RuleAction/Output','RuleClient','RuleClient/Input','RuleClient/Output',
  'RuleScreen','RuleBlock','RuleError','RuleRole','RuleProperty','RuleTimer','RuleApi','RuleApi/GetValue',
  'RuleService','RuleService/GetValue','RuleProcess')) {
  foreach ($op in @('delete','rename')) {
    Case $source @{edits=@(@{op=$op;element=$target;to='Renamed';allowDataLoss=$true})} $false "$op $target" | Out-Null
  }
  Case $source @{edits=@(@{op='set';element=$target;properties=@{nAmE='Renamed'}})} $false "set Name $target" | Out-Null
}
foreach ($target in @('RuleEntity/Value','RuleStatus/Color','RuleDto/Caption')) {
  foreach ($pair in @(@('DataType','Integer'),@('Length',12),@('Decimals',2),@('IsMandatory',$true))) {
    Case $source @{edits=@(@{op='set';element=$target;properties=@{$pair[0]=$pair[1]}})} $false "R5 $target $($pair[0])" | Out-Null
  }
}
foreach ($word in @('DELETE','uPdAtE','TRUNCATE','drop','ALTER','MERGE')) {
  $sql = "SELECT 'ok'; $word {RuleEntity}"
  Case $source @{serverActions=@(@{name='Unsafe';flow=@(@{kind='sql';name='ChangeRows';sql=$sql;outputs=@('RuleDto')})})} $false "new SQL $word" | Out-Null
  Case $source @{edits=@(@{op='set';element='RuleAction/Read';properties=@{sTaTeMeNt=$sql}})} $false "existing SQL $word" | Out-Null
  Case $source @{edits=@(@{op='insertNodes';action='RuleAction';after='Start';flow=@(@{kind='sql';name='ChangeRows';sql=$sql;outputs=@('RuleDto')})})} $false "insert SQL $word" | Out-Null
}
Case $source @{entities=@(@{name='Disposable';attributes=@(@{name='Value'})});edits=@(@{op='rename';element='Disposable';to='Temporary'},@{op='delete';element='Temporary';allowDataLoss=$true})} $true 'R2 same-spec entity' | Out-Null
Case $source @{edits=@(@{op='rename';element='RuleAction/Copy';to='CopyAgain'},@{op='delete';element='RuleAction/CopyAgain'},@{op='rename';element='RuleAction/Scratch';to='Temporary'},@{op='delete';element='RuleAction/Temporary'})} $true 'R3 flow and locals' | Out-Null
$added = Case $source @{entities=@(@{name='RuleEntity';attributes=@(@{name='Added'})});edits=@(@{op='set';element='RuleEntity/Added';properties=@{Length=120}})} $true 'R5 new attribute' -Always
Case $added @{edits=@(@{op='delete';element='RuleEntity/Added';allowDataLoss=$true})} $false 'new attribute becomes existing next apply' | Out-Null
Case $source @{edits=@(@{op='set';element='RuleAction/Read';properties=@{Statement="SELECT 'DELETE '' UPDATE', 'MERGE' /* DROP /* ALTER */ TRUNCATE */ -- DELETE`n"}})} $true 'R6 SELECT with comments and strings' | Out-Null
Case $source @{serverActions=@(@{name='InsertRows';flow=@(@{kind='sql';name='AddRow';sql="INSERT INTO {RuleEntity} ([Value]) VALUES ('UPDATE')";outputs=@('RuleDto')})})} $true 'R6 INSERT' | Out-Null
# Service actions require a Service module.
$service = Case (Join-Path $Tmp 's0.oml') @{serviceActions=@(@{name='RuleServiceAction';inputs=@(@{name='Input'});outputs=@(@{name='Output'})})} $true 'service seed' -Always
foreach ($target in @('RuleServiceAction','RuleServiceAction/Input','RuleServiceAction/Output')) {
  foreach ($op in @('rename','delete')) { Case $service @{edits=@(@{op=$op;element=$target;to='Renamed'})} $false "service $op $target" | Out-Null }
}
Require ((Get-FileHash $source).Hash -eq $before) 'rules changed source'
Write-Host "ok   R2/R3/R5/R6 refusals and allowed cases (part $Part of $Parts)"
