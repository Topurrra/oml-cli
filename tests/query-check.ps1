# Checks `oml query`: refused @filter conditions and a missing oml-tool (always), then real ModelQL queries through
# Service Studio's oml-tool.exe on tests/datamodel.json: fields, a filter, --limit, --fields, a bad field's hint, a step
# of `oml batch`, and that the module file is not changed. Without oml-tool the real queries are skipped with a message.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Tmp = New-TestFolder $Tmp 'query'
try {
function Require($condition, $message) { if (!$condition) { throw $message } }
function Query([string[]]$cliArgs) {
    $text = & $Oml query @cliArgs
    return @{ Code = $LASTEXITCODE; Json = (($text -join "`n") | ConvertFrom-Json) }
}

$blank = Join-Path $Tmp 'query-blank.oml'
$module = Join-Path $Tmp 'query-data.oml'
& $Oml new $blank --name QueryData | Out-Null
Require ($LASTEXITCODE -eq 0) 'oml new failed'
$r = (& $Oml apply $blank (Join-Path $PSScriptRoot 'datamodel.json') $module) -join "`n" | ConvertFrom-Json
Require ($r.saved) 'datamodel.json did not save'

# 1. Refused before anything runs: C# outside the safe subset in a @filter condition.
$q = Query @($module, 'Root { Entities @filter(condition: "System.IO.File.Exists(\"x\")") { Name } }')
Require ($q.Code -eq 2 -and $q.Json.error -match 'refused') "unsafe condition: exit $($q.Code) $($q.Json | ConvertTo-Json -Compress)"

# 2. oml-tool not found: a plain message with what to set.
$saved = $env:OML_TOOL
try {
    $env:OML_TOOL = Join-Path $Tmp 'no-oml-tool.exe'
    $q = Query @($module, 'Root { Name }')
    Require ($q.Code -eq 2 -and $q.Json.error -match 'oml-tool.exe was not found' -and $q.Json.error -match 'OML_TOOL') "missing tool: exit $($q.Code) $($q.Json | ConvertTo-Json -Compress)"
} finally { $env:OML_TOOL = $saved }

$studio = if ($env:OML_SERVICE_STUDIO) { $env:OML_SERVICE_STUDIO } else { 'C:\Program Files\OutSystems\Service Studio 11\Service Studio' }
$tool = if ($env:OML_TOOL) { $env:OML_TOOL } else { Join-Path $studio 'runtimes\win-x64\oml-tool.exe' }
if (-not (Test-Path -LiteralPath $tool)) {
    Write-Host "ok   query: refusals checked; real queries skipped: no oml-tool at $tool"
    return
}

$before = (Get-FileHash $module).Hash
# 3. Fields and nested lists.
$q = Query @($module, 'Root { Name Entities { Name Attributes { Name } } }')
Require ($q.Code -eq 0 -and $q.Json.data.Root.Name -eq 'QueryData') "root: $($q.Json | ConvertTo-Json -Compress -Depth 8)"
$entities = @($q.Json.data.Root.Entities | ForEach-Object Name)
foreach ($e in 'Customer', 'Order', 'OrderLine') { Require ($entities -contains $e) "entities: $($entities -join ', ')" }
Require ((@($q.Json.data.Root.Entities | Where-Object Name -eq 'Customer')[0].Attributes | ForEach-Object Name) -contains 'Email') 'Customer.Email missing'
# 4. A safe filter and an argument.
$q = Query @($module, 'Root { Entities @filter(condition: "_.Name.StartsWith(\"Order\")") { Name } }')
Require ($q.Code -eq 0 -and (@($q.Json.data.Root.Entities | ForEach-Object Name) -join ',') -in 'Order,OrderLine', 'OrderLine,Order') "filter: $($q.Json | ConvertTo-Json -Compress -Depth 8)"
$q = Query @($module, 'Root { Entities(name: "Customer") { Name } }')
Require ($q.Code -eq 0 -and @($q.Json.data.Root.Entities).Count -eq 1) "name argument: $($q.Json | ConvertTo-Json -Compress -Depth 8)"
# 5. --limit cuts every list and says where.
$q = Query @($module, 'Root { Entities { Name } }', '--limit', '1')
Require ($q.Code -eq 0 -and @($q.Json.data.Root.Entities).Count -eq 1 -and $q.Json.cut[0].path -eq 'Root.Entities' -and $q.Json.cut[0].total -ge 3 -and $q.Json.next) "limit: $($q.Json | ConvertTo-Json -Compress -Depth 8)"
# 6. A field the type lacks: the tool's message and the --fields hint; --fields lists the type.
$q = Query @($module, 'Root { Screens { Name } }')
Require ($q.Code -eq 2 -and $q.Json.error -match "fetch field 'Screens'" -and $q.Json.next -match '--fields ESpace') "bad field: $($q.Json | ConvertTo-Json -Compress)"
$q = Query @($module, '--fields', 'ESpace')
Require ($q.Code -eq 0 -and $q.Json.kind -eq 'OBJECT' -and @($q.Json.fields) -contains 'Entities: [IEntity] (name, globalKey, limit)') "fields: $($q.Json | ConvertTo-Json -Compress)"
$q = Query @($module, '--fields', 'NoSuchType')
Require ($q.Code -eq 2 -and $q.Json.error -match 'no type') "unknown type: $($q.Json | ConvertTo-Json -Compress)"
# 7. A step of oml batch; --live is refused there.
$steps = Join-Path $Tmp 'steps.json'
Set-Content $steps (@{ steps = @(@('query', $module, 'Root { Name }'), @('query', 'QueryData', 'Root { Name }', '--live')) } | ConvertTo-Json -Depth 4) -Encoding utf8
$b = (& $Oml batch $steps --continue) -join "`n" | ConvertFrom-Json
Require ($b.results[0].exitCode -eq 0 -and $b.results[0].output.data.Root.Name -eq 'QueryData') "batch step: $($b.results[0] | ConvertTo-Json -Compress -Depth 8)"
Require ($b.results[1].exitCode -eq 2 -and $b.results[1].output.error -match 'not allowed') "batch --live: $($b.results[1] | ConvertTo-Json -Compress -Depth 8)"
Require ((Get-FileHash $module).Hash -eq $before) 'the module file changed'
Write-Host 'ok   query: refusals, missing tool, fields, filter, --limit, bad field hint, --fields, batch step, file unchanged'
} finally { Remove-TestWorkFolder $Tmp }
