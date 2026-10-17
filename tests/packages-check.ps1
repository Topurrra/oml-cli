param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$env:TEMP = $Tmp; $env:TMP = $Tmp
$dir = Join-Path $Tmp ('packages-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
function Run([string[]]$Arguments) {
    $result = & $Oml @Arguments | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "oml $($Arguments -join ' '): $($result | ConvertTo-Json -Depth 10 -Compress)" }
    return $result
}
function Check($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Census([string]$Path) {
    $c = Run @('census', $Path)
    return ($c.byType.PSObject.Properties | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ';'
}
$blank = Join-Path $dir 'blank.oml'
Run @('new', $blank, '--name', 'PackageSource', '--kind', 'CrossDevice') | Out-Null
$spec = Join-Path $dir 'extension.json'
@'
{"structures":[{"name":"Item","attributes":[{"name":"Label","type":"Text","length":80},{"name":"Count","type":"Integer"}]}],"actions":[{"name":"ReadItems","inputs":[{"name":"Limit","type":"Integer","mandatory":true}],"outputs":[{"name":"Items","type":"List of Item"}]}]}
'@ | Set-Content $spec
$xif = Join-Path $dir 'Sample.xif'
Run @('extension', 'new', $xif, '--name', 'PackageExtension', '--spec', $spec) | Out-Null
$extension = Run @('extension', 'inspect', $xif)
Check ($extension.actions[0].parameters[1].type -eq 'List of Item' -and $extension.structures[0].attributes.Count -eq 2) 'extension signature round-trip'
$source = Join-Path $PSScriptRoot '../ref/test.oml'
if (-not (Test-Path $source)) { $source = $blank }
$hash = (Get-FileHash $source).Hash
$app = Join-Path $dir 'sample.oap'
Run @('app', 'pack', $app, '--name', 'PackageApp', $source, $xif) | Out-Null
$packageHash = (Get-FileHash $app).Hash
$listed = Run @('app', 'list', $app)
Check ($listed.applications.Count -eq 1 -and $listed.modules.Count -eq 2) 'application list missing modules'
Check ($listed.modules[0].size -gt 0 -and $listed.applications[0].key) 'application metadata missing'
Check ($listed.applications[0].dependencies.Count -eq 0) 'unexpected application dependencies'
$extract = Join-Path $dir 'extracted'
$files = (Run @('app', 'extract', $app, $extract)).extracted
foreach ($file in $files) { Check ((Get-FileHash $file.path).Hash -eq $file.sha256) 'extracted hash mismatch' }
$extractedOml = ($files | Where-Object path -Like '*.oml').path
Check ((Census $source) -eq (Census $extractedOml)) 'package changed element counts'
$a = Run @('validate', $source); $b = Run @('validate', $extractedOml)
Check ($a.errorCount -eq $b.errorCount) 'package changed validation'
Run @('extension', 'inspect', ($files | Where-Object path -Like '*.xif').path) | Out-Null
$refused = & $Oml app extract $app $extract | ConvertFrom-Json
Check ($LASTEXITCODE -ne 0 -and $refused.problems.Count -gt 0) 'extract must refuse overwrite'
Check ((Get-FileHash $app).Hash -eq $packageHash) 'package input changed'
$template = Join-Path $dir 'template.oml'
$r = Run @('new', $template, '--from-template', $source, '--name', 'FromTemplate')
Check $r.saved 'template was not saved'
$before = Run @('inspect', $source); $after = Run @('inspect', $template)
Check ($before.module.key -ne $after.module.key -and $after.name -eq 'FromTemplate') 'template retained module identity'
Check ($before.module.defaultTheme -eq $after.module.defaultTheme) 'template lost theme'
Check ((($before.references.module | Sort-Object) -join ',') -eq (($after.references.module | Sort-Object) -join ',')) 'template lost references'
Check ((($before.screens.name | Sort-Object) -join ',') -eq (($after.screens.name | Sort-Object) -join ',')) 'template lost blocks/layout'
Run @('validate', $template) | Out-Null
$png = Join-Path $dir 'icon.png'
[IO.File]::WriteAllBytes($png, [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aJ1sAAAAASUVORK5CYII='))
$settings = @{ description = 'Package settings'; javaScript = 'console.log(42);'; isUserProvider = $false; isMultiTenant = $false; icon = $png }
if ($before.module.defaultTheme) { $settings.defaultTheme = $before.module.defaultTheme }
$moduleSpec = Join-Path $dir 'settings.json'
@{ module = $settings } | ConvertTo-Json -Depth 5 | Set-Content $moduleSpec
$out = Join-Path $dir 'settings.oml'
$r = Run @('apply', $source, $moduleSpec, $out)
Check $r.saved 'module settings not saved'
$inspected = Run @('inspect', $out)
Check ($inspected.module.description -eq 'Package settings' -and $inspected.module.javaScript -eq 'console.log(42);' -and $inspected.module.icon.size -eq (Get-Item $png).Length) 'module settings failed round-trip'
Check ($inspected.module.key -eq $before.module.key -and $inspected.name -eq $before.name) 'settings changed module identity'
foreach ($invalid in @('{"module":{"name":"Oops"}}', '{"module":{"defaultTheme":"MissingTheme"}}', '{"module":{"dbms":"All"}}')) {
    $invalid | Set-Content $moduleSpec
    $badOut = Join-Path $dir ('bad-' + [guid]::NewGuid().ToString('N') + '.oml')
    $r = & $Oml apply $source $moduleSpec $badOut | ConvertFrom-Json
    Check ($LASTEXITCODE -ne 0 -and -not $r.saved -and $r.problems.Count -gt 0 -and -not (Test-Path $badOut)) 'unsupported setting must prevent save'
}
Check ((Get-FileHash $source).Hash -eq $hash) 'source changed'
Write-Host 'ok   packages, template factory, extensions, module settings'
