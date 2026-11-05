# oml map: build an application map of two modules (an integration module that calls a REST API inside public wrapper
# actions, and a core module that calls the wrappers, saves the result in an entity and launches a process on it), then
# check every query: overview, uses, impact, data, api with its chain through the wrappers, the project remembered by
# the folder, reuse of unchanged files, stale and removed files, and the errors. Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'map'
$saved = @{}; foreach ($n in 'OML_MAPS', 'OML_LOG') { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
try {
  function Require($condition, $message) { if (-not $condition) { throw "map: $message" } }
  function Json { param([Parameter(ValueFromRemainingArguments)]$a) $out = & $Oml @a; ($out -join "`n") | ConvertFrom-Json }
  $env:OML_LOG = '0'; $env:OML_MAPS = Join-Path $Tmp 'maps'
  $in = Join-Path $Tmp 'in'; New-Item -ItemType Directory $in | Out-Null
  $work = Join-Path $Tmp 'project'; New-Item -ItemType Directory $work | Out-Null

  # Fixtures: CrmIS (REST CrmApi, wrappers GetClientInfo and GetCredits) and CrmCore (references CrmIS).
  & $Oml new (Join-Path $Tmp 'is-blank.oml') --name CrmIS --kind CrossDevice | Out-Null
  $r = Json apply (Join-Path $Tmp 'is-blank.oml') (Join-Path $PSScriptRoot 'map-is.json') (Join-Path $in 'CrmIS.oml')
  Require $r.saved "fixture CrmIS: $($r | ConvertTo-Json -Compress -Depth 5)"
  $core = Join-Path $Tmp 'map-core.json'
  (Get-Content (Join-Path $PSScriptRoot 'map-core.json') -Raw).Replace('{{PRODUCER}}', (Join-Path $in 'CrmIS.oml').Replace('\', '\\')) | Set-Content $core
  & $Oml new (Join-Path $Tmp 'core-blank.oml') --name CrmCore --kind CrossDevice | Out-Null
  $r = Json apply (Join-Path $Tmp 'core-blank.oml') $core (Join-Path $in 'CrmCore.oml')
  Require $r.saved "fixture CrmCore: $($r | ConvertTo-Json -Compress -Depth 5)"

  # 1. Build: both files read; the folder remembers the project.
  Push-Location $work
  try {
    $b = Json map build crm --in $in
    Require ($LASTEXITCODE -eq 0 -and $b.read -eq 2 -and $b.reused -eq 0) "build: $($b | ConvertTo-Json -Compress)"
    Require ((Get-Content (Join-Path $work '.oml\map.json') -Raw | ConvertFrom-Json).project -eq 'crm') 'build: .oml/map.json'
    Write-Host 'ok   map build: two modules read, the folder remembers the project'

    # 2. Overview, uses, impact.
    $o = Json map overview
    Require ($o.totals.modules -eq 2 -and $o.consumedApis[0].api -eq 'CrmApi' -and $o.consumedApis[0].methods -eq 2) "overview: $($o | ConvertTo-Json -Compress -Depth 6)"
    $u = Json map uses GetClientInfo
    Require ($u.definedIn[0].module -eq 'CrmIS' -and $u.definedIn[0].kind -eq 'ServerAction' -and $u.calledBy[0].flow -eq 'CrmCore/SyncCustomer' -and $u.referencedBy -contains 'CrmCore') "uses: $($u | ConvertTo-Json -Compress -Depth 6)"
    $i = Json map impact CrmApi.GetCreditLines
    $flows = @($i.affected | ForEach-Object flow)
    Require ($flows -contains 'CrmIS/GetCredits' -and $flows -contains 'CrmCore/LoadCredits' -and $flows -contains 'CrmCore/CustomerCredits/Load') "impact: $($flows -join ', ')"
    # A Reactive screen action: the model throws NotImplementedException for its outputs; the module must still be read.
    $u = Json map uses SyncCustomer
    Require (@($u.calledBy | ForEach-Object flow) -match 'CustomerSync' ) "screen action call: $($u | ConvertTo-Json -Compress -Depth 6)"
    Write-Host 'ok   map overview, uses, impact (through the wrapper, the process activity and a screen action)'

    # 3. Data lineage: the API output saved in the entity, through the wrapper; the entity feeds the next API.
    $d = Json map data Customer
    Require ($d.writtenBy[0].flow -eq 'CrmCore/SyncCustomer' -and $d.readBy -contains 'CrmCore/LoadCredits' -and $d.launches[0].process -eq 'CustomerCredits') "data: $($d | ConvertTo-Json -Compress -Depth 6)"
    $from = $d.valuesFrom | Where-Object attribute -eq 'ExternalId'
    Require ($from.from -eq 'api:CrmApi.GetClient.out:Response.CustomerId' -and $from.via -like '*GetClientInfo*SyncCustomer*') "data valuesFrom: $($from | ConvertTo-Json -Compress)"
    Require (($d.valuesTo | Where-Object attribute -eq 'ExternalId').to -eq 'api:CrmApi.GetCreditLines.in:CustomerId') 'data valuesTo'
    Write-Host 'ok   map data: API output -> entity attribute -> next API input, through wrappers'

    # 4. The API chain: GetClient -> Customer.ExternalId (launches CustomerCredits) -> GetCreditLines.
    $a = Json map api CrmApi.GetClient
    Require ($a.verb -eq 'GET' -and $a.url -eq '/customers/{Id}' -and $a.inputs[0].in -eq 'path') "api: $($a | ConvertTo-Json -Compress -Depth 6)"
    $steps = @($a.chain | ForEach-Object { "$($_.from) -> $($_.to)" })
    Require ($steps -contains 'api:CrmApi.GetClient.out:Response.CustomerId -> entity:Customer.ExternalId' -and $steps -contains 'entity:Customer.ExternalId -> api:CrmApi.GetCreditLines.in:CustomerId') "api chain: $($steps -join ' | ')"
    Require (($a.chain | Where-Object to -eq 'entity:Customer.ExternalId').launches -contains 'CustomerCredits (on Create)') 'api chain: the process launched on Customer'
    Write-Host 'ok   map api: the chain from one API to the next, with the process it launches'
  } finally { Pop-Location }

  # 5. Outside the project folder: --project; an unknown project and a bad name are errors.
  $o = Json map overview --project crm
  Require ($o.project -eq 'crm') 'map --project'
  $x = Json map overview --project nope
  Require ($LASTEXITCODE -eq 1 -and $x.hint -like '*map build nope*') "unknown project: $($x | ConvertTo-Json -Compress)"
  $bad = Json map build Bad_Name --in $in
  Require ($LASTEXITCODE -eq 2 -and $bad.error -like '*lower-case*') 'bad project name'
  $l = Json map list
  Require (@($l.maps).Count -eq 1 -and $l.maps[0].project -eq 'crm') 'map list'
  Write-Host 'ok   map --project, list, unknown project and bad name'

  # 6. Chained mocks: GetClient's CustomerId for a persona is the value that selects GetCreditLines' answer for it.
  Push-Location $work
  try {
    $m = Json mock --all --out (Join-Path $Tmp 'mocks')
    Require ($LASTEXITCODE -eq 0 -and $m.methods -eq 2 -and $m.files -eq 10) "mock: $($m | ConvertTo-Json -Compress -Depth 5)"
    $mocks = Join-Path $Tmp 'mocks'
    $client = Get-Content (Join-Path $mocks 'CrmApi\GetClient\normal.json') -Raw | ConvertFrom-Json
    $list = Get-Content (Join-Path $mocks 'CrmApi\checklist.md') -Raw
    Require ($client.CustomerId -and $list -match [regex]::Escape("CustomerId=$($client.CustomerId) (query)")) "mock chain: GetClient CustomerId $($client.CustomerId) does not select GetCreditLines`n$list"
    $many = Get-Content (Join-Path $mocks 'CrmApi\GetClient\many.json') -Raw | ConvertFrom-Json
    Require ($many.CustomerId -ne $client.CustomerId) 'mock: two personas share a CustomerId'
    Require ((Get-Content (Join-Path $mocks 'CrmApi\GetClient\error.json') -Raw | ConvertFrom-Json).status -eq 500) 'mock: error persona'
    Require ($list -match 'Id=I-1001 \(path\)') 'mock: the first API of the chain gets a request value per persona'
    # Same input, same output.
    $again = Json mock --all --out (Join-Path $Tmp 'mocks2')
    Require ((Get-Content (Join-Path $Tmp 'mocks2\CrmApi\GetClient\normal.json') -Raw) -eq (Get-Content (Join-Path $mocks 'CrmApi\GetClient\normal.json') -Raw)) 'mock: not deterministic'
    # The personas file: the developer's own test values flow through the whole chain.
    $personas = Join-Path $Tmp 'personas.json'
    '[{"name":"anna","items":1,"values":{"Customer.ExternalId":"C-77","Customer.Name":"Anna Smith"}},{"name":"down","status":503}]' | Set-Content $personas
    $m = Json mock --all --personas $personas --out (Join-Path $Tmp 'mocks3')
    $anna = Get-Content (Join-Path $Tmp 'mocks3\CrmApi\GetClient\anna.json') -Raw | ConvertFrom-Json
    Require ($anna.CustomerId -eq 'C-77' -and $anna.FullName -eq 'Anna Smith') "mock personas: $($anna | ConvertTo-Json -Compress)"
    Require ((Get-Content (Join-Path $Tmp 'mocks3\CrmApi\checklist.md') -Raw) -match 'CustomerId=C-77 \(query\)') 'mock personas: the chain uses the given value'
    '[{"name":"leak","values":{"Customer.ExternalId":"ghp_1234567890abcdefghijklmnopqrstuvwxyzAB"}}]' | Set-Content $personas
    $bad = Json mock --all --personas $personas --out (Join-Path $Tmp 'mocks4')
    Require ($LASTEXITCODE -eq 2 -and ($bad.error -like '*secret*' -or $bad.error -like '*token*')) "mock: a secret in personas must be refused: $($bad | ConvertTo-Json -Compress)"
    # A sample response in the module gives the shape: its extra fields stay, lists follow the persona, linked fields get the chain's value.
    $source = Get-ChildItem (Join-Path $env:OML_MAPS 'crm\sources') -Filter *.json | Where-Object { (Get-Content $_.FullName -Raw) -match '"GetClient"' } | Select-Object -First 1
    $doc = Get-Content $source.FullName -Raw | ConvertFrom-Json -Depth 50
    $method = ($doc.facts[0].restConsumed[0].methods | Where-Object name -eq 'GetClient')
    $method.sampleResponse = '{"CustomerId":"X1","FullName":"Ann","Since":"2020-01-31","Tags":[{"T":"vip"}]}'
    $doc | ConvertTo-Json -Depth 50 -Compress | Set-Content $source.FullName
    Json mock CrmApi.GetClient --out (Join-Path $Tmp 'mocks5') | Out-Null
    $shaped = Get-Content (Join-Path $Tmp 'mocks5\CrmApi\GetClient\many.json') -Raw | ConvertFrom-Json
    Require ($shaped.CustomerId -eq $many.CustomerId -and @($shaped.Tags).Count -eq 5 -and $shaped.Since -match '^\d{4}-\d\d-\d\d$') "mock sample shape: $($shaped | ConvertTo-Json -Compress -Depth 5)"
  } finally { Pop-Location }
  Write-Host 'ok   oml mock: chained values across APIs, request values per persona, personas file, sample shape, deterministic'

  # 7. Rebuild reads only what changed; a changed file is stale until then; a removed file leaves the map.
  Push-Location $work
  try {
    $b = Json map build crm
    Require ($b.read -eq 0 -and $b.reused -eq 2) "rebuild unchanged: $($b | ConvertTo-Json -Compress)"
    (Get-Item (Join-Path $in 'CrmCore.oml')).LastWriteTimeUtc = (Get-Date).ToUniversalTime().AddMinutes(1)
    Require ((Json map overview).stale -like '*1 input file(s) changed*') 'stale after a change'
    Remove-Item (Join-Path $in 'CrmIS.oml')
    $b = Json map build crm
    Require ($b.removed -eq 1 -and $b.files -eq 1) "rebuild after removal: $($b | ConvertTo-Json -Compress)"
    Require ((Json map overview).totals.modules -eq 1) 'removed module left the map'
  } finally { Pop-Location }
  Write-Host 'ok   map build: reuses unchanged files, reports stale ones, drops removed ones'
} finally {
  foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
  Remove-TestWorkFolder $Tmp
}
