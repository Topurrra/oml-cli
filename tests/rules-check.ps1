# The production rules on every protected family (skill/rules.md):
#   saves    renames, public signature changes (listed in "breaking"), entity settings, a longer length, same-spec elements
#   asks     deletes of existing elements (rule 3), data-risk attribute changes (rule 5), data-changing SQL (rule 6):
#            not saved, "needsApproval"; with -Approve the same apply with --user-approved saves
#   refused  rule 2: an existing entity or attribute deleted, an attribute renamed or retyped; with -Approve also with --user-approved
# -Part i -Parts n: run only every n-th case (i = 0..n-1), so n processes can share the cases. Cases that later cases build on always run.
param([string]$Oml, [string]$Tmp, [int]$Part = 0, [int]$Parts = 1)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'rules'
Require-TestFile (Join-Path $Fixtures 'r0.oml') 'fixture module r0.oml'
Require-TestFile (Join-Path $Fixtures 's0.oml') 'fixture module s0.oml'
try {
function Require($ok, $message) { if (-not $ok) { throw $message } }
$script:index = 0
function Run($source, $path, $out, [switch]$Approved) {
  $flags = @(if ($Approved) { '--user-approved' })
  $text = (& $Oml apply $source $path $out $flags) -join "`n"
  return [pscustomobject]@{ Exit = $LASTEXITCODE; Json = ($text | ConvertFrom-Json); Text = $text }
}
function Case($source, $spec, $expect, $label, [switch]$Approve, [string]$Says, [switch]$Always) {
  $path = Join-Path $Tmp 'rules-case.json'
  $out = Join-Path $Tmp "rules-case-$script:index.oml"
  $script:index++
  if (-not $Always -and (($script:index - 1) % $Parts) -ne $Part) { return $out }
  $spec | ConvertTo-Json -Depth 50 | Set-Content $path
  $r = Run $source $path $out
  $dump = $r.Json | ConvertTo-Json -Depth 12
  switch ($expect) {
    'saves' { Require ($r.Exit -eq 0 -and $r.Json.saved -and $r.Json.reopenNewErrors.Count -eq 0) "$label failed: $dump" }
    'asks' { Require ($r.Exit -eq 1 -and -not $r.Json.saved -and -not (Test-Path $out) -and @($r.Json.needsApproval).Count -gt 0 -and $r.Json.next -match '--user-approved' -and -not ($r.Text -match 'refused')) "$label did not stop with needsApproval: $dump" }
    default { Require ($r.Exit -eq 1 -and -not $r.Json.saved -and -not (Test-Path $out) -and ($r.Json.problems -join ' ') -match 'refused:.*with any flag or setting \(rule 2\)') "$label refusal failed: $dump" }
  }
  if ($Says) { Require ($r.Text.Contains($Says)) "$label does not say '$Says': $dump" }
  if ($Approve) {
    $again = Join-Path $Tmp "rules-case-$($script:index - 1)-approved.oml"
    $a = Run $source $path $again -Approved
    $dump = $a.Json | ConvertTo-Json -Depth 12
    if ($expect -eq 'asks') { Require ($a.Exit -eq 0 -and $a.Json.saved -and @($a.Json.approved | Where-Object by -eq '--user-approved').Count -gt 0) "$label with --user-approved did not save: $dump"; return $again }
    Require ($a.Exit -eq 1 -and -not $a.Json.saved -and -not (Test-Path $again) -and ($a.Json.problems -join ' ') -match '\(rule 2\)') "$label with --user-approved was not refused: $dump"
  }
  return $out
}
$source = Join-Path $Tmp 'rules-source.oml'
$r = & $Oml apply (Join-Path $Fixtures 'r0.oml') (Join-Path $PSScriptRoot 'rules.json') $source | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $r.saved) "rules source: $($r | ConvertTo-Json -Depth 12)"
$before = (Get-FileHash $source).Hash
# Rule 3: any existing element that is not an entity or attribute: delete asks, rename saves.
foreach ($target in @('RuleDto','RuleDto/Caption','RuleAction','RuleAction/Input','RuleAction/Output','RuleClient','RuleClient/Input','RuleClient/Output',
  'RuleScreen','RuleBlock','RuleError','RuleRole','RuleProperty','RuleTimer','RuleApi','RuleApi/GetValue',
  'RuleService','RuleService/GetValue','RuleProcess')) {
  Case $source @{edits=@(@{op='delete';element=$target})} 'asks' "delete $target" -Approve:($target -in 'RuleClient','RuleScreen','RuleRole','RuleTimer') | Out-Null
  Case $source @{edits=@(@{op='rename';element=$target;to='Renamed'})} 'saves' "rename $target" | Out-Null
}
Case $source @{edits=@(@{op='remove';element='RuleBlock'})} 'asks' 'remove RuleBlock' -Approve | Out-Null
# Rule 2: an existing entity or attribute is never deleted; an attribute is never renamed or retyped. Entities may be renamed.
foreach ($target in @('RuleEntity','RuleEntity/Value','RuleStatus','RuleStatus/Color')) {
  Case $source @{edits=@(@{op='delete';element=$target;allowDataLoss=$true})} 'refused' "delete $target" -Approve | Out-Null
}
foreach ($target in @('RuleEntity/Value','RuleStatus/Color')) {
  Case $source @{edits=@(@{op='rename';element=$target;to='Renamed'})} 'refused' "rename $target" | Out-Null
  Case $source @{edits=@(@{op='set';element=$target;properties=@{nAmE='Renamed'}})} 'refused' "set Name $target" | Out-Null
  Case $source @{edits=@(@{op='set';element=$target;properties=@{DataType='Integer'}})} 'refused' "retype $target" -Approve | Out-Null
}
Case $source @{edits=@(@{op='remove';element='RuleEntity'})} 'refused' 'remove RuleEntity' | Out-Null
Case $source @{entities=@(@{name='RuleEntity';attributes=@(@{name='Value';type='Integer'})})} 'refused' 'retype through entities' | Out-Null
Case $source @{copy=@(@{from=$source;elements=@('RuleEntity')})} 'refused' 'copy an entity from another module' -Says 'makes a new, empty table' | Out-Null
foreach ($target in @('RuleEntity','RuleStatus')) {
  Case $source @{edits=@(@{op='rename';element=$target;to="$($target)Renamed"})} 'saves' "rename entity $target" | Out-Null
}
# Rule 5: a shorter length, fewer decimals or a new mandatory setting asks; a longer length, optional and settings save. Structures are free.
Case $source @{edits=@(@{op='set';element='RuleEntity/Value';properties=@{Length=12}})} 'asks' 'shorter length' -Approve -Says 'shorter length 50 -> 12' | Out-Null
Case $source @{edits=@(@{op='set';element='RuleEntity/Value';properties=@{IsMandatory=$true}})} 'asks' 'new mandatory' | Out-Null
Case $source @{entities=@(@{name='RuleEntity';attributes=@(@{name='Value';length=200;description='longer'})})} 'saves' 'longer length through entities' | Out-Null
Case $source @{entities=@(@{name='RuleEntity';updateBehavior='allAttributes';exposeReadOnly=$true})} 'saves' 'entity settings' | Out-Null
foreach ($pair in @(@('DataType','Integer'),@('Length',12),@('IsMandatory',$true))) {
  Case $source @{edits=@(@{op='set';element='RuleDto/Caption';properties=@{$pair[0]=$pair[1]}})} 'saves' "structure attribute $($pair[0])" | Out-Null
}
# Rule 6: data-changing SQL asks, new, changed or inserted; read-only SQL and INSERT save.
foreach ($word in @('DELETE','uPdAtE','TRUNCATE','drop','ALTER','MERGE')) {
  $sql = "SELECT 'ok'; $word {RuleEntity}"
  Case $source @{serverActions=@(@{name='Unsafe';flow=@(@{kind='sql';name='ChangeRows';sql=$sql;outputs=@('RuleDto')})})} 'asks' "new SQL $word" -Approve:($word -eq 'DELETE') | Out-Null
  Case $source @{edits=@(@{op='set';element='RuleAction/Read';properties=@{sTaTeMeNt=$sql}})} 'asks' "existing SQL $word" | Out-Null
}
Case $source @{edits=@(@{op='insertNodes';action='RuleAction';after='Start';flow=@(@{kind='sql';name='ChangeRows';sql="DELETE FROM {RuleEntity}";outputs=@('RuleDto')})})} 'asks' 'insert SQL DELETE' | Out-Null
Case $source @{edits=@(@{op='set';element='RuleAction/Read';properties=@{Statement="SELECT 'DELETE '' UPDATE', 'MERGE' /* DROP /* ALTER */ TRUNCATE */ -- DELETE`n"}})} 'saves' 'R6 SELECT with comments and strings' | Out-Null
Case $source @{serverActions=@(@{name='InsertRows';flow=@(@{kind='sql';name='AddRow';sql="INSERT INTO {RuleEntity} ([Value]) VALUES ('UPDATE')";outputs=@('RuleDto')})})} 'saves' 'R6 INSERT' | Out-Null
# Rule 4: a public signature change saves and is listed as breaking, with what consumers do.
Case $source @{edits=@(@{op='addVariables';action='RulePublic';inputs=@(@{name='Region';mandatory=$true})})} 'saves' 'new mandatory input on a public action' -Says "new mandatory input 'Region'. Callers in other modules must pass it." | Out-Null
Case $source @{serverActions=@(@{name='RulePublic';inputs=@(@{name='Id';type='Text'})})} 'saves' 'retyped input on a public action' -Says "input 'Id' changed type (Integer to Text)" | Out-Null
Case $source @{edits=@(@{op='set';element='RulePublic';properties=@{Public=$false}})} 'saves' 'public to private' -Says 'was public and is now private' | Out-Null
Case $source @{edits=@(@{op='rename';element='RulePublic';to='RuleShared'})} 'saves' 'rename of a public action' -Says "was renamed to 'RuleShared'" | Out-Null
Case $source @{edits=@(@{op='delete';element='RulePublic/Id'})} 'asks' 'delete of a public input' -Says "input 'Id' was removed. Callers must stop passing it." | Out-Null
# Same-spec elements, flow nodes and locals: free.
Case $source @{entities=@(@{name='Disposable';attributes=@(@{name='Value'})});edits=@(@{op='rename';element='Disposable';to='Temporary'},@{op='delete';element='Temporary'})} 'saves' 'R2 same-spec entity' | Out-Null
Case $source @{edits=@(@{op='rename';element='RuleAction/Copy';to='CopyAgain'},@{op='delete';element='RuleAction/CopyAgain'},@{op='rename';element='RuleAction/Scratch';to='Temporary'},@{op='delete';element='RuleAction/Temporary'})} 'saves' 'R3 flow and locals' | Out-Null
$added = Case $source @{entities=@(@{name='RuleEntity';attributes=@(@{name='Added'})});edits=@(@{op='set';element='RuleEntity/Added';properties=@{Length=120}})} 'saves' 'R5 new attribute' -Always
Case $added @{edits=@(@{op='delete';element='RuleEntity/Added'})} 'refused' 'new attribute becomes existing next apply' | Out-Null
# Service actions require a Service module.
$service = Case (Join-Path $Fixtures 's0.oml') @{serviceActions=@(@{name='RuleServiceAction';inputs=@(@{name='Input'});outputs=@(@{name='Output'})})} 'saves' 'service seed' -Always
foreach ($target in @('RuleServiceAction','RuleServiceAction/Input','RuleServiceAction/Output')) {
  Case $service @{edits=@(@{op='rename';element=$target;to='Renamed'})} 'saves' "service rename $target" | Out-Null
  Case $service @{edits=@(@{op='delete';element=$target})} 'asks' "service delete $target" | Out-Null
}
# The project's autoApprove.deletes and autoApprove.dataDeletion (.oml/oml-config.json where oml runs): saved without the flag.
if ($Part -eq 0) {
  $project = Join-Path $Tmp 'project'
  New-Item -ItemType Directory -Force (Join-Path $project '.oml') | Out-Null
  $spec = Join-Path $Tmp 'rules-auto.json'
  @{edits=@(@{op='delete';element='RuleError'})} | ConvertTo-Json -Depth 10 | Set-Content $spec
  Push-Location $project
  try {
    Set-Content -Encoding utf8 (Join-Path $project '.oml\oml-config.json') '{ "autoApprove": { "deletes": true } }'
    $a = Run $source $spec (Join-Path $Tmp 'rules-auto-delete.oml')
    Require ($a.Exit -eq 0 -and $a.Json.saved -and $a.Json.approved[0].by -eq 'autoApprove.deletes') "autoApprove.deletes: $($a.Text)"
    @{edits=@(@{op='set';element='RuleEntity/Value';properties=@{Length=12}})} | ConvertTo-Json -Depth 10 | Set-Content $spec
    $a = Run $source $spec (Join-Path $Tmp 'rules-auto-data-off.oml')
    Require ($a.Exit -eq 1 -and @($a.Json.needsApproval).Count -eq 1 -and $a.Json.needsApproval[0].kind -eq 'data') "autoApprove.deletes must not cover a data change: $($a.Text)"
    Set-Content -Encoding utf8 (Join-Path $project '.oml\oml-config.json') '{ "autoApprove": { "dataDeletion": true } }'
    $a = Run $source $spec (Join-Path $Tmp 'rules-auto-data.oml')
    Require ($a.Exit -eq 0 -and $a.Json.saved -and $a.Json.approved[0].by -eq 'autoApprove.dataDeletion') "autoApprove.dataDeletion: $($a.Text)"
    @{edits=@(@{op='delete';element='RuleEntity'})} | ConvertTo-Json -Depth 10 | Set-Content $spec
    Set-Content -Encoding utf8 (Join-Path $project '.oml\oml-config.json') '{ "autoApprove": { "deletes": true, "dataDeletion": true } }'
    $a = Run $source $spec (Join-Path $Tmp 'rules-auto-entity.oml')
    Require ($a.Exit -eq 1 -and -not $a.Json.saved -and ($a.Json.problems -join ' ') -match '\(rule 2\)') "autoApprove must never delete an entity: $($a.Text)"
  } finally { Pop-Location }
  Write-Host 'ok   autoApprove.deletes and autoApprove.dataDeletion save without the flag; an entity delete stays refused'
}
Require ((Get-FileHash $source).Hash -eq $before) 'rules changed source'
Write-Host "ok   rules: rule 2 refused, rules 3/5/6 ask and save with --user-approved, renames and public signature changes save (part $Part of $Parts)"
} finally { Remove-TestWorkFolder $Tmp }
