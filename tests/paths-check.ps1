# Element paths: a unique top-level name wins, qualified forms work, an ambiguous name lists every candidate with a
# path that works in describe, tree and edits, a path never falls through to another element, edits never reach a
# referenced element, and index paths name the same element on every load.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($ok, $message) { if (-not $ok) { throw $message } }
function Oml([string[]]$cliArgs) {
  $text = & $Oml @cliArgs 2>$null
  [pscustomobject]@{ Code = $LASTEXITCODE; Json = (($text -join "`n") | ConvertFrom-Json); Text = ($text -join "`n") }
}
function Apply($source, $json, $out) {
  $spec = Join-Path $Tmp 'paths-spec.json'; Set-Content -Path $spec -Value $json -Encoding utf8
  Remove-Item $out -ErrorAction SilentlyContinue
  & $Oml apply $source $spec $out 2>$null | ConvertFrom-Json
}
function Run([string[][]]$steps) {
  $file = Join-Path $Tmp 'paths-steps.json'
  @{ steps = $steps } | ConvertTo-Json -Depth 6 | Set-Content $file
  ((& $Oml run $file --continue) -join "`n" | ConvertFrom-Json).results
}

foreach ($p in 'PathsProd', 'PathsTest') { & $Oml new (Join-Path $Tmp "$p-blank.oml") --name $p --kind CrossDevice | Out-Null }
$prod = Join-Path $Tmp 'PathsProd.oml'
$r = Apply (Join-Path $Tmp 'PathsProd-blank.oml') '{ "entities": [{ "name": "Region", "public": true, "attributes": [{ "name": "Label" }] }], "serverActions": [{ "name": "GetThing", "public": true }], "structures": [{ "name": "Thing", "public": true, "attributes": [{ "name": "Label" }] }] }' $prod
Require $r.saved 'producer'
# Names the model accepts side by side: a screen and an entity, a block and a screen in other flows, a flow and a
# screen, a server action and a block, two screen locals, own elements named like referenced ones, a screen named
# like a container in another screen. Plus a switch and a block instance, for index paths.
$m = Join-Path $Tmp 'paths.oml'
$spec = @{
  references = @(@{ producer = $prod; elements = @('Region', 'GetThing', 'Thing') })
  entities = @(@{ name = 'Sector'; attributes = @(@{ name = 'Label' }) })
  serverActions = @(@{ name = 'Menu' }, @{ name = 'Classify'; inputs = @(@{ name = 'N'; type = 'Integer' }); outputs = @(@{ name = 'R'; type = 'Integer' })
    flow = @(@{ kind = 'switch'; name = 'S'; cases = @(@{ condition = 'N = 1'; next = 'A1' }, @{ condition = 'N = 2'; next = 'A2' }, @{ condition = 'N = 3'; next = 'A3' }, @{ condition = 'N > 0'; next = 'A4' }); otherwise = 'A0' },
      @{ kind = 'assign'; name = 'A1'; assign = @{ R = '1' }; next = 'End' }, @{ kind = 'assign'; name = 'A2'; assign = @{ R = '2' }; next = 'End' },
      @{ kind = 'assign'; name = 'A3'; assign = @{ R = '3' }; next = 'End' }, @{ kind = 'assign'; name = 'A4'; assign = @{ R = '4' }; next = 'End' },
      @{ kind = 'assign'; name = 'A0'; assign = @{ R = '0' }; next = 'End' }) })
  blocks = @(@{ name = 'Card'; flow = 'Common' }, @{ name = 'Menu'; flow = 'Common' },
    @{ name = 'Badge'; flow = 'Common'; inputs = @(@{ name = 'Zed'; type = 'Text' }, @{ name = 'Alpha'; type = 'Text' }, @{ name = 'Mid'; type = 'Text' }) })
  screens = @(
    @{ name = 'Sector' }, @{ name = 'Card' }, @{ name = 'Admin' }, @{ name = 'Users'; flow = 'Admin' },
    @{ name = 'Other'; locals = @(@{ name = 'Shared'; type = 'Integer' }) }, @{ name = 'Third'; locals = @(@{ name = 'Shared'; type = 'Integer' }) },
    @{ name = 'Region' }, @{ name = 'GetThing'; locals = @(@{ name = 'Thing'; type = 'PathsProd.Thing' }) },
    @{ name = 'Dashboard'; widgets = @(@{ type = 'Expression'; name = 'Caption'; value = '"dashboard caption"' }) },
    @{ name = 'Home'; widgets = @(@{ type = 'Container'; name = 'Dashboard'; children = @(@{ type = 'Expression'; name = 'Title'; value = '"home title"' }) }) },
    @{ name = 'Badges'; widgets = @(@{ type = 'BlockInstance'; name = 'Inst'; sourceBlock = 'Badge'; args = [ordered]@{ Mid = '"m"'; Alpha = '"a"'; Zed = '"z"' } }) },
    @{ name = 'Alpha'; actions = @(@{ name = 'Save'; locals = @(@{ name = 'Note'; type = 'Text' }); flow = @() }) }, @{ name = 'Beta'; actions = @(@{ name = 'Save'; flow = @() }) })
} | ConvertTo-Json -Depth 10
$r = Apply (Join-Path $Tmp 'PathsTest-blank.oml') $spec $m
Require ($r.saved -and !$r.newErrors) "the model refused names it should accept: $($r | ConvertTo-Json -Depth 6 -Compress)"

function Kind($path, $module = $m) { $d = Oml @('describe', $module, $path); Require ($d.Code -eq 0) "describe '$path' failed: $($d.Json.error)"; $d.Json }
# This module's own top-level element wins over a referenced and a nested one of the same name.
Require ((Kind 'Region').kind -eq 'Screen') 'own screen Region did not win over PathsProd.Region'
Require ((Kind 'GetThing').kind -eq 'Screen') 'own screen GetThing did not win over PathsProd.GetThing'
Require ((Kind 'PathsProd.Region').element -eq 'PathsProd.Region') 'Module.Name did not pick the reference'
Require ((Kind 'Thing').element -eq 'PathsProd.Thing') 'a referenced element did not win over a nested local'
Require ((Kind 'GetThing/Thing').kind -eq 'LocalVariable') 'Screen/Local did not resolve'
# Qualified forms.
Require ((Kind 'MainFlow/Sector').kind -eq 'Screen') 'Flow/Screen'
Require ((Kind 'Screen:Sector').kind -eq 'Screen') 'Kind:Name (Screen)'
Require ((Kind 'Entity:Sector').kind -eq 'ServerEntity') 'Kind:Name (Entity)'
Require ((Kind 'PathsTest.Admin/Users').element -eq 'PathsTest.Users') 'own Module.Flow/Screen'
Require ((Kind 'Admin/Users').element -eq 'PathsTest.Users') 'the rest of the path did not pick the flow Admin over the screen Admin'
$t = Oml @('tree', $m, 'PathsProd.Thing'); Require ($t.Code -eq 0 -and $t.Json.name -eq 'Thing') 'tree Module.Name'

# A path never falls through: the first group with a match decides, also for writes.
foreach ($case in @(@('Dashboard/Title', "no element 'Title' under 'Dashboard' \(Screen\)"), @('Region/Label', "no element 'Label' under 'Region' \(Screen\)"))) {
  $d = Oml @('describe', $m, $case[0])
  Require ($d.Code -eq 2 -and $d.Json.error -match $case[1]) "describe $($case[0]) fell through: $($d.Text)"
}
$edits = @(
  @('{"op":"set","element":"Dashboard/Title","properties":{"value":"\"changed\""}}', "no element 'Title' under 'Dashboard' \(Screen\)"),
  @('{"op":"delete","element":"Dashboard/Title"}', "no element 'Title' under 'Dashboard' \(Screen\)"),
  @('{"op":"set","element":"Region/Label","properties":{"description":"changed"}}', "no element 'Label' under 'Region' \(Screen\)"),
  @('{"op":"set","element":"PathsProd.Region/Label","properties":{"description":"changed"}}', 'referenced module; it cannot be edited here'),
  @('{"op":"set","element":"Thing","properties":{"description":"changed"}}', 'referenced module; it cannot be edited here'))
foreach ($case in $edits) {
  $out = Join-Path $Tmp 'paths-refused.oml'
  $r = Apply $m "{`"edits`":[$($case[0])]}" $out
  Require (!$r.saved -and !(Test-Path $out) -and ($r.problems -join ' ') -match $case[1]) "edit was not refused ($($case[0])): $($r | ConvertTo-Json -Depth 5 -Compress)"
}
# A change must name each part exactly: a part that matches several elements of its group is refused, even when only
# one of them holds the rest of the path. A read may let the rest of the path choose.
Require ((Kind 'Save/Note').kind -eq 'LocalVariable') 'a read of Save/Note did not resolve'
foreach ($case in @(@('Save/Note', "'Save' matches 2 elements.*Alpha/Save \(ScreenAction\), Beta/Save \(ScreenAction\)"), @('Admin/Users', "'Admin' matches 2 elements.*MobileFlow:Admin, Screen:Admin"))) {
  $r = Apply $m "{`"edits`":[{`"op`":`"set`",`"element`":`"$($case[0])`",`"properties`":{`"description`":`"x`"}}]}" (Join-Path $Tmp 'paths-refused.oml')
  Require (!$r.saved -and ($r.problems -join ' ') -match $case[1]) "write $($case[0]) was not refused with its candidates: $($r.problems -join '; ')"
}
foreach ($path in 'Alpha/Save/Note', 'MobileFlow:Admin/Users') {
  $r = Apply $m "{`"edits`":[{`"op`":`"set`",`"element`":`"$path`",`"properties`":{`"description`":`"x`"}}]}" (Join-Path $Tmp 'paths-exact.oml')
  Require ($r.saved -and !$r.problems) "write $path failed: $($r.problems -join '; ')"
}
# The full path changes exactly the nested element, and the screen of the same name stays as it was.
$edited = Join-Path $Tmp 'paths-home.oml'
$r = Apply $m '{"edits":[{"op":"set","element":"Home/Dashboard/Title","properties":{"value":"\"changed title\""}}]}' $edited
Require ($r.saved -and !$r.problems) "Home/Dashboard/Title: $($r.problems -join '; ')"
Require ((Kind 'Screen:Home' $edited).text -match 'Expression Title value="changed title"') 'the edit did not change Home/Dashboard/Title'
Require ((Kind 'Screen:Dashboard' $edited).text -match 'value="dashboard caption"' -and (Kind 'Screen:Dashboard' $edited).text -notmatch 'changed') 'the edit changed the screen Dashboard'
# replaceWidgets reads its "at" path by the same write rules: no fall-through, no referenced element, each part exact.
$widgets = '"widgets":[{"type":"Expression","name":"Fresh","value":"\"fresh\""}]'
foreach ($case in @(@('Dashboard/Title', "no element 'Title' under 'Dashboard' \(Screen\)"), @('Thing', 'referenced module; it cannot be edited here'),
    @('Save/Note', "'Save' matches 2 elements.*Alpha/Save \(ScreenAction\), Beta/Save \(ScreenAction\)"), @('Admin/Users', "'Admin' matches 2 elements.*MobileFlow:Admin, Screen:Admin"))) {
  $out = Join-Path $Tmp 'paths-refused.oml'
  $r = Apply $m "{`"edits`":[{`"op`":`"replaceWidgets`",`"at`":`"$($case[0])`",$widgets}]}" $out
  Require (!$r.saved -and !(Test-Path $out) -and ($r.problems -join ' ') -match $case[1]) "replaceWidgets at $($case[0]) was not refused: $($r | ConvertTo-Json -Depth 5 -Compress)"
}
$replaced = Join-Path $Tmp 'paths-replaced.oml'
$r = Apply $m "{`"edits`":[{`"op`":`"replaceWidgets`",`"at`":`"Home/Dashboard`",$widgets},{`"op`":`"replaceWidgets`",`"at`":`"MobileFlow:Admin/Users`",$widgets}]}" $replaced
Require ($r.saved -and !$r.problems) "replaceWidgets at exact paths: $($r.problems -join '; ')"
$homeText = (Kind 'Screen:Home' $replaced).text
Require ($homeText -match 'Container Dashboard' -and $homeText -match 'Expression Fresh' -and $homeText -notmatch 'home title') "replaceWidgets did not replace inside Home/Dashboard: $homeText"
Require ((Kind 'Screen:Dashboard' $replaced).text -match 'value="dashboard caption"') 'replaceWidgets at Home/Dashboard changed the screen Dashboard'
Require ((Kind 'MobileFlow:Admin/Users' $replaced).text -match 'Expression Fresh') 'replaceWidgets at MobileFlow:Admin/Users did not reach the screen Users'

# Ambiguous names: every candidate is listed with its kind and a path, and each path works in describe, tree and edits.
$renames = @(); $expected = @()
foreach ($name in 'Sector', 'Card', 'Admin', 'Shared', 'Menu') {
  $d = Oml @('describe', $m, $name)
  Require ($d.Code -eq 2 -and $d.Json.error -match "ambiguous path '$name': 2 elements match") "describe $name was not ambiguous: $($d.Text)"
  $listed = ($d.Json.error -split 'exact paths: ')[1] -split ', '
  Require ($listed.Count -eq 2) "describe $name listed $($listed.Count) candidates: $($d.Json.error)"
  $tree = Oml @('tree', $m, $name)
  Require ($tree.Code -eq 2 -and $tree.Json.error -eq $d.Json.error) "tree $name did not give the same error"
  $seen = @{}
  foreach ($item in $listed) {
    $path = $item -replace ' \([A-Za-z]+\)$', ''
    $kind = if ($item -match ' \(([A-Za-z]+)\)$') { $Matches[1] } else { ($path -split '/')[-1] -replace ':.*$', '' }
    $one = Kind $path
    Require ($one.kind -eq $kind) "listed path $path gave kind $($one.kind), expected $kind"
    $seen["$($one.element) $($one.kind)"] = 1
    $t = Oml @('tree', $m, $path); Require ($t.Code -eq 0 -and $t.Json.path -eq $path) "tree $path failed: $($t.Json.error)"
    # Locals and widgets of new screens may be renamed; the edit must reach exactly the listed element.
    if ($kind -eq 'LocalVariable') { $new = "$($path -replace '/.*$', '')Shared"; $renames += @{ op = 'rename'; element = $path; to = $new }; $expected += , @("$($path -replace '/.*$', '')/$new", $kind) }
  }
  Require ($seen.Count -eq 2) "the listed paths of $name did not name 2 different elements"
}
$renamed = Join-Path $Tmp 'paths-renamed.oml'
$r = Apply $m (@{ edits = $renames } | ConvertTo-Json -Depth 6) $renamed
Require ($r.saved -and !$r.problems) "renames on the listed paths failed: $($r.problems -join '; ')"
foreach ($e in $expected) { Require ((Kind $e[0] $renamed).kind -eq $e[1]) "rename did not reach $($e[0])" }

# find gives each match a path that describe accepts, and every element apply made resolves by it (one process).
$i = Oml @('inspect', $m, '--own')
$names = @($i.Json.entities.name) + @($i.Json.serverActions.name) + @($i.Json.screens.name) + @($i.Json.blocks.name) + @($i.Json.structures.name) | Sort-Object -Unique
$found = Run @($names | ForEach-Object { , @('find', $_, $m) })
$paths = @($found | ForEach-Object { $_.output.matches } | Where-Object source -eq 'this module' | ForEach-Object { Require ($_.path) "find $($_.name) gave no path"; $_.path })
$paths += @(@($i.Json.screens) + @($i.Json.blocks) | ForEach-Object { "$($_.flow)/$($_.name)" })
foreach ($d in Run @($paths | ForEach-Object { , @('describe', $m, $_) })) {
  Require ($d.exitCode -eq 0 -and $d.output.element -like 'PathsTest.*') "path $($d.args[2]) did not resolve: $($d.output | ConvertTo-Json -Depth 8 -Compress)"
}

# Index paths name the same element on every load: switch conditions in evaluation order, arguments in parameter order.
$s = Oml @('tree', $m, 'Classify/S')
$conditions = @($s.Json.children | Where-Object kind -match 'Condition')
Require ($conditions.Count -eq 4) "the switch has $($conditions.Count) conditions in tree"
$ci = Oml @('tree', $m, 'Badges/Inst')
$arguments = @($ci.Json.children | Where-Object kind -eq 'Argument')
Require ($arguments.Count -eq 3) "the block instance has $($arguments.Count) arguments in tree"
$first = $null
foreach ($load in 1..4) {
  $text = (Oml @('tree', $m, 'Classify')).Text + (Oml @('tree', $m, 'Badges')).Text
  if ($null -eq $first) { $first = $text } else { Require ($text -eq $first) "tree changed between loads ($load)" }
  $steps = @(0..3 | ForEach-Object { , @('tree', $m, $conditions[$_].path) }) + @(0..2 | ForEach-Object { , @('tree', $m, $arguments[$_].path) })
  $got = @(Run $steps | ForEach-Object { ($_.output | ConvertTo-Json -Depth 20 -Compress) })
  foreach ($k in 0..3) { Require ($got[$k] -match [regex]::Escape(@('N = 1', 'N = 2', 'N = 3', 'N > 0')[$k])) "$($conditions[$k].path) is not condition $k on load $load" }
  # Parameter order (Zed, Alpha, Mid), not name order and not the order the spec passed them.
  foreach ($k in 0..2) { Require ($got[4 + $k] -match ('\\"' + @('z', 'a', 'm')[$k] + '\\"')) "$($arguments[$k].path) is not argument $k on load $load" }
}
# inspect lists every section by name, the same on every load (the model's own lists change order between loads).
$first = & $Oml inspect $m; $second = & $Oml inspect $m
Require (($first -join "`n") -eq ($second -join "`n")) 'inspect of the same module differs between two loads'
$i = ($first -join "`n") | ConvertFrom-Json
foreach ($section in 'entities', 'serverActions', 'screens', 'blocks', 'references') {
  $names = [string[]]@($i.$section | ForEach-Object { if ($section -eq 'references') { $_.module } else { $_.name } })
  $sorted = [string[]]$names.Clone(); [Array]::Sort($sorted, [StringComparer]::OrdinalIgnoreCase)
  Require (($section -notin 'screens', 'blocks' -or $names.Count -gt 2) -and ($names -join '|') -ceq ($sorted -join '|')) "inspect $section is not sorted by name: $($names -join ', ')"
}
Write-Host 'ok   element paths: top-level wins, no fall-through, no edits of references, ambiguous candidates work everywhere, stable index paths'
