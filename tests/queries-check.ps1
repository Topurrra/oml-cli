param([string]$oml, [string]$module, [string]$source)
$ErrorActionPreference = 'Stop'
function Walk($node) {
  $node
  foreach ($child in $node.children) { Walk $child }
}
$screen = @(Walk (& $oml tree $module Orders | ConvertFrom-Json))
$totals = @(Walk (& $oml tree $module QueryTotals | ConvertFrom-Json))
function Require($condition, $message) { if (-not $condition) { throw $message } }
foreach ($mode in @('Left','Full')) {
  Require (@($screen | Where-Object { $_.joinType -eq $mode -and $_.left -eq 'Order' -and $_.right -eq 'Customer' }).Count -eq 1) "$mode join endpoints did not round-trip"
}
Require (@($totals | Where-Object { $_.joinType -eq 'Inner' }).Count -eq 1) 'Inner join missing'
foreach ($fn in @('Sum','Avg','Min','Max','Count')) {
  Require (@($totals | Where-Object { $_.function -eq $fn }).Count -eq 1) "$fn did not round-trip"
}
Require (@($screen | Where-Object { $_.text -eq 'Order.CustomerId = Customer.Id' }).Count -ge 2) 'Join conditions missing'
Require (@($screen | Where-Object { $_.name -eq 'Label' }).Count -ge 1) 'Calculated Label missing'
Require (@($screen | Where-Object { $_.text -eq 'Customer.Name + " (" + Order.Total + ")"' }).Count -ge 1) 'Calculated expression missing'
Require (@($screen | Where-Object { $_.type -eq 'GroupByAttribute' }).Count -ge 1) 'Group by missing'
foreach ($name in @('JoinedOrders','LoadSummary')) {
  Require (@($screen | Where-Object { $_.type -eq 'RefreshQuery' -and $_.ref -eq $name }).Count -eq 1) "Refresh $name missing"
}
Require (@($screen | Where-Object { $_.type -eq 'WebDestination' -and $_.ref -eq 'Detail' }).Count -eq 1) 'Destination missing'
Require (@($screen | Where-Object { $_.type -eq 'EventHandler' -and $_.ref -eq 'Detail' }).Count -eq 2) 'Link/Button navigation missing'
Require (@($screen | Where-Object { $_.type -eq 'Argument' -and $_.ref -eq 'CustomerId' }).Count -eq 3) 'Navigation arguments missing'
Require (@($screen | Where-Object { $_.text -eq 'JoinedOrders.List.Current.Order.CustomerId' }).Count -ge 1) 'Link argument value missing'
Require (@($screen | Where-Object { $_.type -eq 'SwitchCondition' -and $_.target }).Count -eq 2) 'Switch case targets missing'
Require (@($screen | Where-Object { $_.type -eq 'Otherwise' -and $_.target -eq 'End' }).Count -eq 1) 'Switch otherwise missing'
Require (@($screen | Where-Object { $_.text -eq 'SelectedCustomer <> NullIdentifier()' }).Count -eq 1) 'Switch condition missing'
Write-Host 'ok   queries wiring'

$negative = @(
  @{ name = 'bad-join'; spec = '{ "entities": [{"name":"Order","attributes":[{"name":"Name"}]}], "screens": [{"name":"BadJoin", "aggregates":[{"name":"Query", "sources":["Order"], "joins":[{"type":"only with","left":"Order","right":"Missing","condition":"True"}]}]}]}'; problem = "no aggregate source 'Missing'" },
  @{ name = 'bad-refresh'; spec = '{"screens":[{"name":"Owner", "aggregates":[{"name":"OtherQuery"}]}, {"name":"Caller", "actions":[{"name":"Refresh", "flow":[{"kind":"refreshData","dataSource":"OtherQuery"}]}]}]}'; problem = "no data source 'OtherQuery' in owning screen 'Caller'" },
  @{ name = 'bad-navigation'; spec = '{"screens":[{"name":"Caller", "actions":[{"name":"Go", "flow":[{"kind":"destination","screen":"Missing"}]}]}]}'; problem = "named 'Missing'" },
  @{ name = 'bad-switch'; spec = '{"screens":[{"name":"Caller", "actions":[{"name":"Choose", "flow":[{"kind":"switch","cases":[{"condition":"True","next":"End"}],"otherwise":"End"}]}]}]}'; problem = 'Cannot change the target node' }
)
foreach ($case in $negative) {
  $specPath = Join-Path (Split-Path $module) ($case.name + '.json')
  $outPath = Join-Path (Split-Path $module) ($case.name + '.oml')
  Set-Content -LiteralPath $specPath -Value $case.spec
  $report = & $oml apply $source $specPath $outPath 2>$null | ConvertFrom-Json
  Require ($LASTEXITCODE -eq 1 -and -not $report.saved -and -not (Test-Path -LiteralPath $outPath)) "$($case.name) saved or crashed"
  Require (@($report.problems | Where-Object { $_.Contains($case.problem) }).Count -ge 1) "$($case.name) did not report the expected problem"
}
Write-Host 'ok   queries refusal paths'
