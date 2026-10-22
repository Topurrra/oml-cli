# The architect skill (skill/architect.md, skill/reference/worker-brief.md):
#   1. every `oml` command, option, doc topic, recipe and spec key that the two files name exists;
#   2. the worked example runs: two spec parts built from recipes, spec check, ONE multi-part apply, review --md, check, preview light and dark;
#   3. the same two parts as one repeated part are refused (the "check overlaps by hand" claim).
# Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $Tmp 'architect-check'; Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $work | Out-Null
function Fail($m) { throw "architect: $m" }
function Run([string[]]$a) {
  $out = & $Oml @a 2>&1
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = ($out | ForEach-Object { "$_" }) -join "`n" }
}

# --- 1. Names in the docs exist. ---
$files = 'skill/architect.md', 'skill/reference/worker-brief.md' | ForEach-Object { Join-Path $repo $_ }
$help = (Run '--help').Text
$commands = @($help -split "`n" | Where-Object { $_ -match '^oml ([a-z]+)' } | ForEach-Object { $Matches[1] } | Sort-Object -Unique)
$allHelp = $help
foreach ($c in $commands) { $allHelp += "`n" + (Run $c, '--help').Text }
$seenCmd = @{}; $seenFlag = @{}
foreach ($f in $files) {
  $text = Get-Content -Raw $f
  # `oml <word>` in backticks or at the start of a code line
  foreach ($m in [regex]::Matches($text, '(?m)(?:`|^)oml ([a-z]+)')) {
    $c = $m.Groups[1].Value
    if ($c -eq 'memory') { continue } # built in parallel; the docs name it only as "if it exists"
    if ($commands -notcontains $c) { Fail "$(Split-Path $f -Leaf) names unknown command 'oml $c'" }
    $seenCmd[$c] = 1
  }
  foreach ($m in [regex]::Matches((($text -split "`n" | Where-Object { $_ -match "oml " }) -join "`n"), "(--[a-z][a-z-]*)")) {
    $o = $m.Groups[1].Value
    if ($allHelp -notmatch ('(?<![\w-])' + [regex]::Escape($o) + '(?![\w-])')) { Fail "$(Split-Path $f -Leaf) names unknown option $o" }
    $seenFlag[$o] = 1
  }
  foreach ($m in [regex]::Matches($text, 'oml doc ([a-z-]+)')) {
    $r = Run 'doc', $m.Groups[1].Value
    if ($r.Exit -ne 0) { Fail "oml doc $($m.Groups[1].Value) exits $($r.Exit)" }
  }
  foreach ($m in [regex]::Matches($text, 'skill/recipes/([a-z-]+\.jsonc)')) {
    if (-not (Test-Path (Join-Path $repo "skill/recipes/$($m.Groups[1].Value)"))) { Fail "recipe $($m.Groups[1].Value) does not exist" }
  }
  # Recipe names in backticks after "Recipe:" and spec keys after "Spec keys:"
  foreach ($line in ($text -split "`n")) {
    if ($line -match 'Recipe: `([a-z-]+)`') { if (-not (Test-Path (Join-Path $repo "skill/recipes/$($Matches[1]).jsonc"))) { Fail "recipe $($Matches[1]) does not exist" } }
    if ($line -match 'Spec keys:') {
      foreach ($k in [regex]::Matches((($line.Substring($line.IndexOf('Spec keys:')) -split '\. ')[0]), '`([A-Za-z]+)`')) { # up to the end of the sentence
        $r = Run 'spec', $k.Groups[1].Value
        if ($r.Exit -ne 0 -or $r.Text.Length -lt 20) { Fail "spec key $($k.Groups[1].Value) is unknown" }
      }
    }
  }
}
foreach ($c in 'run', 'inspect', 'describe', 'apply', 'review', 'check', 'preview', 'spec', 'doc', 'validate', 'install', 'publish') {
  if (-not $seenCmd.ContainsKey($c)) { Fail "docs no longer name 'oml $c'; update this check" }
}
Write-Host "ok   architect docs: $($seenCmd.Count) commands, $($seenFlag.Count) options, doc topics, recipes and spec keys exist"

# --- 2. The worked example. ---
function Ok($r, $what) { if ($LASTEXITCODE -ne 0 -or -not $r.saved -or $r.newErrors.Count -or $r.reopenNewErrors.Count -or $r.problems.Count) { Fail "$what failed: $($r | ConvertTo-Json -Depth 8 -Compress)" } }
$oldTemp = $env:TEMP; $oldTmp = $env:TMP
$env:TEMP = $work; $env:TMP = $work
try {
$ui = Join-Path $work 'ui-base.json'
Set-Content $ui -Encoding utf8 @'
{ "blocks": [ { "name": "LayoutTopMenu", "flow": "Common", "widgets": [
    { "type": "Placeholder", "name": "Title" }, { "type": "Placeholder", "name": "Actions" },
    { "type": "Placeholder", "name": "MainContent" } ] } ],
  "onException": { "name": "OnException", "flow": "MainFlow", "nodes": [
    { "kind": "exceptionHandler", "name": "OnAllErrors", "exception": "AllExceptions", "logError": true },
    { "kind": "message", "message": "\"Something went wrong\"", "type": "error" } ] },
  "globalExceptionHandler": "MainFlow/OnException" }
'@
& $Oml new (Join-Path $work 'shop0.oml') --name Shop --kind CrossDevice | Out-Null
$r = & $Oml apply (Join-Path $work 'shop0.oml') $ui (Join-Path $work 'shop.oml') | ConvertFrom-Json; Ok $r 'Shop base'
& $Oml new (Join-Path $work 'core0.oml') --name Core --kind CrossDevice | Out-Null
$coreSpec = Join-Path $work 'core.json'
Set-Content $coreSpec -Encoding utf8 '{ "entities": [{ "name": "Customer", "public": true, "attributes": [{ "name": "Name", "length": 100 }, { "name": "Email", "type": "Email", "length": 250 }] }] }'
$r = & $Oml apply (Join-Path $work 'core0.oml') $coreSpec (Join-Path $work 'core.oml') | ConvertFrom-Json; Ok $r 'Core'
$shop = Join-Path $work 'shop.oml'; $core = Join-Path $work 'core.oml'

# Lane A: the list-search-paging recipe with the names of brief A.
$a = (Get-Content -Raw (Join-Path $repo 'skill/recipes/list-search-paging.jsonc')) -creplace 'Customers', 'Orders' -creplace 'Customer', 'Order' -creplace '\bName\b', 'Number' -creplace '\bCity\b', 'Status' -creplace 'No customers match', 'No orders match'
$partA = Join-Path $work 'orders-screen.json'; Set-Content $partA -Encoding utf8 $a
# Lane B: copy the recipe that brief B names, then edit it. The test Core has no Product_GetById action, so the edit
# drops the execute node and the second element; the rest follows the brief's rename list.
$producer = $core -replace '\\', '/'
$partB = Join-Path $work 'customer-validate.json'
$b = Get-Content -Raw (Join-Path $repo 'skill/recipes/references-from-producer.jsonc')
$b = $b -replace '(?m)^\s*\{ "kind": "execute".*\r?\n', ''
$b = $b.Replace('tmp/recipes/producer.oml', $producer).Replace('"Product", "Product_GetById"', '"Customer"')
$b = $b.Replace('"UseCatalog"', '"Customer_Validate"').Replace('"name": "Id"', '"name": "CustomerId"').Replace('RecipeProducer.Product Identifier', 'Core.Customer Identifier')
$b = $b.Replace('"Counts products of the producer."', '"Checks that a customer of Core exists."')
$b = $b.Replace('{ "name": "ProductCount", "type": "Integer", "description": "Number of products." }', '{ "name": "IsValid", "type": "Boolean" }, { "name": "Reason", "type": "Text" }')
$b = $b.Replace('"name": "Products", "sources": ["RecipeProducer.Product"], "maxRecords": 10', '"name": "FindCustomer", "sources": ["Core.Customer"], "filters": ["Core_Customer.Id = CustomerId"], "maxRecords": 1')
$b = $b.Replace('{ "ProductCount": "Products.List.Length" }', '{ "IsValid": "NOT FindCustomer.List.Empty", "Reason": "\"\"" }')
if (($b -replace '(?m)^//.*','') -match 'Product') { Fail "recipe edit left 'Product' in part B: $b" }
Set-Content $partB -Encoding utf8 $b
foreach ($p in $partA, $partB) {
  $r = Run 'spec', 'check', $p
  if ($r.Exit -ne 0 -or $r.Text -notmatch '"ok":true') { Fail "spec check $p : $($r.Text)" }
}
# ONE apply for both parts.
$staged = Join-Path $work 'shop.staged.oml'
$r = & $Oml apply $shop $partA $partB $staged --describe-created | ConvertFrom-Json; Ok $r 'multi-part apply'
if (@($r.specs).Count -ne 2) { Fail "apply did not report both parts: $($r.specs)" }
foreach ($e in 'entity Order', 'screen Orders', 'serverAction Customer_Validate') { if ($r.created -notcontains $e) { Fail "apply did not create '$e': $($r.created -join ', ')" } }
# Only the one warning the doc names.
$other = @($r.newWarnings | Where-Object { $_ -and -not ($_.id -eq 'UnusedUserAction' -and @($_.samples | Where-Object { $_.detail -notlike "'Customer_Validate' action is never used*" }).Count -eq 0) })
if ($other.Count) { Fail "unexpected new warnings: $($other | ConvertTo-Json -Depth 6 -Compress)" }
if (-not @($r.newWarnings | Where-Object id -eq 'UnusedUserAction').Count) { Fail 'the UnusedUserAction warning named in the doc is gone; update the doc' }
$md = Join-Path $work 'shop.review.md'
$rv = & $Oml review $shop $staged --md $md | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $md) -or $rv.summary.added -lt 3 -or $rv.summary.risks -ne 0) { Fail "review: $($rv | ConvertTo-Json -Depth 5 -Compress)" }
$ck = & $Oml check $staged | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $null -eq $ck.summary) { Fail 'check did not report' }
$pv = Join-Path $work 'preview.json'
$steps = foreach ($th in 'light', 'dark') { , @('preview', $staged, 'Orders', '--theme', $th, '--out', (Join-Path $work "Orders.$th.html")) }
Set-Content $pv -Encoding utf8 (@{ steps = @($steps) } | ConvertTo-Json -Depth 5)
$pr = & $Oml run $pv | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or @($pr.results | Where-Object exitCode -ne 0).Count -or @($pr.results).Count -ne 2) { Fail 'preview of Orders (light and dark) failed' }
Write-Host "ok   architect example: 2 parts, one apply (saved, 0 newErrors, only UnusedUserAction), review --md ($($rv.summary.added) added, 0 risks), check, preview light and dark"

# --- 3. A repeated part is refused. ---
$r = Run 'apply', $shop, $partA, $partA, (Join-Path $work 'dup.oml')
if ($r.Exit -eq 0 -or $r.Text -notmatch '"saved":false') { Fail "a repeated part was not refused: $($r.Text)" }
Write-Host 'ok   architect: a repeated part gives saved=false'
} finally { $env:TEMP = $oldTemp; $env:TMP = $oldTmp }
