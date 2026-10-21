# Every fixture and recipe, applied twice: the second apply, on the first one's output, must save with no problem.
# Creation lists reuse what exists, so a spec applied again changes nothing. Edits are ordered
# operations, not desired state: in the specs that hold them, only their problems are accepted (listed below).
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $Tmp ('reapply-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work, (Join-Path $work 'tmp/recipes') | Out-Null
$env:TEMP = Join-Path $work 'tmp'; $env:TMP = $env:TEMP
# Spec -> problems accepted on the second apply, with the reason.
$accepted = @{
  'edits'               = '^edit\['                # edits only: ordered operations
  'edit-existing-flow'  = '^edit\['
  'check-bad'           = '^edit\['
  # Its own edits insert two nodes into SummarizeCustomers, so the flow it lists differs from the module's.
  'sql'                 = '^edit\[|^serverAction SummarizeCustomers exists, .*flow \(2 nodes; module: 4\)'
}
$failures = @()
function Ap($name, $source, $spec) {
  $out = Join-Path $work "$name.oml"
  $r = & $Oml apply $source $spec $out 2>$null | ConvertFrom-Json
  [pscustomobject]@{ Exit = $LASTEXITCODE; Report = $r; Out = $out }
}
function Twice($name, $source, $spec) {
  $first = Ap $name $source $spec
  if (-not $first.Report.saved) { $script:failures += "$name`: first apply did not save: $(@($first.Report.problems) -join ' | ')"; return $null }
  $again = Ap "$name.again" $first.Out $spec
  $problems = @(@($again.Report.problems) + @($again.Report.newErrors | Where-Object { $_ } | ForEach-Object { "newError $($_.id)" }) | Where-Object { $_ })
  $pattern = $accepted[$name]
  $bad = @($problems | Where-Object { -not $pattern -or $_ -notmatch $pattern })
  if ($bad.Count -or (-not $pattern -and ($again.Exit -ne 0 -or -not $again.Report.saved))) {
    $script:failures += "$name`: second apply exit $($again.Exit): $(@($bad) -join ' | ')"
  }
  return $first.Out
}
function New-Base($name, $kind) { $p = Join-Path $work "$name.oml"; & $Oml new $p --name $name --kind $kind | Out-Null; $p }

$t = Join-Path $repo 'tests'
$r0 = New-Base Reg CrossDevice; $s0 = New-Base RegSvc Service; $w0 = New-Base RegWeb WebResponsive
$producer = (Ap 'references-producer' (New-Base ReferenceProducer CrossDevice) (Join-Path $t 'references-producer.json')).Out
Copy-Item $producer (Join-Path $work 'tmp/references-producer.oml')
Push-Location $work
try {
  foreach ($f in 'block','check-bad','check-clean','data','datamodel','delete','logic','platform','preview','queries','references','rest','rules','scaffold','sql','static') {
    $null = Twice $f $r0 (Join-Path $t "$f.json")
  }
  $null = Twice 'service' $s0 (Join-Path $t 'service.json')
  foreach ($f in 'web','logic-web-extended','check-web-bad','check-web-clean') { $null = Twice $f $w0 (Join-Path $t "$f.json") }
  $null = Twice 'edits' (Join-Path $work 'logic.oml') (Join-Path $t 'edits.json')

  # Recipes, in the chain recipes-check.ps1 uses.
  $rc = Join-Path $repo 'skill/recipes'
  $base = New-Base RecipeConsumer CrossDevice
  $null = Twice 'entity-with-crud' $base "$rc/entity-with-crud.jsonc"
  $uiSpec = Join-Path $work 'ui-base.json'
  Set-Content -Path $uiSpec -Encoding utf8 -Value @'
{ "blocks": [ { "name": "LayoutTopMenu", "flow": "Common", "widgets": [
    { "type": "Placeholder", "name": "Title" }, { "type": "Placeholder", "name": "Actions" },
    { "type": "Placeholder", "name": "MainContent" } ] } ],
  "onException": { "name": "OnException", "flow": "MainFlow", "nodes": [
    { "kind": "exceptionHandler", "name": "OnAllErrors", "exception": "AllExceptions", "logError": true },
    { "kind": "message", "message": "\"Something went wrong\"", "type": "error" } ] },
  "globalExceptionHandler": "MainFlow/OnException" }
'@
  $uiBase = Twice 'ui-base' (New-Base RecipeUi CrossDevice) $uiSpec
  $crud = Twice 'ui-crud' $uiBase "$rc/entity-with-crud.jsonc"
  $screens = Twice 'list-detail-screens' $crud "$rc/list-detail-screens.jsonc"
  $null = Twice 'edit-existing-flow' $screens "$rc/edit-existing-flow.jsonc"
  foreach ($n in 'timer-role-site-property','form-with-validation','dropdown-from-static','list-search-paging','aggregate-on-demand-detail','loading-empty-error-states','block-popup-event','role-protected-screen') { $null = Twice $n $uiBase "$rc/$n.jsonc" }
  foreach ($n in 'aggregate-join-groupby','rest-client','sql-query','expose-rest-api','send-email') { $null = Twice $n $base "$rc/$n.jsonc" }
  $pspec = Join-Path $work 'producer.json'
  '{"entities":[{"name":"Product","public":true,"attributes":[{"name":"Name"}]}],"serverActions":[{"name":"Product_GetById","public":true,"inputs":[{"name":"Id","type":"Product Identifier"}],"outputs":[{"name":"Product","type":"Product"}],"flow":[{"kind":"aggregate","name":"Match","sources":["Product"],"filters":["Product.Id = Id"],"maxRecords":1},{"kind":"assign","assign":{"Product":"Match.List.Current.Product"}}]}]}' | Set-Content $pspec
  Copy-Item (Ap 'producer' (New-Base RecipeProducer CrossDevice) $pspec).Out (Join-Path $work 'tmp/recipes/producer.oml')
  $null = Twice 'references-from-producer' $base "$rc/references-from-producer.jsonc"
  $null = Twice 'web-screen' (New-Base RecipeWeb WebResponsive) "$rc/web-screen.jsonc"
}
finally { Pop-Location }
if ($failures.Count) { throw "re-apply: $($failures -join "`n")" }
Write-Host 'ok   re-apply: every fixture and recipe applied twice saves; only edits problems are accepted'
