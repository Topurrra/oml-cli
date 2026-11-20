param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'describe'
try {
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
# A client action with a JavaScript node (its code is printed), a message and a comment.
$spec.screens[0].actions += [pscustomobject]@{ name='Notify'; flow=@(
    @{ kind='javaScript'; name='Mark'; inputs=@(@{name='Value';type='Text'}); outputs=@(@{name='Out';type='Text'}); args=@{Value='"x"'}
       javaScript="var a = `$parameters.Value;`n`$parameters.Out = a + '!';" },
    @{ kind='message'; name='Tell'; message='"Saved"'; type='warning' },
    @{ kind='comment'; text='Explains the flow' }) }
$spec.screens[0].widgets[0].children[1] | Add-Member -NotePropertyName mandatory -NotePropertyValue 'True'
# A block with a description, an input default, HTML attributes, an icon, If() classes and a Visible expression, for describe.
$spec | Add-Member -NotePropertyName blocks -NotePropertyValue @(@{ name='Pill'; description='A small label.'; public=$true
    inputs=@(@{name='Tone';type='Text';mandatory=$false;default='"info"';description='info or danger.'}, @{name='IsSolid';type='Boolean';mandatory=$false;description='Filled.'}, @{name='Note';type='Text';mandatory=$false;description='Extra text.'})
    widgets=@(@{ type='Container'; name='PillRoot'; style='"pill pill--" + Tone + If(IsSolid, " is-solid", " is-soft")'; attributes=@{ role='"status"'; 'aria-label'='"Tone " + Tone' }
        children=@(@{type='Icon';name='PillIcon';icon='check'}, @{type='Expression';name='PillNote';value='Note';visible='Note <> "" and not IsSolid'}) }) }) -Force
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
Require ($screen.text.Contains('DataAction LoadValue() -> DataValue: Integer') -and $screen.text.Contains('set DataValue = 7')) 'screen data action body missing'
foreach ($line in @('javascript Mark(Value = "x") -> Out: Text:', "  var a = `$parameters.Value;", "  `$parameters.Out = a + '!';", 'message Warning: "Saved"', 'comment: Explains the flow', 'Container Header style="card"', 'Input TotalInput variable=NewTotal style="form-control" type=Number mandatory=True')) {
    Require ($screen.text.Contains($line)) "screen describe missing $line in: $($screen.text)"
}
$pill = Read-Oml @('describe',$out,'Pill')
foreach ($line in @('Block Pill(Tone: Text = "info", IsSolid: Boolean, Note: Text) [public]', 'attr role="status"', 'attr aria-label="Tone " + Tone', 'Icon PillIcon', 'icon=check',
    'style="pill pill--" + Tone + If(IsSolid, " is-solid", " is-soft")', 'visible=Note <> "" and not IsSolid')) {
    Require ($pill.text.Contains($line)) "block describe missing $line in: $($pill.text)"
}
$js = Read-Oml @('describe',$out,'Orders/Notify/Mark')
Require ($js.text.Contains('javascript:') -and $js.text.Contains("`$parameters.Out = a + '!';") -and !$js.text.Contains('[signature only]')) "JavaScript node code: $($js.text)"
$data = Read-Oml @('describe',$out,'Orders/LoadValue')
Require ($data.text.Contains('set DataValue = 7')) 'standalone data action missing body'
$action = Read-Oml @('describe',$out,'Orders/Save')
Require ($action.text.Contains('call DescribeTest.CreateOrderFor(')) 'screen action path'
# The default depth is 1: a called action's body is printed. --depth 0 prints its signature and says how to see more.
Require ($action.text.Contains('if Total >= 0:')) 'default depth did not print the called body'
$shallow = Read-Oml @('describe',$out,'Orders/Save','--depth','0')
Require ($shallow.text.Contains('[signature only: --depth 1 shows it]') -and !$shallow.text.Contains('if Total')) "--depth 0 expanded a body: $($shallow.text)"
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
$without = Read-Oml @('describe',$consumer,'Use')
Require (!$without.text.Contains('if Total >= 0:') -and $without.text.Contains('[signature only: in DescribeTest; add --in')) "reference proxy treated as body: $($without.text)"
$with = Read-Oml @('describe',$consumer,'Use','--depth','1','--in',$out)
Require ($with.text.Contains('if Total >= 0:')) 'producer body not expanded'
# Widget keys that set a model property by its name (spec.md, Reactive UI): each one must read back in describe.
# Reactive and Traditional Web take the same keys for a Button. A key the build lacks fails the apply and names the key.
$keysSpec = Join-Path $Tmp 'describe-keys.json'
$keysButton = @{ type='Button'; name='GoButton'; onClick='Go'; isDefault=$true; confirmationMessage='"Go now?"'; children=@(@{type='Text';text='Go'}) }
$keysScreen = @{ name='Keys'; locals=@(@{name='Secret';type='Text'}); actions=@(@{name='Go';flow=@(@{kind='assign';assign=@{Secret='""'}})}) }
$keysScreen.widgets = @(@{ type='Input'; name='SecretInput'; variable='Secret'; inputType='Password'; prompt='"Secret"'; maxLength=20 }, $keysButton)
@{ screens=@($keysScreen) } | ConvertTo-Json -Depth 30 | Set-Content $keysSpec
$keysOut = Join-Path $Tmp 'describe-keys.oml'
Remove-Item $keysOut -ErrorAction SilentlyContinue
$r = Read-Oml @('apply',$blank,$keysSpec,$keysOut)
Require ($r.saved -and !$r.newErrors -and !$r.reopenNewErrors) "Reactive widget keys did not save cleanly: $($r | ConvertTo-Json -Depth 8 -Compress)"
$keys = Read-Oml @('describe',$keysOut,'Keys')
foreach ($line in @('type=Password', 'prompt="Secret"', '[default button]', 'confirm="Go now?"')) {
    Require ($keys.text.Contains($line)) "Reactive widget keys: describe missing $line in: $($keys.text)"
}
$webKeysBlank = Join-Path $Tmp 'describe-web-blank.oml'
$webKeysOut = Join-Path $Tmp 'describe-web-keys.oml'
Remove-Item $webKeysBlank,$webKeysOut -ErrorAction SilentlyContinue
Read-Oml @('new',$webKeysBlank,'--kind','WebResponsive','--name','DescribeWebKeys') | Out-Null
$keysScreen.widgets = @($keysButton)
@{ screens=@($keysScreen) } | ConvertTo-Json -Depth 30 | Set-Content $keysSpec
$r = Read-Oml @('apply',$webKeysBlank,$keysSpec,$webKeysOut)
Require ($r.saved -and !$r.newErrors -and !$r.reopenNewErrors) "Traditional widget keys did not save cleanly: $($r | ConvertTo-Json -Depth 8 -Compress)"
$keys = Read-Oml @('describe',$webKeysOut,'Keys')
foreach ($line in @('[default button]', 'confirm="Go now?"')) {
    Require ($keys.text.Contains($line)) "Traditional widget keys: describe missing $line in: $($keys.text)"
}
# A long Advanced SQL: the describe line is cut and says how to read it; --sql prints it whole; --out writes it to a file.
$sqlBlank = Join-Path $Tmp 'describe-sql-blank.oml'
$sqlOut = Join-Path $Tmp 'describe-sql.oml'
$sqlSpec = Join-Path $Tmp 'describe-sql.json'
$sqlFile = Join-Path $Tmp 'describe-sql.sql'
Read-Oml @('new',$sqlBlank,'--kind','Service','--name','DescribeSql') | Out-Null
$columns = (0..40 | ForEach-Object { "{Item}.[Note] AS Col$_" }) -join ",`n  "
$statement = "SELECT {Item}.*,`n  $columns`nFROM {Item}`nWHERE {Item}.[Note] <> 'THE_LAST_WORD'"
@{ entities=@(@{ name='Item'; attributes=@(@{ name='Note' }) }); serverActions=@(@{ name='Report'; flow=@(@{ kind='sql'; name='GetAll'; sql=$statement; outputs=@('Item') }) }) } | ConvertTo-Json -Depth 30 | Set-Content $sqlSpec
$r = Read-Oml @('apply',$sqlBlank,$sqlSpec,$sqlOut)
Require ($r.saved) "long SQL fixture did not save: $($r | ConvertTo-Json -Depth 6 -Compress)"
$d = Read-Oml @('describe',$sqlOut,'Report')
Require ($d.text.Contains('[cut: ') -and $d.text.Contains('--sql]') -and -not $d.text.Contains('THE_LAST_WORD')) "describe of a long SQL does not say it is cut: $($d.text)"
$plain = (& $Oml describe $sqlOut Report --sql) -join "`n"
Require ($LASTEXITCODE -eq 0 -and $plain.Contains('THE_LAST_WORD') -and $plain.StartsWith('SELECT')) "describe --sql does not print the whole statement: $plain"
$o = Read-Oml @('describe',$sqlOut,'Report','--sql','--out',$sqlFile)
Require ($o.statements -eq 1 -and (Get-Content -Raw $sqlFile).Contains('THE_LAST_WORD') -and (Get-Content -Raw $sqlFile).StartsWith('-- ')) "describe --sql --out: $($o | ConvertTo-Json -Compress)"
Write-Host 'ok   describe, public flags, filters, compact and pretty JSON, long SQL (--sql, --out)'

} finally { Remove-TestWorkFolder $Tmp }
