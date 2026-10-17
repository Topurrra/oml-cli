param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($ok, $message) { if (-not $ok) { throw $message } }
function Nodes($node) { $node; foreach ($child in $node.children) { Nodes $child } }
function ApplyCase($name, $source, $spec, $expected, $message) {
  $path = Join-Path $Tmp "$name.json"
  $out = Join-Path $Tmp "$name.oml"
  $spec | ConvertTo-Json -Depth 40 | Set-Content $path
  $r = & $Oml apply $source $path $out | ConvertFrom-Json
  Require ($LASTEXITCODE -eq $expected) "$name exit: $($r | ConvertTo-Json -Depth 10)"
  if ($expected -eq 0) { Require ($r.saved -and $r.reopenNewErrors.Count -eq 0) "$name did not save cleanly" }
  else { Require (-not $r.saved -and -not (Test-Path $out) -and ($r.problems -join ' ') -match $message) "$name refusal: $($r | ConvertTo-Json -Depth 10)" }
  return $r
}
$sql = Join-Path $Tmp 'out-sql.json.oml'
$deleted = Join-Path $Tmp 'out-delete.json.oml'
$all = @(Nodes (& $Oml tree $sql SearchCustomers | ConvertFrom-Json))
Require (@($all | Where-Object text -eq 'GetTopCustomers.List.Current.Customer.Name').Count -eq 1) 'SQL entity output expression lost'
Require (@($all | Where-Object text -eq '"%" + Search + "%"').Count -eq 1) 'SQL parameter argument lost'
Require (@($all | Where-Object { $_.source -eq 'GetTopCustomers' -and $_.target -eq 'Loop' }).Count -eq 1) 'SQL connector lost'
$summary = @(Nodes (& $Oml tree $sql SummarizeCustomers | ConvertFrom-Json))
Require (@($summary | Where-Object text -eq 'GetSummary.List.Current.CustomerSummary.Caption').Count -eq 1) 'SQL structure output expression lost'
Require (@($summary | Where-Object { $_.source -eq 'Start' -and $_.target -eq 'InsertedSql' }).Count -eq 1) 'inserted SQL connector lost'
$data = & $Oml inspect $deleted | ConvertFrom-Json
Require (@($data.entities | Where-Object name -eq 'UnusedEntity').Count -eq 0) 'deleted entity remains'
$kept = & $Oml tree $deleted KeptEntity | ConvertFrom-Json
Require (@($kept.children | Where-Object name -eq 'UnusedColumn').Count -eq 0) 'deleted attribute remains'

foreach ($target in @('Customer', 'Customer/Name')) {
  $n = $target.Replace('/', '-')
  ApplyCase "delete-denied-$n" $sql @{edits=@(@{op='delete';element=$target})} 1 'refused:' | Out-Null
  ApplyCase "delete-used-$n" $sql @{edits=@(@{op='delete';element=$target;allowDataLoss=$true})} 1 'refused:' | Out-Null
}
ApplyCase 'delete-id' $deleted @{edits=@(@{op='delete';element='KeptEntity/Id';allowDataLoss=$true})} 1 'refused:' | Out-Null

# Each usage must be discovered independently, including expressions deferred within the same apply.
$uses = @(
  @('aggregate', @{serverActions=@(@{name='Consumer';flow=@(@{kind='aggregate';name='Fetch';sources=@('Victim');filters=@('Victim.Value <> ""');sorts=@(@{attribute='Victim.Value'})})})}, 'Consumer/Fetch'),
  @('local', @{serverActions=@(@{name='Consumer';locals=@(@{name='Rows';type='List of Victim'})})}, 'Consumer/Rows'),
  @('input', @{serverActions=@(@{name='Consumer';inputs=@(@{name='Row';type='Victim Identifier'})})}, 'Consumer/Row'),
  @('structure', @{structures=@(@{name='ContainerDto';attributes=@(@{name='Row';type='Victim'})})}, 'ContainerDto/Row'),
  @('foreign-key', @{entities=@(@{name='Child';attributes=@(@{name='VictimId';type='Victim Identifier'})})}, 'Child/VictimId'),
  @('crud', @{serverActions=@(@{name='Consumer';flow=@(@{kind='execute';action='GetVictim';args=@{Id='NullIdentifier()'}})})}, 'Consumer/GetVictim'),
  @('widget', @{screens=@(@{name='Consumer';locals=@(@{name='Row';type='Victim'});widgets=@(@{type='Expression';name='ValueText';value='Row.Value'})})}, 'Consumer/(Row|ValueText)')
)
foreach ($use in $uses) {
  $spec = $use[1]
  $spec.entities = @(@{name='Victim';attributes=@(@{name='Value'})}) + @($spec.entities | Where-Object { $null -ne $_ })
  $spec.edits = @(@{op='delete';element='Victim';allowDataLoss=$true})
  ApplyCase "delete-usage-$($use[0])" (Join-Path $Tmp 'r0.oml') $spec 1 "usages:.*$($use[2])" | Out-Null
}
foreach ($flow in @(
  @{kind='sql';name='ReadRows';sql='SELECT {Victim}.* FROM {Victim}';outputs=@('Victim')},
  @{kind='aggregate';name='ReadRows';sources=@('Victim');filters=@('Victim.Value <> ""');sorts=@(@{attribute='Victim.Value'})},
  @{kind='execute';name='ReadRows';action='GetVictim';args=@{Id='NullIdentifier()'}}
)) {
  ApplyCase "delete-used-attribute-$($flow.kind)" (Join-Path $Tmp 'r0.oml') @{
    entities=@(@{name='Victim';attributes=@(@{name='Value'})})
    serverActions=@(@{name='Consumer';flow=@($flow)})
    edits=@(@{op='delete';element='Victim/Value';allowDataLoss=$true})
  } 1 'usages:.*Consumer/ReadRows' | Out-Null
}
# Removing usages explicitly permits a later data deletion, including a deleted pending SQL node.
ApplyCase 'delete-after-usage-removal' (Join-Path $Tmp 'r0.oml') @{
  entities=@(@{name='Disposable';attributes=@(@{name='Value'})})
  serverActions=@(@{name='Consumer';flow=@(@{kind='sql';name='ReadRows';sql='SELECT {Disposable}.* FROM {Disposable}';outputs=@('Disposable')})})
  edits=@(@{op='delete';element='Consumer/ReadRows'}, @{op='delete';element='Disposable';allowDataLoss=$true})
} 0 '' | Out-Null
Write-Host 'ok   SQL outputs, arguments, inserted topology and guarded data deletion'
