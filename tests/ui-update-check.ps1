# Updating existing UI with no rebuild. A listed existing screen, block, theme or email takes a different styleSheet
# (and a screen a text title and more requiredScripts), and the report lists it in "changed". replaceWidgets replaces
# the widgets inside an element and is refused when a removed widget is still used. {"file": ...} spec values read
# code from files next to the spec, byte for byte. This file is ASCII only: a job's console code page can change
# non-ASCII text in the CLI output, so the self-test checks real non-ASCII text in process.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $Tmp ('ui-update-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir -Force | Out-Null
function Require($condition, $message) { if (-not $condition) { throw "ui-update: $message" } }
$n = 0
function Apply-Spec($source, $json) {
  $script:n++
  $spec = Join-Path $dir "s$script:n.json"; $out = Join-Path $dir "o$script:n.oml"
  Set-Content -LiteralPath $spec -Value $json -Encoding utf8
  $r = & $Oml apply $source $spec $out 2>$null | ConvertFrom-Json
  [pscustomobject]@{ Exit = $LASTEXITCODE; Report = $r; Out = $out; Problems = (@($r.problems) -join "`n"); Text = ($r | ConvertTo-Json -Depth 8 -Compress) }
}
# Saved, nothing created but the edits, and exactly these entries in "changed".
function Changed($source, $json, [string[]]$changed, $what) {
  $r = Apply-Spec $source $json
  Require ($r.Exit -eq 0 -and $r.Report.saved) "$what was refused: $($r.Text)"
  Require (-not (@($r.Report.created) | Where-Object { $_ -notlike 'edit*' })) "$what created something: $($r.Text)"
  Require ((@($r.Report.changed) -join '|') -ceq ($changed -join '|')) "$what`: changed is '$(@($r.Report.changed) -join '|')', expected '$($changed -join '|')'"
  $r
}
function Refused($source, $json, [string[]]$texts, $what) {
  $r = Apply-Spec $source $json
  Require ($r.Exit -eq 1 -and -not $r.Report.saved) "$what was not refused: $($r.Text)"
  foreach ($t in $texts) { Require ($r.Problems.Contains($t)) "$what`: no problem with '$t': $($r.Problems)" }
}
function Css($module, $element) {
  $html = Join-Path $dir "preview-$([guid]::NewGuid().ToString('N')).html"
  $null = & $Oml preview $module $element --out $html
  Require ($LASTEXITCODE -eq 0) "preview $element failed"
  Get-Content -Raw $html
}
function Text($module, $element) { (& $Oml describe $module $element | ConvertFrom-Json).text }

$lib = Join-Path $dir 'lib.js'; Set-Content -LiteralPath $lib -Value 'window.uiLib = 1;' -Encoding utf8
$base = @"
{
  "scripts": [{ "name": "Lib", "file": $(ConvertTo-Json $lib) }],
  "themes": [{ "name": "AppTheme", "styleSheet": ".t { color: red; }" }, { "name": "OtherTheme", "styleSheet": ".o { }" }],
  "blocks": [{ "name": "Shell", "widgets": [{ "type": "Placeholder", "name": "Main" }] },
    { "name": "Card", "styleSheet": ".card { color: red; }", "widgets": [{ "type": "Container", "name": "CardRoot", "children": [{ "type": "Text", "text": "Card one" }] }] }],
  "screens": [{ "name": "Home", "title": "\"Home\"", "styleSheet": ".home { margin: 0; }", "locals": [{ "name": "Name", "type": "Text" }, { "name": "Show", "type": "Boolean" }],
      "actions": [{ "name": "Save", "flow": [{ "kind": "assign", "name": "Mark", "assign": { "NameInput.Valid": "False" } }] }],
      "widgets": [
        { "type": "Form", "name": "EditForm", "children": [
          { "type": "Label", "children": [{ "type": "Text", "text": "Old label" }] },
          { "type": "Input", "name": "NameInput", "variable": "Name" },
          { "type": "Button", "onClick": "Save", "children": [{ "type": "Text", "text": "Save" }] }] },
        { "type": "If", "name": "Cond", "condition": "Show", "trueBranch": [{ "type": "Text", "text": "Shown" }] },
        { "type": "Container", "name": "Contact", "children": [{ "type": "Input", "name": "EmailInput", "variable": "Name" }] },
        { "type": "Container", "name": "LabelBox", "children": [
          { "type": "Label", "name": "InnerLabel", "targetWidget": "InnerInput", "children": [{ "type": "Text", "text": "Inner" }] },
          { "type": "Input", "name": "InnerInput", "variable": "Name" }] },
        { "type": "Label", "name": "EmailLabel", "targetWidget": "EmailInput", "children": [{ "type": "Text", "text": "Email" }] }] },
    { "name": "Page", "layout": "Shell", "placeholders": { "Main": [{ "type": "Text", "text": "Page one" }] } }],
  "emails": [{ "name": "Notice", "subject": "\"Hi\"", "styleSheet": ".mail { color: red; }", "widgets": [{ "type": "Text", "text": "Hello" }] }]
}
"@
$blank = Join-Path $dir 'blank.oml'
& $Oml new $blank --kind CrossDevice | Out-Null
$built = Apply-Spec $blank $base
Require ($built.Exit -eq 0 -and $built.Report.saved -and -not $built.Report.changed) "base did not save: $($built.Text)"
$m = $built.Out

# A block's CSS changed three times in a row, each on the last result: no rebuild, and the preview shows the new CSS.
foreach ($color in 'blue', 'green', 'black') {
  $m = (Changed $m "{ `"blocks`": [{ `"name`": `"Card`", `"styleSheet`": `".card { color: $color; }`" }] }" @('styleSheet of block Card') "block CSS $color").Out
  Require ((Css $m Card).Contains(".card { color: $color; }")) "block CSS $color is not in the module"
}
# The same CSS written differently (spaces, a comment) changes nothing.
$null = Changed $m '{ "blocks": [{ "name": "Card", "styleSheet": "/* c */ .card{color:black;}" }] }' @() 'same block CSS'
# Screen: styleSheet, a text title and a required script.
$s = Changed $m '{ "screens": [{ "name": "Home", "styleSheet": ".home { margin: 4px; }", "title": "\"Start\"", "requiredScripts": ["Lib"] }] }' @('styleSheet of screen Home', 'title of screen Home', 'requiredScripts of screen Home') 'screen styleSheet, title, requiredScripts'
Require ((Css $s.Out Home).Contains('.home { margin: 4px; }')) 'screen CSS is not in the module'
$null = Changed $s.Out '{ "screens": [{ "name": "Home", "styleSheet": ".home { margin: 4px; }", "title": "\"Start\"", "requiredScripts": ["Lib"] }] }' @() 'same screen settings again'
# Theme and email.
$t = Changed $m '{ "themes": [{ "name": "AppTheme", "styleSheet": ".t { color: blue; }" }], "emails": [{ "name": "Notice", "subject": "\"Hi\"", "styleSheet": ".mail { color: blue; }" }] }' @('styleSheet of theme AppTheme', 'styleSheet of email Notice') 'theme and email CSS'
Require (@((& $Oml inspect $t.Out | ConvertFrom-Json).content.themes | Where-Object name -eq 'AppTheme').styleSheet -eq '.t { color: blue; }') 'theme CSS is not in the module'
Require ((Css $t.Out Notice).Contains('.mail { color: blue; }')) 'email CSS is not in the module'
# A module has no style sheet of its own: the refusal names themes.
Refused $m '{ "module": { "styleSheet": ".x { }" } }' @('module.styleSheet', '"themes"') 'module styleSheet'
# describe --css prints whole style sheets. A styleSheet replaces the whole sheet, so a new one that loses more than
# half of the old one or drops its selectors is warned about, also through edits.set; a bigger one is not.
$bigCss = '.home { margin: 0; } .home-title { font-size: 20px; color: navy; } .home-list { padding: 8px 12px; border: 1px solid #ccc; }'
$big = Changed $m "{ `"screens`": [{ `"name`": `"Home`", `"styleSheet`": `"$bigCss`" }] }" @('styleSheet of screen Home') 'a bigger screen sheet'
Require (-not ((@($big.Report.warnings) -join ' ').Contains('replaced:'))) "a bigger sheet was warned about: $($big.Text)"
$one = & $Oml describe $big.Out Home --css | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $one.element -eq 'Home' -and $one.kind -eq 'Screen' -and $one.styleSheet -ceq $bigCss -and -not $one.text) "describe --css: $($one | ConvertTo-Json -Compress)"
$many = @(& $Oml describe $big.Out Home Card AppTheme --css | ConvertFrom-Json)
Require ($many.Count -eq 3 -and $many[1].styleSheet -eq '.card { color: black; }' -and $many[2].styleSheet -eq '.t { color: red; }') "describe --css on several elements: $($many | ConvertTo-Json -Compress)"
$noCss = & $Oml describe $big.Out Home/Save --css 2>&1 | Out-String
Require ($LASTEXITCODE -ne 0 -and $noCss.Contains('has no style sheet')) "describe --css on an action: $noCss"
$noCss = & $Oml describe $big.Out Home --css --depth 1 2>&1 | Out-String
Require ($LASTEXITCODE -eq 2 -and $noCss.Contains('takes no --depth')) "describe --css --depth: $noCss"
$small = Changed $big.Out '{ "screens": [{ "name": "Home", "styleSheet": ".home { margin: 2px; }" }] }' @('styleSheet of screen Home') 'a much smaller screen sheet'
$sw = @($small.Report.warnings) -join ' '
Require ($sw.Contains("styleSheet of screen Home replaced: $($bigCss.Length) -> 22 characters, 2 selectors gone (") -and $sw.Contains('.home-title') -and $sw.Contains('.home-list')`
  -and $sw.Contains('the whole sheet is replaced, not merged') -and $sw.Contains("oml describe <module> Home --css")) "no warning for a much smaller sheet: $sw"
$renamed = Changed $big.Out "{ `"screens`": [{ `"name`": `"Home`", `"styleSheet`": `"$($bigCss.Replace('.home-title', '.home-heading'))`" }] }" @('styleSheet of screen Home') 'a sheet with a renamed selector'
Require ((@($renamed.Report.warnings) -join ' ').Contains('1 selector gone (.home-title)')) "no warning for a dropped selector: $($renamed.Text)"
$cleared = Apply-Spec $big.Out '{ "edits": [{ "op": "set", "element": "Card", "properties": { "styleSheet": "" } }] }'
Require ($cleared.Report.saved -and (@($cleared.Report.warnings) -join ' ').Contains('styleSheet of block Card replaced: 23 -> 0 characters, 1 selector gone (.card)')) "no warning for a sheet cleared by set: $($cleared.Text)"

# oml tree: --depth N levels under the element, and at most 250 parts with a note on how to narrow it.
$d1 = & $Oml tree $m Home --depth 1 | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and @($d1.children).Count -gt 0 -and -not @($d1.children | Where-Object { $_.children }) -and @($d1.children | Where-Object { $_.more -gt 0 }).Count -gt 0 -and -not $d1.truncated) "tree --depth 1: $($d1 | ConvertTo-Json -Depth 4 -Compress)"
$bad = & $Oml tree $m Home --depth 0 2>&1 | Out-String
Require ($LASTEXITCODE -eq 2 -and $bad.Contains('--depth N (1 or more)')) "tree --depth 0: $bad"
$rows = (1..300 | ForEach-Object { "{ `"type`": `"Container`", `"children`": [{ `"type`": `"Text`", `"text`": `"Row $_`" }] }" }) -join ','
$wide = Apply-Spec $m "{ `"screens`": [{ `"name`": `"Wide`", `"widgets`": [$rows] }] }"
Require ($wide.Report.saved) "wide screen: $($wide.Text)"
$wt = & $Oml tree $wide.Out Wide
$wj = $wt | ConvertFrom-Json
function Parts($n) { 1 + (@($n.children) | Where-Object { $_ } | ForEach-Object { Parts $_ } | Measure-Object -Sum).Sum }
Require ($LASTEXITCODE -eq 0 -and $wj.truncated -like 'Cut at 250 parts*oml tree <module.oml> Wide/<path> --depth 2.' -and (Parts $wj) -le 251) "tree of a wide screen: $((Parts $wj)) parts, truncated '$($wj.truncated)'"
$part = & $Oml tree $wide.Out 'Wide/Container[299]' --depth 2 | ConvertFrom-Json
Require ($part.path -eq 'Wide/Container[299]' -and -not $part.truncated -and @($part.children).Count -ge 1) "tree of one part: $($part | ConvertTo-Json -Depth 4 -Compress)"

# Restated widgets are not applied; the warning names the op that changes them.
$w = Changed $m '{ "screens": [{ "name": "Home", "widgets": [{ "type": "Text", "text": "x" }] }, { "name": "Page", "placeholders": { "Main": [] } }] }' @() 'restated widgets'
$warn = @($w.Report.warnings) -join ' '
Require ($warn.Contains('screen Home exists: its widgets') -and $warn.Contains('replaceWidgets with "at": "Home"') -and $warn.Contains('screen Page exists: its placeholders')) "warnings do not name replaceWidgets: $warn"

# replaceWidgets: the form's widgets, keeping NameInput, which the Save action uses.
$form = '{ "edits": [{ "op": "replaceWidgets", "at": "Home/EditForm", "widgets": [
  { "type": "Container", "style": "\"form-row\"", "children": [
    { "type": "Label", "children": [{ "type": "Text", "text": "New label" }] },
    { "type": "Input", "name": "NameInput", "variable": "Name" }] },
  { "type": "Button", "onClick": "Save", "children": [{ "type": "Text", "text": "Save now" }] }] }] }'
$f = Changed $m $form @('widgets of Home/EditForm') 'replaceWidgets on a form'
$homeText = Text $f.Out Home
Require ($homeText.Contains('New label') -and $homeText.Contains('Save now') -and -not $homeText.Contains('Old label')) "form widgets not replaced: $homeText"
# Applied again on its result: the same widgets again.
$again = Changed $f.Out $form @('widgets of Home/EditForm') 'replaceWidgets again'
Require ((Text $again.Out Home) -ceq $homeText) 'replaceWidgets again gave other widgets'
# Removing NameInput is refused, naming the widget and where it is used; nothing is saved.
Refused $m '{ "edits": [{ "op": "replaceWidgets", "at": "Home/EditForm", "widgets": [{ "type": "Text", "text": "Gone" }] }] }' @("refused: replaceWidgets at 'Home/EditForm'", 'NameInput (used by MainFlow/Home/Save/Mark)', 'same name') 'replaceWidgets removing a used widget'
# A usage in an unnamed widget: the path in the refusal is one that tree and edits read, and it names that widget.
$u = Apply-Spec $m '{ "screens": [{ "name": "Home2", "locals": [{ "name": "N", "type": "Text" }], "widgets": [
  { "type": "Container", "children": [{ "type": "Expression", "value": "\"shallow\"" }] },
  { "type": "Container", "children": [{ "type": "Container", "children": [{ "type": "Expression", "value": "NameInput2.Valid" }] }] },
  { "type": "Container", "name": "Form2", "children": [{ "type": "Input", "name": "NameInput2", "variable": "N" }] }] }] }'
Require ($u.Exit -eq 0 -and $u.Report.saved) "unnamed-widget base: $($u.Text)"
$used = 'Home2/Container[1]/Container[0]/Expression[0]'
Refused $u.Out '{ "edits": [{ "op": "replaceWidgets", "at": "Home2/Form2", "widgets": [{ "type": "Text", "text": "t" }] }] }' @("NameInput2 (used by $used)") 'replaceWidgets with a usage in an unnamed widget'
$tree = & $Oml tree $u.Out $used | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $tree.kind -eq 'Expression' -and (($tree | ConvertTo-Json -Depth 6) -match 'NameInput2\.Valid')) "the usage path $used does not name the widget that uses NameInput2"
# A whole block, a layout placeholder and an If branch.
$b = Changed $m '{ "edits": [{ "op": "replaceWidgets", "at": "Card", "widgets": [{ "type": "Text", "text": "Card two" }] }] }' @('widgets of Card') 'replaceWidgets on a block'
Require ((Text $b.Out Card) -match 'Card two' -and (Text $b.Out Card) -notmatch 'CardRoot') 'block widgets not replaced'
$p = Changed $m '{ "edits": [{ "op": "replaceWidgets", "at": "Page/Main", "widgets": [{ "type": "Text", "text": "Page two" }] }] }' @('widgets of Page/Main') 'replaceWidgets on a placeholder'
Require ((Text $p.Out Page) -match 'Page two' -and (Text $p.Out Page) -notmatch 'Page one') 'placeholder widgets not replaced'
$i = Changed $m '{ "edits": [{ "op": "replaceWidgets", "at": "Home/Cond", "area": "trueBranch", "widgets": [{ "type": "Text", "text": "Now shown" }] }] }' @('widgets of Home/Cond') 'replaceWidgets on an If branch'
Require ((Text $i.Out Home) -match 'Now shown' -and (Text $i.Out Home) -notmatch '"Shown"|value=Shown') 'If branch not replaced'
Refused $m '{ "edits": [{ "op": "replaceWidgets", "at": "Home/Save", "widgets": [{ "type": "Text", "text": "x" }] }] }' @("'Home/Save' holds no widgets") 'replaceWidgets on a screen action'
Set-Content (Join-Path $dir 'missing-widgets.json') '{ "edits": [{ "op": "replaceWidgets", "at": "Home" }] }'
$sc = & $Oml spec check (Join-Path $dir 'missing-widgets.json') | ConvertFrom-Json
Require (-not $sc.ok -and (@($sc.problems.message) -join ' ') -match "missing required key 'widgets' for op 'replaceWidgets'") "spec check of replaceWidgets: $($sc | ConvertTo-Json -Compress)"
# A used name given to another kind of widget is refused: NameInput.Valid would mean something else.
Refused $m '{ "edits": [{ "op": "replaceWidgets", "at": "Home/EditForm", "widgets": [{ "type": "Container", "name": "NameInput" }] }] }' @('NameInput was an Input, now a Container; used by MainFlow/Home/Save/Mark', 'rename the widget or update the usages in the same spec') 'replaceWidgets with another kind under a used name'
# A Label's target widget is a reference to the widget itself: a new widget of the same name does not take it over.
Refused $m '{ "edits": [{ "op": "replaceWidgets", "at": "Home/Contact", "widgets": [{ "type": "Input", "name": "EmailInput", "variable": "Name" }] }] }' @('EmailInput is the target of MainFlow/Home/EmailLabel', '"targetWidget"') 'replaceWidgets removing a Label target'
# set targetWidget takes a widget name; then the old target can go. Two replaceWidgets on one element: one "changed" entry.
$null = Changed $m '{ "edits": [{ "op": "set", "element": "Home/EmailLabel", "properties": { "targetWidget": "NameInput" } },
  { "op": "replaceWidgets", "at": "Home/Contact", "widgets": [{ "type": "Text", "text": "No email" }] },
  { "op": "replaceWidgets", "at": "Home/Contact", "widgets": [{ "type": "Text", "text": "Still no email" }] }] }' @('widgets of Home/Contact') 'set targetWidget, then replaceWidgets twice'
Refused $m '{ "edits": [{ "op": "set", "element": "Home/EmailLabel", "properties": { "targetWidget": "NoSuchInput" } }] }' @("no widget named 'NoSuchInput'") 'targetWidget naming no widget'
# describe and tree show a Label's target; an empty targetWidget clears it, and then the old target can go.
Require ((Text $m Home).Contains('Label EmailLabel targetWidget=EmailInput') -and ((& $Oml tree $m Home) -join '').Contains('"targetWidget":"EmailInput"')) 'describe or tree does not show targetWidget'
$cl = Changed $m '{ "edits": [{ "op": "set", "element": "Home/EmailLabel", "properties": { "targetWidget": "" } },
  { "op": "replaceWidgets", "at": "Home/Contact", "widgets": [{ "type": "Text", "text": "No email" }] }] }' @('widgets of Home/Contact') 'clear targetWidget, then replaceWidgets'
Require (-not (Text $cl.Out Home).Contains('targetWidget=EmailInput')) 'an empty targetWidget did not clear the target'
# Replacing a part that holds a Label and its input: a warning when no new Label restores the link, none when one does.
$lw = Changed $m '{ "edits": [{ "op": "replaceWidgets", "at": "Home/LabelBox", "widgets": [{ "type": "Input", "name": "InnerInput", "variable": "Name" }] }] }' @('widgets of Home/LabelBox') 'replaceWidgets dropping a Label link'
Require ((@($lw.Report.warnings) -join ' ').Contains('removed the Label InnerLabel, which pointed at InnerInput')) "no warning for a lost Label link: $($lw.Text)"
$lk = Changed $m '{ "edits": [{ "op": "replaceWidgets", "at": "Home/LabelBox", "widgets": [{ "type": "Label", "targetWidget": "InnerInput", "children": [{ "type": "Text", "text": "Inner" }] },
  { "type": "Input", "name": "InnerInput", "variable": "Name" }] }] }' @('widgets of Home/LabelBox') 'replaceWidgets keeping a Label link'
Require (-not (@($lk.Report.warnings) -join ' ').Contains('removed the Label') -and (Text $lk.Out Home).Contains('targetWidget=InnerInput')) "a restored Label link was warned about or not set: $($lk.Text)"
# A theme listed without styleSheet keeps its CSS; a baseTheme is applied; a bare path that is no file is refused.
$th = Changed $m '{ "themes": [{ "name": "AppTheme" }, { "name": "OtherTheme", "baseTheme": "AppTheme" }] }' @('baseTheme of theme OtherTheme') 'theme without styleSheet, with baseTheme'
$themes = (& $Oml inspect $th.Out | ConvertFrom-Json).content.themes
Require (@($themes | Where-Object name -eq 'AppTheme').styleSheet -eq '.t { color: red; }' -and @($themes | Where-Object name -eq 'OtherTheme').baseTheme -eq 'AppTheme') "theme CSS lost or baseTheme not set: $($themes | ConvertTo-Json -Compress)"
$null = Changed $th.Out '{ "themes": [{ "name": "OtherTheme", "baseTheme": "AppTheme" }] }' @() 'same baseTheme again'
Set-Content (Join-Path $dir 'theme-only.json') '{ "themes": [{ "name": "AppTheme" }] }'
Require ((& $Oml spec check (Join-Path $dir 'theme-only.json') | ConvertFrom-Json).ok) 'spec check refuses a theme without styleSheet'
Refused $m '{ "themes": [{ "name": "AppTheme", "styleSheet": "styles/app.css" }] }' @('looks like a file path', '"file"') 'theme path that is not a file'
# CSS compare: spaces around a colon in a declaration do not count; spaces inside a string do.
$null = Changed $m '{ "screens": [{ "name": "Home", "styleSheet": ".home{margin : 0 ;}" }] }' @() 'same screen CSS, space before a colon'
$q = Changed $m '{ "screens": [{ "name": "Home", "styleSheet": ".home { content: \"a   b\"; }" }] }' @('styleSheet of screen Home') 'screen CSS with a string'
$null = Changed $q.Out '{ "screens": [{ "name": "Home", "styleSheet": ".home { content: \"a b\"; }" }] }' @('styleSheet of screen Home') 'screen CSS, fewer spaces inside a string'
# In a selector a space before a colon matters ("div :hover" is a descendant), also inside @media and & nesting.
$md = Changed $m '{ "blocks": [{ "name": "Card", "styleSheet": "@media (min-width: 600px) { .card :hover { color: red; } } .card { & :focus { color: red; } }" }] }' @('styleSheet of block Card') 'block CSS with @media and nesting'
$null = Changed $md.Out '{ "blocks": [{ "name": "Card", "styleSheet": "@media (min-width: 600px) { .card :hover { color : red ; } } .card { & :focus { color:red; } }" }] }' @() 'same nested CSS, spaces around declaration colons'
$md2 = Changed $md.Out '{ "blocks": [{ "name": "Card", "styleSheet": "@media (min-width: 600px) { .card:hover { color: red; } } .card { & :focus { color: red; } }" }] }' @('styleSheet of block Card') 'descendant selector in @media changed'
$null = Changed $md2.Out '{ "blocks": [{ "name": "Card", "styleSheet": "@media (min-width: 600px) { .card:hover { color: red; } } .card { &:focus { color: red; } }" }] }' @('styleSheet of block Card') 'descendant selector in & nesting changed'

# The base spec applied again on its own result changes and creates nothing. On an updated module it changes only
# what it restates (the block CSS, back to red) and keeps the replaced widgets.
$re = Apply-Spec $built.Out $base
Require ($re.Exit -eq 0 -and $re.Report.saved -and -not $re.Report.created -and -not $re.Report.changed) "re-apply: $($re.Text)"
$re = Changed $f.Out $base @('styleSheet of block Card') 're-apply after replaceWidgets'
Require ((Text $re.Out Home) -ceq $homeText) 're-apply changed the replaced widgets'

# {"file": ...} values, relative to the spec file.
$specDir = Join-Path $dir 'spec'; New-Item -ItemType Directory (Join-Path $specDir 'code') -Force | Out-Null
# The JavaScript keeps the 6 ASCII characters of the escape for e-acute (backslash u00e9), not the letter itself: a JSON
# value would turn that escape into the letter, a file value must not. The self-test checks real non-ASCII text.
$js = "const Card = ({ title }) => {`n  const label = ``Card: `${title} \u00e9 \`"q\`" 'single' \\ \n``;`n`n  return React.createElement(`"div`", { className: `"card`" }, label.replace(/\s+/g, `" `"));`n};`n"
[IO.File]::WriteAllText((Join-Path $specDir 'code/app.js'), $js, [Text.UTF8Encoding]::new($true)) # with a BOM
[IO.File]::WriteAllText((Join-Path $specDir 'code/home.css'), ".home { content: `"\201C`"; }`n")
[IO.File]::WriteAllText((Join-Path $specDir 'code/count.txt'), 'Length(Name) + 1')
$fileSpec = Join-Path $specDir 'files.json'
Set-Content -LiteralPath $fileSpec -Encoding utf8 -Value '{ "module": { "javaScript": { "file": "code/app.js" } },
  "screens": [{ "name": "Files", "styleSheet": { "file": "code/home.css" }, "locals": [{ "name": "Name", "type": "Text" }],
    "widgets": [{ "type": "Expression", "value": { "file": "code/../code/count.txt" } }] }] }'
$check = & $Oml spec check $fileSpec | ConvertFrom-Json
Require ($check.ok) "spec check of file values: $($check | ConvertTo-Json -Compress -Depth 10)"
$fo = Join-Path $dir 'files.oml'
$fr = & $Oml apply $blank $fileSpec $fo | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $fr.saved) "apply with file values: $($fr | ConvertTo-Json -Compress -Depth 10)"
Require ((& $Oml inspect $fo | ConvertFrom-Json).module.javaScript -ceq $js) 'module JavaScript from a file is not byte-identical'
Require ((Css $fo Files).Contains(".home { content: `"\201C`"; }")) 'styleSheet from a file not stored'
Require ((Text $fo Files) -match 'Length\(Name\) \+ 1') 'expression from a file not stored'
# SQL from a file: stored, and the production rule still refuses data-changing SQL.
[IO.File]::WriteAllText((Join-Path $specDir 'code/top.sql'), "SELECT {Customer}.*`nFROM {Customer}`nWHERE {Customer}.[Name] LIKE 'a\%'")
[IO.File]::WriteAllText((Join-Path $specDir 'code/wipe.sql'), 'DELETE FROM {Customer}')
foreach ($sql in 'top', 'wipe') {
  Set-Content -LiteralPath (Join-Path $specDir "$sql.json") -Encoding utf8 -Value ('{ "entities": [{ "name": "Customer", "attributes": [{ "name": "Name" }] }],
    "serverActions": [{ "name": "Find", "flow": [{ "kind": "sql", "name": "Query", "sql": { "file": "code/' + $sql + '.sql" }, "outputs": ["Customer"] }] }] }')
}
$so = Join-Path $dir 'sql.oml'
$sr = & $Oml apply $blank (Join-Path $specDir 'top.json') $so | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $sr.saved) "apply with a SQL file: $($sr | ConvertTo-Json -Compress -Depth 10)"
$sqlText = Text $so Find # describe joins the lines with spaces
Require ($sqlText.Contains("FROM {Customer} WHERE {Customer}.[Name] LIKE 'a\%'")) "SQL from a file not stored: $sqlText"
$sw = & $Oml apply $blank (Join-Path $specDir 'wipe.json') (Join-Path $dir 'wipe.oml') | ConvertFrom-Json
Require (-not $sw.saved -and (@($sw.problems) -join ' ').Contains('SQL contains DELETE')) "a DELETE from a SQL file was not refused: $($sw | ConvertTo-Json -Compress -Depth 10)"
# UTF-16 with a BOM is read; a note key (_ or $) is not resolved; a script file path is relative to the spec's folder
# first (the working folder of this check is the repository root, which has no code/lib2.js).
[IO.File]::WriteAllText((Join-Path $specDir 'code/u16.js'), 'var u16 = "\\";', [Text.UnicodeEncoding]::new($false, $true))
[IO.File]::WriteAllText((Join-Path $specDir 'code/lib2.js'), 'window.lib2 = 1;')
$u16Spec = Join-Path $specDir 'u16.json'
Set-Content -LiteralPath $u16Spec -Encoding utf8 -Value '{ "_note": { "file": "code/none.js" }, "module": { "javaScript": { "file": "code/u16.js" } }, "scripts": [{ "name": "Lib2", "file": "code/lib2.js" }] }'
$uo = Join-Path $dir 'u16.oml'
$ur = & $Oml apply $blank $u16Spec $uo | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $ur.saved) "apply with a UTF-16 file and a spec-relative script: $($ur | ConvertTo-Json -Compress -Depth 10)"
$ui = & $Oml inspect $uo | ConvertFrom-Json
Require ($ui.module.javaScript -ceq 'var u16 = "\\";' -and @($ui.content.scripts | Where-Object name -eq 'Lib2').javaScript -eq 'window.lib2 = 1;') "UTF-16 file or spec-relative script not stored: $($ui.module.javaScript)"
# A file with a NUL character, a file over 2 MB, a device name and a junction inside the spec's folder are refused.
[IO.File]::WriteAllText((Join-Path $specDir 'code/zero.js'), "a`0b")
[IO.File]::WriteAllText((Join-Path $specDir 'code/big.css'), ('a' * (2MB + 1)))
$outside = Join-Path $dir 'outside'; New-Item -ItemType Directory $outside -Force | Out-Null
Set-Content (Join-Path $outside 'secret.js') 'secret'
New-Item -ItemType Junction -Path (Join-Path $specDir 'code/jn') -Target $outside | Out-Null
# Missing files, a path outside the spec's folder, an absolute path and a file value on another key.
foreach ($case in @(
    @('{ "module": { "javaScript": { "file": "code/zero.js" } } }', 'NUL character'),
    @('{ "module": { "javaScript": { "file": "code/big.css" } } }', 'larger than 2 MB'),
    @('{ "module": { "javaScript": { "file": "code/NUL" } } }', 'Windows device name'),
    @('{ "module": { "javaScript": { "file": "code/jn/secret.js" } } }', 'is a link'),
    @('{ "module": { "javaScript": { "file": "code/none.js" } } }', 'file not found'),
    @('{ "module": { "javaScript": { "file": "../lib.js" } } }', "outside the spec's folder"),
    @("{ `"module`": { `"javaScript`": { `"file`": $(ConvertTo-Json $lib) } } }", 'must be a path relative'),
    @('{ "module": { "description": { "file": "code/app.js" } } }', 'only for javaScript, styleSheet, sql, expression, value'))) {
  $bad = Join-Path $specDir 'bad.json'; Set-Content -LiteralPath $bad -Value $case[0] -Encoding utf8
  $c = & $Oml spec check $bad | ConvertFrom-Json
  Require (-not $c.ok -and (@($c.problems.message) -join ' ').Contains($case[1])) "spec check '$($case[1])': $($c | ConvertTo-Json -Compress -Depth 10)"
  $out = Join-Path $dir "bad-$([guid]::NewGuid().ToString('N')).oml"
  $a = & $Oml apply $blank $bad $out | ConvertFrom-Json
  Require ($LASTEXITCODE -eq 1 -and -not $a.saved -and $a.phase -eq 'spec' -and (@($a.problems) -join ' ').Contains($case[1]) -and -not (Test-Path $out)) "apply '$($case[1])': $($a | ConvertTo-Json -Compress)"
}
# A plain path that names a file in both the spec's folder and the working folder: the spec's file, and a warning.
# copy "from" is relative to the spec's folder too.
New-Item -ItemType Directory (Join-Path $specDir 'assets'), (Join-Path $dir 'assets') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $specDir 'assets/lib.js'), 'window.fromSpec = 1;')
[IO.File]::WriteAllText((Join-Path $dir 'assets/lib.js'), 'window.fromWorkingFolder = 1;')
$copySource = Join-Path $specDir 'copy-source.oml'
Set-Content (Join-Path $dir 'copy-source.json') '{ "serverActions": [{ "name": "CopyMe" }] }'
$null = & $Oml apply $blank (Join-Path $dir 'copy-source.json') $copySource
$both = Join-Path $specDir 'both.json'
Set-Content -LiteralPath $both -Value '{ "scripts": [{ "name": "Lib3", "file": "assets/lib.js" }], "copy": [{ "from": "copy-source.oml", "elements": ["CopyMe"] }] }'
$bo = Join-Path $dir 'both.oml'
Push-Location $dir
try { $br = & $Oml apply $blank $both $bo | ConvertFrom-Json } finally { Pop-Location }
Require ($br.saved -and (@($br.warnings) -join ' ').Contains("assets/lib.js is in both the spec's folder and the working folder; using the spec's") -and (@($br.created) -join ' ').Contains('CopyMe')) "both folders or copy from the spec's folder: $($br | ConvertTo-Json -Compress -Depth 10)"
Require (@((& $Oml inspect $bo | ConvertFrom-Json).content.scripts | Where-Object name -eq 'Lib3').javaScript -eq 'window.fromSpec = 1;') 'the spec folder file was not used'
# Two specs in two folders: each reads its own code/x.* file.
foreach ($part in 'a', 'b') {
  New-Item -ItemType Directory (Join-Path $dir "$part/code") -Force | Out-Null
  [IO.File]::WriteAllText((Join-Path $dir "$part/code/x.txt"), "/* $part */ .$part { color: red; }")
}
Set-Content (Join-Path $dir 'a/a.json') '{ "screens": [{ "name": "PartA", "styleSheet": { "file": "code/x.txt" } }] }'
Set-Content (Join-Path $dir 'b/b.json') '{ "blocks": [{ "name": "PartB", "styleSheet": { "file": "code/x.txt" } }] }'
$mo = Join-Path $dir 'multi.oml'
$mr = & $Oml apply $blank (Join-Path $dir 'a/a.json') (Join-Path $dir 'b/b.json') $mo | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $mr.saved) "multi-spec apply with file values: $($mr | ConvertTo-Json -Compress -Depth 10)"
Require ((Css $mo PartA).Contains('/* a */ .a { color: red; }') -and (Css $mo PartB).Contains('/* b */ .b { color: red; }')) 'multi-spec: each spec must read its own file (comments in a file stay)'
# oml run: the step's spec reads its files relative to that spec, not to the steps file or the working folder.
$steps = Join-Path $dir 'steps.json'; $ro = Join-Path $dir 'run.oml'
Set-Content $steps (ConvertTo-Json @{ steps = @(, @('apply', $blank, $fileSpec, $ro)) } -Depth 4)
$run = & $Oml run $steps | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $run.results[0].output.saved) "oml run with file values: $($run | ConvertTo-Json -Compress -Depth 10)"
Require ((& $Oml inspect $ro | ConvertFrom-Json).module.javaScript -ceq $js) 'oml run: module JavaScript from a file is not byte-identical'
Write-Host 'ok   ui-update: styleSheet, title and requiredScripts on existing UI in "changed"; replaceWidgets with usage refusal; {"file"} values'
