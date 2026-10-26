# Checks inspect: parameter and input descriptions, --only aliases, and --in <folder> (one process, a broken file gives an error entry).
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($condition, $message) { if (-not $condition) { throw $message } }
$dir = Join-Path $Tmp ('inspect-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $dir | Out-Null
$blank = Join-Path $dir 'blank.oml'
& $Oml new $blank --name InspA --kind CrossDevice | Out-Null
$spec = @'
{ "serverActions": [ { "name": "Calc", "inputs": [ { "name": "Amount", "type": "Integer", "description": "The amount to add." }, { "name": "Plain", "type": "Text" } ],
    "outputs": [ { "name": "Total", "type": "Integer", "description": "Sum." } ], "flow": [ { "kind": "assign", "assign": { "Total": "Amount" } } ] } ],
  "screens": [ { "name": "Home", "inputs": [ { "name": "Who", "type": "Text", "description": "User to greet." }, { "name": "Other", "type": "Text" } ] } ] }
'@
$specPath = Join-Path $dir 'spec.json'; Set-Content $specPath $spec -Encoding utf8
$mod = Join-Path $dir 'InspA.oml'
$built = & $Oml apply $blank $specPath $mod | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $built.saved) 'inspect-check: fixture did not save'

# 1. Descriptions: only where not empty.
$r = & $Oml inspect $mod --own | ConvertFrom-Json
$calc = $r.serverActions | Where-Object name -eq 'Calc'
Require (($calc.inputs | Where-Object name -eq 'Amount').description -eq 'The amount to add.') 'input description'
Require ($null -eq ($calc.inputs | Where-Object name -eq 'Plain').PSObject.Properties['description']) 'empty description must be left out'
Require (($calc.outputs | Where-Object name -eq 'Total').description -eq 'Sum.') 'output description'
$homeScreen = $r.screens | Where-Object name -eq 'Home'
Require ($homeScreen.inputs -contains 'Who' -and $homeScreen.inputDescriptions.Who -eq 'User to greet.' -and $null -eq $homeScreen.inputDescriptions.PSObject.Properties['Other']) 'screen inputDescriptions'
# Session variables exist in Traditional Web modules only.
$webBlank = Join-Path $dir 'webblank.oml'; $web = Join-Path $dir 'InspW.oml'
& $Oml new $webBlank --name InspW --kind WebResponsive | Out-Null
$webSpec = Join-Path $dir 'web.json'; Set-Content $webSpec '{ "sessionVariables": [ { "name": "CartId", "type": "Integer" } ] }' -Encoding utf8
Require ((& $Oml apply $webBlank $webSpec $web | ConvertFrom-Json).saved) 'inspect-check: web fixture did not save'
$w = & $Oml inspect $web --own --only sessionVariables | ConvertFrom-Json
$pretty = & $Oml inspect $mod --pretty | Out-String
Require ($pretty -notlike '*sessionVariables*') 'a module without session variables must keep its output (also --pretty)'
Require (($w.sessionVariables | Where-Object name -eq 'CartId').type -eq 'Integer') 'sessionVariables section'


# 2. --only aliases, any case: the output key and the filter name both work.
$a = & $Oml inspect $mod --own --only siteproperties,SERVERACTIONS | ConvertFrom-Json
Require (@($a.PSObject.Properties.Name | Sort-Object) -join ',' -eq 'serverActions,siteProperties') '--only aliases'
$b = & $Oml inspect $mod --own --only actions | ConvertFrom-Json
Require (@($b.PSObject.Properties.Name | Sort-Object) -join ',' -eq 'serverActions') '--only actions'
$c = & $Oml inspect $mod --own --only clientActions | ConvertFrom-Json
Require ($null -eq $c.PSObject.Properties['serverActions']) '--only clientActions selects one kind'
& $Oml inspect $mod --only nonsense | Out-Null
Require ($LASTEXITCODE -eq 2) '--only unknown must exit 2'

# 3. --in: every .oml in the folder, a broken file does not stop the rest, no recursion unless asked.
$folder = Join-Path $dir 'many'; New-Item -ItemType Directory -Force (Join-Path $folder 'sub') | Out-Null
Copy-Item $mod (Join-Path $folder 'One.oml'); Copy-Item $mod (Join-Path $folder 'Two.oml'); Copy-Item $mod (Join-Path $folder 'sub\Deep.oml')
Set-Content (Join-Path $folder 'Broken.oml') 'not a module' -Encoding ascii
$many = & $Oml inspect --in $folder --own --only serverActions | ConvertFrom-Json
Require ($LASTEXITCODE -eq 1) '--in with a broken file exits 1'
Require ((@($many.modules | ForEach-Object file) -join ',') -eq 'Broken.oml,One.oml,Two.oml') '--in lists the top folder only'
Require ((($many.modules | Where-Object file -eq 'Broken.oml').error) -like '*Broken.oml*' ) 'broken file error entry'
$one = $many.modules | Where-Object file -eq 'One.oml'
Require ($one.serverActions.Count -ge 1 -and $null -eq $one.PSObject.Properties['error']) '--in module output'
$same = & $Oml inspect $mod --own --only serverActions | ConvertFrom-Json
Require (($one.serverActions | ConvertTo-Json -Depth 20 -Compress) -ceq ($same.serverActions | ConvertTo-Json -Depth 20 -Compress)) '--in output equals single inspect'
$deep = & $Oml inspect --in $folder --recursive --only entities | ConvertFrom-Json
Require (@($deep.modules).Count -eq 4) '--recursive adds sub folders'
& $Oml inspect --in (Join-Path $dir 'missing') | Out-Null
Require ($LASTEXITCODE -eq 2) '--in missing folder exits 2'
& $Oml inspect --in $folder --only nonsense | Out-Null
Require ($LASTEXITCODE -eq 2) '--in with a bad --only stops with exit 2'
$bad = & $Oml inspect --in $folder --changed-since yesterday | ConvertFrom-Json
Require ($LASTEXITCODE -eq 2 -and $null -eq $bad.modules) '--in with a bad date stops with exit 2'

# 4. --in inside oml run keeps the run's own module sharing: the later step still works and the run ends cleanly.
$steps = Join-Path $dir 'steps.json'
$stepList = @{ steps = @(@('inspect', $mod, '--own', '--only', 'serverActions'), @('inspect', '--in', $folder, '--own', '--only', 'serverActions'), @('inspect', $mod, '--own', '--only', 'serverActions')) }
$stepList | ConvertTo-Json -Depth 5 | Set-Content $steps -Encoding utf8
$run = & $Oml run $steps --continue | ConvertFrom-Json
Require (@($run.results | ForEach-Object exitCode) -join ',' -eq '0,1,0') 'run with inspect --in'
Require (@($run.results[1].output.modules).Count -eq 3 -and @($run.results[2].output.serverActions).Count -eq 1) 'run: the step after inspect --in'

# 5. --changed-since leaves out session variables that have no recent change.
$none = & $Oml inspect $web --own --changed-since 2999-01-01 | ConvertFrom-Json
Require ($null -eq $none.PSObject.Properties['sessionVariables'] -or @($none.sessionVariables).Count -eq 0) '--changed-since must filter sessionVariables'
Write-Host 'inspect-check ok'
