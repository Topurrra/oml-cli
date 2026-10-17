param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($condition, $message) { if (!$condition) { throw $message } }
function Read-Oml([string[]]$cliArgs) {
    $text = & $Oml @cliArgs
    Require ($LASTEXITCODE -eq 0) "Command failed: $cliArgs $text"
    return (($text -join "`n") | ConvertFrom-Json)
}
$blank = Join-Path $Tmp 'describe-blank.oml'
$out = Join-Path $Tmp 'describe-logic.oml'
Remove-Item $blank,$out -ErrorAction SilentlyContinue
Read-Oml @('new',$blank,'--kind','CrossDevice','--name','DescribeTest') | Out-Null
# Derive the fixture and expose one action without changing the shared fixture.
$spec = Get-Content (Join-Path $PSScriptRoot 'logic.json') -Raw | ConvertFrom-Json
$spec.entities[0] | Add-Member -NotePropertyName public -NotePropertyValue $true
$spec.serverActions[0] | Add-Member -NotePropertyName public -NotePropertyValue $true
$spec.screens[0] | Add-Member -NotePropertyName dataActions -NotePropertyValue @(@{
    name='LoadValue';outputs=@(@{name='DataValue';type='Integer'});flow=@(@{kind='assign';assign=@{DataValue='7'}})
})
$specPath = Join-Path $Tmp 'describe-spec.json'
$spec | ConvertTo-Json -Depth 80 | Set-Content $specPath
$r = Read-Oml @('apply',$blank,$specPath,$out)
Require ($r.saved -and !$r.newErrors -and !$r.reopenNewErrors) 'describe fixture did not save cleanly'
$a = Read-Oml @('describe',$out,'DescribeTest.CreateOrderFor')
Require ($a.public -eq $true -and $a.text.Contains('[public]')) 'describe public flag'
foreach ($line in @('if Total >= 0:', 'else:', 'call CreateOrder(Source = Rec)  [entity action]', 'raise InvalidOrder("Total must be >= 0")')) {
    Require ($a.text.Contains($line)) "describe missing $line"
}
Require ($a.calls -contains 'DescribeTest.CreateOrder') 'describe qualified call'
Require ($a.raises -contains 'DescribeTest.InvalidOrder') 'describe qualified raise'
$sum = Read-Oml @('describe',$out,'SumCustomerOrders')
foreach ($line in @('query GetOrders: Order where Order.CustomerId = CustomerId order by Order.Id desc max 50', 'for each GetOrders.List:', '-> continue at Loop')) {
    Require ($sum.text.Contains($line)) "describe missing $line"
}
$screen = Read-Oml @('describe',$out,'Orders','--depth','1')
foreach ($line in @('OnInitialize -> Init','ScreenAction Save(', 'Button SaveButton', 'onClick=Save','ServerAction CreateOrderFor(')) {
    Require ($screen.text.Contains($line)) "screen describe missing $line"
}
Require ($screen.text.Contains('DataAction LoadValue() -> DataValue: Integer') -and !$screen.text.Contains('set DataValue = 7')) 'screen data action is not one line'
$data = Read-Oml @('describe',$out,'Orders/LoadValue')
Require ($data.text.Contains('set DataValue = 7')) 'standalone data action missing body'
$action = Read-Oml @('describe',$out,'Orders/Save')
Require ($action.text.Contains('call DescribeTest.CreateOrderFor(')) 'screen action path'
$shallow = Read-Oml @('describe',$out,'Orders/Save')
Require ($shallow.text.Contains('[signature only]') -and !$shallow.text.Contains('if Total')) 'default depth expanded a body'
$inspect = Read-Oml @('inspect',$out,'--only','actions')
Require (($inspect.serverActions | Where-Object name -eq 'CreateOrderFor').public -eq $true) 'inspect public flag'
Require (@($inspect.PSObject.Properties.Name | Where-Object { $_ -notin @('serverActions','serviceActions','clientActions') }).Count -eq 0) 'inspect --only leaked sections'
$full = Read-Oml @('inspect',$out,'--pretty')
Require ($full.PSObject.Properties.Name -contains 'references') 'pretty lost empty fields'
$own = Read-Oml @('inspect',$out,'--pretty','--own')
Require ($own.PSObject.Properties.Name -notcontains 'references') 'inspect --own retained references'
$compact = & $Oml inspect $out
Require ($LASTEXITCODE -eq 0 -and @($compact).Count -eq 1) 'default JSON is not one line'
Require ($compact -notmatch ': null|:\[\]|:\{\}') 'compact JSON retained empty fields'
$pretty = & $Oml --pretty inspect $out --only actions
Require ($LASTEXITCODE -eq 0 -and @($pretty).Count -gt 1 -and ($pretty -join "`n").Contains('"serviceActions": []')) 'global --pretty did not preserve empties'
$first = & $Oml describe $out Orders
$second = & $Oml describe $out Orders
Require (($first -join '') -eq ($second -join '')) 'describe is not deterministic'
# Producer bodies are expanded only when their file is supplied.
$consumerBlank = Join-Path $Tmp 'describe-consumer-blank.oml'
Remove-Item $consumerBlank -ErrorAction SilentlyContinue
Read-Oml @('new',$consumerBlank,'--kind','CrossDevice','--name','DescribeConsumer') | Out-Null
$consumer = Join-Path $Tmp 'describe-consumer.oml'
Remove-Item $consumer -ErrorAction SilentlyContinue
@{ references = @(@{producer=$out;elements=@('Customer','CreateOrderFor')}); serverActions=@(@{name='Use';inputs=@(@{name='CustomerId';type='DescribeTest.Customer Identifier'});flow=@(@{kind='execute';action='DescribeTest.CreateOrderFor';args=@{CustomerId='CustomerId';Total='1'}})}) } | ConvertTo-Json -Depth 30 | Set-Content $specPath
$r = Read-Oml @('apply',$consumerBlank,$specPath,$consumer)
Require $r.saved 'describe consumer did not save'
$fullConsumer = Read-Oml @('inspect',$consumer)
Require ($fullConsumer.references.Count -gt 0) 'consumer missing references'
$ownConsumer = Read-Oml @('inspect',$consumer,'--own','--only','actions,references')
Require ($ownConsumer.PSObject.Properties.Name -notcontains 'references' -and $ownConsumer.serverActions) 'own filter retained populated references or lost actions' 
$without = Read-Oml @('describe',$consumer,'Use','--depth','1')
Require (!$without.text.Contains('if Total >= 0:')) 'reference proxy treated as body'
$with = Read-Oml @('describe',$consumer,'Use','--depth','1','--in',$out)
Require ($with.text.Contains('if Total >= 0:')) 'producer body not expanded'
Write-Host 'ok   describe, public flags, filters, compact and pretty JSON'
