# Apply JSONC recipes with the current CLI. All paths stay under this worktree.
param(
  [string]$Oml = (Join-Path $PSScriptRoot '../src/bin/Release/net8.0/oml.exe')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$Oml = (Resolve-Path $Oml).Path
$scratch = Join-Path $repo 'tmp/recipes'
New-Item -ItemType Directory -Force $scratch | Out-Null
$env:TEMP = $scratch; $env:TMP = $scratch
# Each run gets new output paths; the reference recipe has one fixed producer path.
$run = Join-Path $scratch ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $run | Out-Null
function New-Base($name, $kind = 'CrossDevice') {
  $path = Join-Path $run "$name.oml"
  & $Oml new $path --name $name --kind $kind | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "new failed: $name" }
  return $path
}
function Apply-Spec($source, $spec, $name) {
  $out = Join-Path $run "$name.oml"
  $r = & $Oml apply $source $spec $out | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0 -or -not $r.saved -or $r.problems.Count -or $r.newErrors.Count -or $r.reopenNewErrors.Count -or $r.newWarnings.Count -or $r.warnings.Count) {
    throw "$name failed: $($r | ConvertTo-Json -Depth 12 -Compress)"
  }
  Write-Host "ok   $name saved=true newErrors=0 reopenNewErrors=0 newWarnings=0"
  return $out
}
function Recipe($name, $source) {
  Apply-Spec $source (Join-Path $repo "skill/recipes/$name.jsonc") $name
}
# Prove the logic is wired: describe one element and require every pattern in its text.
function Assert-Describe($module, $element, [string[]]$patterns) {
  $d = & $Oml describe $module $element | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0 -or -not $d.text) { throw "describe failed: $element" }
  foreach ($p in $patterns) {
    if ($d.text -notmatch [regex]::Escape($p)) { throw "describe $element lacks '$p'`n$($d.text)" }
  }
  Write-Host "ok   describe $element"
}
Push-Location $repo
try {
  $base = New-Base RecipeConsumer
  $null = Recipe 'entity-with-crud' $base
  # UI recipes need a layout block named LayoutTopMenu and a UI flow exception handler, as a real app has.
  # Build that base here so every clone runs all recipes (no exported module needed).
  $uiSpec = Join-Path $run 'ui-base.json'
  Set-Content -Path $uiSpec -Encoding utf8 -Value @'
{ "blocks": [ { "name": "LayoutTopMenu", "flow": "Common", "widgets": [
    { "type": "Placeholder", "name": "Title" }, { "type": "Placeholder", "name": "Actions" },
    { "type": "Placeholder", "name": "MainContent" } ] } ],
  "onException": { "name": "OnException", "flow": "MainFlow", "nodes": [
    { "kind": "exceptionHandler", "name": "OnAllErrors", "exception": "AllExceptions", "logError": true },
    { "kind": "message", "message": "\"Something went wrong\"", "type": "error" } ] },
  "globalExceptionHandler": "MainFlow/OnException" }
'@
  $uiBase = Apply-Spec (New-Base RecipeUi) $uiSpec 'ui-base'
  $crud = Apply-Spec $uiBase (Join-Path $repo 'skill/recipes/entity-with-crud.jsonc') 'ui-crud'
  $screens = Recipe 'list-detail-screens' $crud
  $null = Recipe 'edit-existing-flow' $screens
  $null = Recipe 'timer-role-site-property' $uiBase
  foreach ($name in @('aggregate-join-groupby','rest-client','sql-query')) {
    $null = Recipe $name $base
  }
  $form = Recipe 'form-with-validation' $uiBase
  Assert-Describe $form Contact_Save @('raise InvalidContact', 'call CreateOrUpdateContact')
  Assert-Describe $form ContactForm @('call RecipeUi.Contact_Save', 'on exception InvalidContact', 'set NameError')
  $dropdown = Recipe 'dropdown-from-static' $uiBase
  Assert-Describe $dropdown Task_Add @('call CreateTask')
  Assert-Describe $dropdown Tasks @('Dropdown PriorityInput variable=NewTask.PriorityId', 'call RecipeUi.Task_Add', 'refresh FetchTasks', 'TaskPriority.Label')
  $paging = Recipe 'list-search-paging' $uiBase
  Assert-Describe $paging Customers @('like "%" + SearchText', 'order by Customer.Name', 'max PageSize', 'set StartIndex = StartIndex + PageSize', 'refresh FetchCustomers')
  $api = Recipe 'expose-rest-api' $base
  Assert-Describe $api 'OrderApi/GetOrder' @('GET /orders/{Id}', 'call RecipeConsumer.Order_Get')
  Assert-Describe $api 'OrderApi/PlaceOrder' @('POST /orders', 'call RecipeConsumer.Order_Place')
  Assert-Describe $api Order_Place @('call CreateOrder', 'raise InvalidOrder')
  $roleScreen = Recipe 'role-protected-screen' $uiBase
  Assert-Describe $roleScreen AuditReport_Load @('call RecipeUi.CheckAuditorRole', 'raise NotAuthorized')
  Assert-Describe $roleScreen AuditReport @('roles Auditor')
  Assert-Describe $roleScreen Home @('If condition=IsAuditor', 'Link onClick=AuditReport')
  $producerBase = New-Base RecipeProducer
  $producerSpec = Join-Path $run 'producer.json'
  '{"entities":[{"name":"Product","public":true,"attributes":[{"name":"Name"}]}],"serverActions":[{"name":"Product_GetById","public":true,"inputs":[{"name":"Id","type":"Product Identifier"}],"outputs":[{"name":"Product","type":"Product"}],"flow":[{"kind":"aggregate","name":"Match","sources":["Product"],"filters":["Product.Id = Id"],"maxRecords":1},{"kind":"assign","assign":{"Product":"Match.List.Current.Product"}}]}]}' | Set-Content $producerSpec
  $producer = Apply-Spec $producerBase $producerSpec 'producer'
  Copy-Item -LiteralPath $producer -Destination (Join-Path $scratch 'producer.oml') -Force
  # Synthetic producer contract is approved by this test; real producers need user choice.
  $found = & $Oml find Product $base --in $producer | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0 -or -not ($found.matches | Where-Object { $_.qualified -eq 'RecipeProducer.Product' -and $_.isPublic })) { throw 'producer entity find failed' }
  $found = & $Oml find Product_GetById $base --in $producer | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0 -or -not ($found.matches | Where-Object { $_.qualified -eq 'RecipeProducer.Product_GetById' -and $_.isPublic })) { throw 'producer action find failed' }
  $null = Recipe 'references-from-producer' $base
  $web = New-Base RecipeWeb WebResponsive
  $null = Recipe 'web-screen' $web
}
finally { Pop-Location }
