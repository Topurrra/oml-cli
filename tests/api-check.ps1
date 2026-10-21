# oml api: no name prints the usage and useful types (each one matches a type on this build); a name that matches
# nothing exits 2 with the closest type names; a result is never silently empty.
param([string]$Oml)
$ErrorActionPreference = 'Stop'
function Require($condition, $message) { if (-not $condition) { throw "api: $message" } }

$usage = (& $Oml api) -join "`n"
Require ($LASTEXITCODE -eq 0 -and $usage.Contains('oml api <TypeName>...') -and $usage.Contains('Useful types:')) "no name: exit $LASTEXITCODE, $usage"
$names = [regex]::Matches(($usage -split 'Useful types:')[1], '\b(?:OutSystems|ServiceStudio)\.[A-Za-z0-9_.*]+') | ForEach-Object Value
Require ($names.Count -ge 15) "only $($names.Count) useful type names: $usage"
$found = & $Oml api @names | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0) "useful types: exit $LASTEXITCODE"
foreach ($name in $names) {
  $pattern = '^' + [regex]::Escape($name).Replace('\*', '.*') + '$'
  Require (@($found | Where-Object { $_.type -match $pattern }).Count -gt 0) "useful type '$name' matches nothing on this build"
}

$err = (& $Oml api IServerActon 2>&1) -join "`n"
Require ($LASTEXITCODE -eq 2 -and $err.Contains("No type matches 'IServerActon'") -and $err.Contains('Closest: OutSystems.Model.Logic.IServerAction')) "misspelled name: exit $LASTEXITCODE, $err"
$err = (& $Oml api OutSystems.Model.Logic.IServerAction NoSuchTypeAtAll 2>&1) -join "`n"
Require ($LASTEXITCODE -eq 2 -and $err.Contains("No type matches 'NoSuchTypeAtAll'") -and -not $err.Contains("matches 'OutSystems.Model.Logic.IServerAction'")) "one unknown name of two: exit $LASTEXITCODE, $err"
$one = & $Oml api iserveraction | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and @($one.type) -contains 'OutSystems.Model.Logic.IServerAction') "a name in another case: $($one | ConvertTo-Json -Compress)"
# A name without its 'I' is tried with it; a name far from every type says so instead of listing noise.
$retry = & $Oml api ServerAction | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and (@($retry.type) -join ',') -eq 'OutSystems.Model.Logic.IServerAction') "I prefix: $($retry | ConvertTo-Json -Compress)"
$err = (& $Oml api Qwxyzzz 2>&1) -join "`n"
Require ($LASTEXITCODE -eq 2 -and $err.Contains('No close names.') -and -not $err.Contains('Closest:')) "far name: exit $LASTEXITCODE, $err"
Write-Host "ok   api: usage with $($names.Count) useful types, unknown names exit 2 with the closest types or none, any case, I prefix"
