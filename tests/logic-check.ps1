param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$Tmp = Join-Path $Tmp ('logic-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $Tmp | Out-Null
$env:TEMP = $Tmp; $env:TMP = $Tmp
function RunJson([string[]]$CliArgs) {
  $result = & $Oml @CliArgs | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) { throw ($result | ConvertTo-Json -Depth 12) }
  return $result
}
function Walk($node) { $node; foreach ($child in $node.children) { Walk $child } }
function Tree([string]$file, [string]$name) { @(Walk (RunJson @('tree',$file,$name))) }
function Require($condition, [string]$message) { if (-not $condition) { throw $message } }
function Reject([string]$source, [string]$json, [string]$pattern) {
  $id = [guid]::NewGuid().ToString('N')
  $spec = Join-Path $Tmp "reject-logic-$id.json"; $output = Join-Path $Tmp "reject-logic-$id.oml"
  Set-Content $spec $json
  $result = & $Oml apply $source $spec $output | ConvertFrom-Json
  Require ($LASTEXITCODE -eq 1 -and -not $result.saved -and -not (Test-Path $output)) 'invalid logic spec saved'
  Require (($result | ConvertTo-Json -Depth 15) -match $pattern) "missing refusal: $pattern"
}
$source = Join-Path $Tmp 'logic-check-source.oml'; $built = Join-Path $Tmp 'logic-check-built.oml'
$web = Join-Path $Tmp 'logic-check-web.oml'; $webBuilt = Join-Path $Tmp 'logic-check-web-built.oml'
RunJson @('new',$source,'--name','LogicChecks','--kind','CrossDevice') | Out-Null
$r = RunJson @('apply',$source,(Join-Path $PSScriptRoot 'logic-extended.json'),$built)
Require ($r.saved -and $r.reopenNewErrors.Count -eq 0) 'Reactive logic fixture did not save cleanly'
$nodes = Tree $built 'ConvertRows'
foreach ($kind in @('ExcelToRecordList','RecordListToExcel','Comment')) {
  Require (@($nodes | Where-Object kind -eq $kind).Count -eq 1) "missing $kind"
}
Require (@($nodes | Where-Object { $_.source -eq 'OnDatabase' -and $_.target -eq 'Recover' }).Count -eq 1) 'handler continuation missing'
Require (@($nodes | Where-Object { $_.target -in @('OnDatabase','OnUserError') }).Count -eq 0) 'handler has incoming connector'
Require (@($nodes | Where-Object { $_.source -eq 'Start' -and $_.target -eq 'ImportRows' }).Count -eq 1) 'comment or handler captured Start'
Require (@($nodes | Where-Object { $_.source -eq 'ExportRows' -and $_.target -eq 'End' }).Count -eq 1) 'main flow crosses handler path'
Require (@($nodes | Where-Object { $_.name -eq 'OnUserError' -and $_.ref -eq 'LogicFailure' }).Count -eq 1) 'edit handler lost exception'
Require (@($nodes | Where-Object text -eq 'Import and export rows').Count -eq 1) 'comment text missing'
$client = Tree $built 'ShowStatus'
Require (@($client | Where-Object kind -eq 'FeedbackMessage').Count -eq 1) 'message missing'
Require (@($client | Where-Object text -eq 'Client.Caption').Count -ge 1) 'client variable expression missing'
Require (@((Tree $built 'Caption') | Where-Object text -eq '"Ready"').Count -eq 1) 'client default missing'
foreach ($event in @('OnApplicationReady','OnApplicationResume')) {
  Require (@((Tree $built $event) | Where-Object { $_.ref -eq 'ShowStatus' -and $_.kind -eq 'ExecuteClientAction' }).Count -eq 1) "$event action call missing"
}
Require ((RunJson @('tree',$built,'MainFlow')).ref -eq 'HandleError') 'flow exception handler missing'
Require (@((Tree $built 'MainFlow/LogicHome/ScreenWork') | Where-Object name -eq 'ScreenError').Count -eq 1) 'screen action handler missing'
Require (@((Tree $built 'MainFlow/LogicHome/ReadValue') | Where-Object { $_.source -eq 'DataError' -and $_.target -eq 'Fallback' }).Count -eq 1) 'data action handler continuation missing'
Require (@((Tree $built 'MainFlow/HandleError') | Where-Object { $_.ref -eq 'ShowStatus' -and $_.kind -eq 'ExecuteClientAction' }).Count -eq 1) 'global handler action call missing'
RunJson @('new',$web,'--name','LogicWeb','--kind','WebResponsive') | Out-Null
$r = RunJson @('apply',$web,(Join-Path $PSScriptRoot 'logic-web-extended.json'),$webBuilt)
Require ($r.saved -and $r.reopenNewErrors.Count -eq 0) 'Web logic fixture did not save cleanly'
foreach ($event in @('OnSessionStart','OnBeginWebRequest')) {
  Require (@((Tree $webBuilt $event) | Where-Object { $_.ref -eq 'BeginRequest' -and $_.kind -eq 'ExecuteAction' }).Count -eq 1) "$event action call missing"
}
Require (@((Tree $webBuilt 'BeginRequest') | Where-Object text -eq 'Session.RequestCount + 1').Count -eq 1) 'session expression missing'
Require (@((Tree $webBuilt 'RequestCount') | Where-Object text -eq '0').Count -eq 1) 'session default missing'
$download = Tree $webBuilt 'MainFlow/ExportFile/GetFile'
Require (@($download | Where-Object kind -eq 'Download').Count -eq 1) 'download missing'
Require (@($download | Where-Object text -eq '"export.bin"').Count -eq 1) 'download file name missing'
Reject $source '{"clientVariables":[{"name":"Secret","isSecure":true}]}' 'IsSecure'
Reject $source '{"sessionVariables":[{"name":"Unsupported"}]}' 'Session Variable objects'
Reject $source '{"serverActions":[{"name":"Init"}],"systemEvents":{"onSessionStart":"Init"}}' 'On Session Start objects'
Reject $source '{"serverActions":[{"name":"Bad","flow":[{"kind":"exceptionHandler","name":"A","exception":"AllExceptions"},{"kind":"exceptionHandler","name":"B","exception":"AllExceptions"}]}]}' 'one handler per exception'
Reject $web '{"serverActions":[{"name":"Bad","flow":[{"kind":"sendEmail"}]}]}' 'requires email'
Reject $source '{"clientActions":[{"name":"Bad","flow":[{"kind":"assign","name":"First","next":"Handler"},{"kind":"exceptionHandler","name":"Handler","exception":"AllExceptions"}]}]}' 'cannot connect'
Reject $source '{"clientActions":[{"name":"Init"}],"systemEvents":{"onMadeUp":"Init"}}' 'unknown system event'
Write-Host 'ok   exception paths, variables, events, Excel, messages, comments, download, refusals'
