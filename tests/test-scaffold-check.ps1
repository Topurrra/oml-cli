# oml test scaffold: builds a stand-in BDDFramework (tests/bdd-standin.json, unverified against the real Forge component)
# and a module under test (tests/bdd-producer.json), writes the spec, applies it to a new test module with no errors and
# no new warnings, applies it again, describes and previews the test screen, checks the refusals, that the read modules
# did not change, and that skill/recipes/bdd-test-screen.jsonc is the scaffold output for PlaceOrder. Throws on failure.
# The stand-in has the block names from the OutSystems docs; its Description inputs, Steps placeholder, StepAction event
# and AssertTrue(Condition, FailureMessage) are assumptions.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $Tmp ('test-scaffold-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Force $work | Out-Null
function Fail($m) { throw "test scaffold: $m" }
function Build($name, $fixture) {
  $blank = Join-Path $work "$name-blank.oml"; $out = Join-Path $work "$name.oml"
  & $Oml new $blank --name $name --kind CrossDevice | Out-Null
  $r = & $Oml apply $blank (Join-Path $PSScriptRoot $fixture) $out | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0 -or -not $r.saved -or @($r.newWarnings | Where-Object { $_ -and $_.id -ne 'PublicServerAction' }).Count) { Fail "building $name`: $($r | ConvertTo-Json -Depth 8 -Compress)" }
  $out
}
function Scaffold([string[]]$a) {
  $out = & $Oml test scaffold @a 2>&1
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = ($out | ForEach-Object { "$_" }) -join "`n" }
}
function Assert-Describe($module, $element, [string[]]$patterns) {
  $d = & $Oml describe $module $element | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) { Fail "describe $element exit $LASTEXITCODE" }
  foreach ($p in $patterns) { if ($d.text -notmatch [regex]::Escape($p)) { Fail "describe $element lacks '$p'`n$($d.text)" } }
}

$bdd = Build 'BDDFramework' 'bdd-standin.json'
$producer = Build 'OrderCore' 'bdd-producer.json'
$before = (Get-FileHash $bdd).Hash + (Get-FileHash $producer).Hash

# 1. The spec: one screen per action, the scenarios, what was read from the framework, the TODO count and the notes.
$spec = Join-Path $work 'tests.json'
$r = Scaffold $producer, 'PlaceOrder', 'CancelOrder', 'Ping', '--bdd', $bdd, '--out', $spec
if ($r.Exit -ne 0) { Fail "scaffold exit $($r.Exit): $($r.Text)" }
$report = $r.Text | ConvertFrom-Json
$scenarios = ($report.screens | ForEach-Object { "$($_.screen)=$($_.scenarios -join '+')" }) -join ' '
if ($scenarios -ne 'PlaceOrder_Tests=HappyPath+RaisesInvalidQuantity+EmptyCustomerName CancelOrder_Tests=HappyPath+RaisesInvalidQuantity+EmptyOrderNumber+EmptyCountryCode Ping_Tests=HappyPath') { Fail "scenarios: $scenarios" }
if ($report.framework.step -ne 'BDDStep(Description), event StepAction' -or $report.framework.scenario -ne 'BDDScenario(Description), placeholder Steps' -or $report.framework.assert -ne 'AssertTrue(Condition, FailureMessage)') { Fail "framework: $($report.framework | ConvertTo-Json -Compress)" }
if ($report.testModule -ne 'OrderCore_Tests' -or $report.flow -ne 'TestFlow' -or $report.verified -ne 'stand-in only' -or $report.next[0] -ne 'oml new OrderCore_Tests.oml --name OrderCore_Tests --kind CrossDevice' -or $report.todo -lt 10 -or -not ($report.notes -match 'unverified') -or $report.next[2] -notmatch '^oml apply OrderCore_Tests\.oml ' -or $report.next[-1] -notmatch '^oml review OrderCore_Tests\.oml \S+ --md OrderCore_Tests\.review\.md$') { Fail "report: $($r.Text)" }
$text = [IO.File]::ReadAllText($spec)
if ($text.Contains("`r") -or [IO.File]::ReadAllBytes($spec)[0] -ne 123) { Fail 'spec is not UTF-8 without BOM with LF endings' }
& $Oml spec check $spec | Out-Null
if ($LASTEXITCODE -ne 0) { Fail 'oml spec check refused the spec' }

# 2. Apply to a new test module: no problems, no errors, no new warnings. Then again: everything already exists.
$tests = Join-Path $work 'OrderCore_Tests.oml'; $staged = Join-Path $work 'OrderCore_Tests.staged.oml'
& $Oml new $tests --name OrderCore_Tests --kind CrossDevice | Out-Null
$a = & $Oml apply $tests $spec $staged | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $a.saved -or $a.problems.Count -or $a.newErrors.Count -or $a.reopenNewErrors.Count -or $a.newWarnings.Count -or $a.warnings.Count) { Fail "apply: $($a | ConvertTo-Json -Depth 8 -Compress)" }
$again = & $Oml apply $staged $spec (Join-Path $work 'again.oml') | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $again.saved -or $again.created.Count -or $again.newWarnings.Count) { Fail "second apply: $($again | ConvertTo-Json -Depth 8 -Compress)" }

# 3. The test screens: steps bound through the step event, the call, the asserts, the TODO values, the exception handler.
Assert-Describe $staged PlaceOrder_Tests @(
  'event StepAction=HappyPath_Given()', 'event StepAction=RaisesInvalidQuantity_Then()', 'event StepAction=EmptyCustomerName_When()',
  'set HappyPath_CustomerName = "TODO"', 'set HappyPath_Quantity = 0 /* TODO */', 'set EmptyCustomerName_CustomerName = ""',
  'call OrderCore.PlaceOrder(Attachment = HappyPath_Attachment, CustomerName = HappyPath_CustomerName, Express = HappyPath_Express, Extra = HappyPath_Extra, Line = HappyPath_Line, Quantity = HappyPath_Quantity)',
  'Condition = False /* TODO: check Receipt */',
  'set HappyPath_Total = CallPlaceOrder.Total',
  'call BDDFramework.AssertTrue(Condition = RaisesInvalidQuantity_Message = "Quantity must be positive"',
  'on exception All Exceptions', 'set RaisesInvalidQuantity_Message = OnError.ExceptionMessage', 'sourceBlock=FinalResult')
Assert-Describe $staged CancelOrder_Tests @('Condition = False /* TODO: check what CancelOrder changed */',
  'set HappyPath_CountryCode = NullTextIdentifier() /* TODO */', 'set EmptyCountryCode_CountryCode = NullTextIdentifier()', 'set HappyPath_CancelledOn = #1900-01-01# /* TODO */')
Assert-Describe $staged Ping_Tests @('call OrderCore.Ping()', 'event StepAction=HappyPath_Given()', 'Condition = False /* TODO: check what Ping changed */')
if ((& $Oml describe $staged CancelOrder_Tests | ConvertFrom-Json).text -match 'CheckMessage') { Fail 'CancelOrder raises a computed message: there must be no message check' }

# 4. Preview: the heading shows, and the screen holds 3 scenarios, 9 steps and the final result.
$png = Join-Path $work 'PlaceOrder_Tests.png'
$p = & $Oml preview $staged PlaceOrder_Tests --out (Join-Path $work 'PlaceOrder_Tests.html') --screenshot $png `
  --eval "document.querySelectorAll('.BDDScenario').length + ':' + document.querySelectorAll('.BDDStep').length + ':' + document.querySelectorAll('.FinalResult').length + ':' + document.body.innerText.includes('PlaceOrder tests')" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $p.steps[0].result -ne '3:9:1:true') { Fail "preview: $($p | ConvertTo-Json -Depth 6 -Compress)" }
if ($p.screenshot -and -not (Test-Path $png)) { Fail 'preview reported a screenshot that is missing' }

# 5. Refusals, each with exit code and message. None writes a spec.
$refused = Join-Path $work 'refused.json'
$web = Join-Path $work 'web.oml'; & $Oml new $web --name WebBdd --kind WebResponsive | Out-Null
foreach ($case in @(
  @{ a = @($producer, '--bdd', $bdd, '--out', $refused); exit = 2; text = 'at least one <ServerAction>' },
  @{ a = @($producer, 'PlaceOrder', '--out', $refused); exit = 2; text = 'needs --bdd <BDDFramework.oml>' },
  @{ a = @($producer, 'PlaceOrder', '--bdd', $producer, '--out', $refused); exit = 1; text = "no public block 'BDDScenario'" },
  @{ a = @($producer, 'PlaceOrder', '--bdd', $web, '--out', $refused); exit = 1; text = 'is a Traditional Web module' },
  @{ a = @($producer, 'AuditOrder', '--bdd', $bdd, '--out', $refused); exit = 1; text = "server action 'AuditOrder' is not public" },
  @{ a = @($producer, 'Nope', '--bdd', $bdd, '--out', $refused); exit = 1; text = 'Public server actions: CancelOrder, Ping, PlaceOrder.' },
  @{ a = @($bdd, 'AssertTrue', '--bdd', $bdd, '--out', $refused); exit = 1; text = "'AssertTrue' is a client action" },
  @{ a = @($producer, 'PlaceOrder', 'PlaceOrder', '--bdd', $bdd, '--out', $refused); exit = 2; text = 'listed twice' },
  @{ a = @($producer, 'PlaceOrder', '--bdd', $bdd, '--module-name', 'Bad Name', '--out', $refused); exit = 2; text = 'must be a plain name' },
  @{ a = @($producer, 'PlaceOrder', '--bdd', $bdd, '--out', $spec); exit = 2; text = 'Output already exists' },
  @{ a = @($producer, 'PlaceOrder', '--bdd', $bdd, '--colour', 'x'); exit = 2; text = 'test scaffold accepts' })) {
  $r = Scaffold $case.a
  if ($r.Exit -ne $case.exit -or $r.Text -notmatch [regex]::Escape($case.text)) { Fail "refusal '$($case.text)': exit $($r.Exit), $($r.Text)" }
}
if (Test-Path $refused) { Fail 'a refused scaffold wrote a spec' }
$r = Scaffold @()
if ($r.Exit -ne 2 -or $r.Text -notmatch 'oml test scaffold <module.oml>') { Fail "no arguments: exit $($r.Exit), $($r.Text)" }

# 6. The framework names come from the --bdd file: variants of the stand-in that do not fit are refused with what was
#    found; a renamed step event is used as it is.
$standin = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'bdd-standin.json'))
function Variant($name, [string]$from, [string]$to) {
  if (-not $standin.Contains($from)) { Fail "variant ${name}: the stand-in has no '$from'" }
  $json = Join-Path $work "$name.json"; [IO.File]::WriteAllText($json, $standin.Replace($from, $to))
  $blank = Join-Path $work "$name-blank.oml"; $out = Join-Path $work "$name.oml"
  & $Oml new $blank --name BDDFramework --kind CrossDevice | Out-Null
  $b = & $Oml apply $blank $json $out | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0 -or -not $b.saved) { Fail "variant ${name}: $($b | ConvertTo-Json -Depth 6 -Compress)" }
  $out
}
$stepDecl = '{ "name": "BDDStep", "public": true, "inputs": [ { "name": "Description", "type": "Text", "mandatory": true } ]'
foreach ($case in @(
  @{ name = 'NoPlaceholder'; from = ', { "type": "Placeholder", "name": "Steps" }'; to = ''; text = 'BDDScenario has placeholders (none)' },
  @{ name = 'TwoPlaceholders'; from = '{ "type": "Placeholder", "name": "Steps" }'; to = '{ "type": "Placeholder", "name": "Top" }, { "type": "Placeholder", "name": "Bottom" }'; text = 'BDDScenario has placeholders Top, Bottom' },
  @{ name = 'AssertEquals'; from = '"name": "AssertTrue"'; to = '"name": "AssertEquals"'; text = "no public client action 'AssertTrue'. Public client actions named Assert*: AssertEquals" },
  @{ name = 'StepKind'; from = $stepDecl; to = $stepDecl.Replace(' } ]', ' }, { "name": "Kind", "type": "Integer", "mandatory": true } ]'); text = "BDDStep has a mandatory input 'Kind'" },
  @{ name = 'PrivateStep'; from = '{ "name": "BDDStep", "public": true'; to = '{ "name": "BDDStep", "public": false'; text = "no public block 'BDDStep'" })) {
  $r = Scaffold $producer, 'Ping', '--bdd', (Variant $case.name $case.from $case.to), '--out', $refused
  if ($r.Exit -ne 1 -or $r.Text -notmatch [regex]::Escape($case.text)) { Fail "variant $($case.name): exit $($r.Exit), $($r.Text)" }
}
if (Test-Path $refused) { Fail 'a refused variant wrote a spec' }
$renamed = Variant 'RenamedEvent' '"StepAction"' '"OnStepRun"'
$specRenamed = Join-Path $work 'renamed.json'
$r = Scaffold $producer, 'Ping', '--bdd', $renamed, '--out', $specRenamed
if ($r.Exit -ne 0 -or ($r.Text | ConvertFrom-Json).framework.step -ne 'BDDStep(Description), event OnStepRun') { Fail "renamed event: $($r.Text)" }
$a = & $Oml apply $tests $specRenamed (Join-Path $work 'renamed.staged.oml') | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $a.newWarnings.Count) { Fail "renamed event apply: $($a | ConvertTo-Json -Depth 8 -Compress)" }
Assert-Describe (Join-Path $work 'renamed.staged.oml') Ping_Tests @('event OnStepRun=HappyPath_Then()')

# 7. A test module that already has a TestFlow (as one made from the BDD Framework Client Side app type): the screens are
#    added there and its OnException is reused; an exception handler with another name is refused with a clear problem.
foreach ($handler in 'OnException', 'HandleTestErrors') {
  $json = Join-Path $work "flow-$handler.json"
  [IO.File]::WriteAllText($json, '{ "onException": { "name": "' + $handler + '", "flow": "TestFlow", "nodes": [ { "kind": "exceptionHandler", "name": "OnAll", "exception": "AllExceptions" }, { "kind": "message", "message": "OnAll.ExceptionMessage", "type": "error" } ] }, "screens": [ { "name": "Home", "flow": "TestFlow" } ] }')
  $existing = Join-Path $work "flow-$handler.oml"
  $b = & $Oml apply $tests $json $existing | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) { Fail "test module with TestFlow: $($b | ConvertTo-Json -Depth 6 -Compress)" }
  $a = & $Oml apply $existing $spec (Join-Path $work "flow-$handler.staged.oml") | ConvertFrom-Json
  if ($handler -eq 'OnException') {
    if ($LASTEXITCODE -ne 0 -or $a.newWarnings.Count -or $a.existing -notcontains 'onException OnException' -or $a.created -notcontains 'screen PlaceOrder_Tests' -or @($a.created -match '^flow ').Count) { Fail "existing TestFlow: $($a | ConvertTo-Json -Depth 8 -Compress)" }
    Assert-Describe (Join-Path $work "flow-$handler.staged.oml") 'TestFlow/PlaceOrder_Tests' @('event StepAction=HappyPath_Given()')
  } elseif ($LASTEXITCODE -eq 0 -or -not ($a.problems -match 'flow TestFlow already has an exception handler')) { Fail "other handler name: $($a | ConvertTo-Json -Depth 8 -Compress)" }
}

# 8. It only reads: the framework and the module under test are unchanged.
if (((Get-FileHash $bdd).Hash + (Get-FileHash $producer).Hash) -ne $before) { Fail 'the scaffold changed a module it read' }

# 9. The recipe is the scaffold output for PlaceOrder, apart from the two producer paths.
$one = Join-Path $work 'place-order.json'
$r = Scaffold $producer, 'PlaceOrder', '--bdd', $bdd, '--out', $one, '--module-name', 'OrderCore_Tests', '--producer', 'tmp/recipes/OrderCore.oml'
if ($r.Exit -ne 0) { Fail "scaffold for the recipe: $($r.Text)" }
function Normal($path) {
  $j = ((Get-Content -Raw $path) -split "`n" | Where-Object { $_ -notmatch '^\s*//' }) -join "`n" | ConvertFrom-Json
  foreach ($ref in $j.references) { $ref.producer = '' }
  $j | ConvertTo-Json -Depth 64 -Compress
}
if ((Normal $one) -ne (Normal (Join-Path $repo 'skill/recipes/bdd-test-screen.jsonc'))) { Fail "skill/recipes/bdd-test-screen.jsonc differs from the scaffold output: regenerate it from $one" }
Write-Host 'ok   test scaffold: spec, clean apply and re-apply, describe, preview, refusals, read-only, recipe'
