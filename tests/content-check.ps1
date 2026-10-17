param($Oml, $Tmp, $Source)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $Tmp ('content-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
$spec = Join-Path $dir 'spec.json'
function Apply-Content($InputModule, $Value, $Name, [switch]$Refuse) {
  $Value | ConvertTo-Json -Depth 40 | Set-Content $spec
  $out = Join-Path $dir ($Name + '.oml')
  $r = & $Oml apply $InputModule $spec $out | ConvertFrom-Json
  if ($Refuse) {
    if ($LASTEXITCODE -eq 0 -or $r.saved -or (Test-Path $out) -or $r.problems.Count -eq 0) { throw "Expected refusal $Name : $($r | ConvertTo-Json -Depth 8)" }
    return $r
  }
  if ($LASTEXITCODE -ne 0 -or -not $r.saved -or $r.reopenNewErrors.Count -gt 0) { throw "$Name : $($r | ConvertTo-Json -Depth 8)" }
  return $out
}
$producerBlank = Join-Path $dir 'producer-blank.oml'
& $Oml new $producerBlank --name ContentProducer --kind CrossDevice | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not create content producer' }
$producer = Apply-Content $producerBlank @{
  serverActions = @(@{ name='ContentAction' })
  entities = @(@{ name='ContentEntity'; attributes=@(@{name='Name'}) })
  structures = @(@{ name='ContentDto'; attributes=@(@{name='Value'}) })
  blocks = @(@{name='ContentBlock'})
  screens = @(@{name='ContentPage'; widgets=@(@{type='Text';text='Hello content'})})
} 'producer'
$hash = (Get-FileHash $producer).Hash
$sourceHash = (Get-FileHash $Source).Hash
$svg = Join-Path $dir 'dot.svg'
'<svg xmlns="http://www.w3.org/2000/svg" width="4" height="4"><rect width="4" height="4"/></svg>' | Set-Content $svg
$js = Join-Path $dir 'helper.js'
'window.contentCheck = true;' | Set-Content $js
$png = Join-Path $dir 'dot.png'
[IO.File]::WriteAllBytes($png, [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aB1sAAAAASUVORK5CYII='))
$built = Apply-Content $Source @{
  copy = @(@{from=$producer; elements=@('ContentAction','ContentEntity','ContentDto','MainFlow/ContentPage','MainFlow/ContentBlock'); into='CopiedFlow'})
  serverActions = @(@{name='UseCopy';flow=@(@{kind='execute'; action='ContentAction'})})
  locales = @('ka-GE','en-US')
  translate = @(@{locale='ka-GE';text='MandatoryInput';translation='Content required'})
  images = @(@{name='ContentImage';file=$svg},@{name='Pixel';file=$png})
  scripts = @(@{name='ContentScript';file=$js})
  resources = @(@{name='ContentFile';file=$js;public=$true})
  themes = @(@{name='ContentBase';styleSheet='.base {color:red;}'},@{name='ContentTheme';styleSheet='.child {color:blue;}';baseTheme='ContentBase'})
  folders = @(@{name='Validation';area='serverActions';elements=@('ContentAction')},@{name='Data';area='entities';elements=@('ContentEntity')})
  screens = @(@{name='ContentConsumer';requiredScripts=@('ContentScript');widgets=@(@{type='Image';name='Logo';image='ContentImage'})})
} 'built'
$i = & $Oml inspect $built | ConvertFrom-Json
if ($i.serverActions.name -notcontains 'ContentAction' -or $i.screens.name -notcontains 'ContentPage' -or $i.structures.name -notcontains 'ContentDto') { throw 'Clipboard content missing' }
if (@($i.screens | Where-Object { $_.name -eq 'ContentPage' -and $_.flow -eq 'CopiedFlow' }).Count -ne 1) { throw 'Wrong copy destination' }
if ($i.content.images.format -notcontains 'Svg' -or $i.content.images.format -notcontains 'Png') { throw 'Images missing' }
if ($i.content.scripts.javaScript -notmatch 'contentCheck') { throw 'Script bytes missing' }
if (-not $i.content.resources[0].isPublic -or $i.content.resources[0].bytes -ne (Get-Item $js).Length) { throw 'Resource bytes/public missing' }
if ($i.content.themes.baseTheme -notcontains 'ContentBase') { throw 'Base theme missing' }
if ($i.content.locales -notcontains 'KA-GE' -or $i.content.translations.values.translation -notcontains 'Content required') { throw 'Translation did not persist' }
if ($i.content.folders.elements -notcontains 'ContentAction' -or $i.content.folders.elements -notcontains 'ContentEntity') { throw 'Folder entries missing' }
$tree = & $Oml tree $built MainFlow/ContentConsumer | ConvertFrom-Json
if ($tree.requiredScripts -notcontains 'ContentScript' -or $tree.children.ref -notcontains 'ContentImage') { throw 'Script/image binding missing' }
$tree = & $Oml tree $built UseCopy | ConvertFrom-Json | ConvertTo-Json -Depth 60
if ($tree -notmatch '"ref":\s*"ContentAction"') { throw 'Later logic cannot use copy' }
$repeat = Apply-Content $built @{folders=@(@{name='Validation';area='serverActions';elements=@('UseCopy')})} 'folder-reuse'
$i = & $Oml inspect $repeat | ConvertFrom-Json
if (@($i.content.folders | Where-Object name -eq 'Validation').Count -ne 1 -or $i.content.folders.elements -notcontains 'UseCopy') { throw 'Folder reuse failed' }
$null = Apply-Content $built @{copy=@(@{from=$producer;elements=@('ContentAction')})} 'clash' -Refuse
$null = Apply-Content $built @{locales=@('unknown-culture')} 'culture' -Refuse
$null = Apply-Content $built @{folders=@(@{name='Wrong';area='images';elements=@('UseCopy')})} 'wrong-area' -Refuse
$null = Apply-Content $built @{images=@(@{name='Missing';file=(Join-Path $dir 'missing.png')})} 'missing-file' -Refuse
# SaveTextResources and LoadTextResources are public but refuse in the 11.55.92 headless runtime.
# Assert the supported round trip on a build that implements them, or the explicit refusal on this build.
foreach ($format in @('ResX','xlsx')) {
  $export = Join-Path $dir ('export-' + $format)
  $r = & $Oml translations export $built $export --format $format | ConvertFrom-Json
  if ($r.exported) {
    if ($format -eq 'ResX') {
      $file = Get-ChildItem $export -Filter '*.ka-GE.resx' | Select-Object -First 1
      if (-not $file) { throw 'Export lacks locale file' }
      $file.FullName | Out-Null
      $round = Apply-Content $built @{translations=@{import=$file.FullName}} 'translation-roundtrip'
      $i = & $Oml inspect $round | ConvertFrom-Json
      if ($i.content.translations.values.translation -notcontains 'Content required') { throw 'Translation round trip lost value' }
    }
  } elseif ($LASTEXITCODE -eq 0 -or $r.problems.Count -eq 0 -or (Test-Path $export)) { throw 'Export refusal was not reported safely' }
}
$badImport = Join-Path $dir 'invalid.resx'
'<root />' | Set-Content $badImport
$null = Apply-Content $built @{translations=@{import=$badImport}} 'import-refusal' -Refuse
# Copy must report required producer signatures instead of adding them silently.
$dependency = Join-Path $Tmp 'references-producer.oml'
if (Test-Path $dependency) {
  $dependent = Apply-Content $Source @{
    references=@(@{producer=$dependency;elements=@('ClientHello','Customer')})
    clientActions=@(@{name='UsesDependency';locals=@(@{name='CustomerId';type='Customer Identifier'});flow=@(@{kind='execute';action='ClientHello'})})
  } 'dependent'
  $r = Apply-Content $Source @{copy=@(@{from=$dependent;elements=@('UsesDependency')})} 'missing-reference' -Refuse
  if (($r | ConvertTo-Json -Depth 20) -notmatch 'ReferenceProducer|ClientHello') { throw 'Missing copy dependency was not named' }
  $withReference = Apply-Content $Source @{
    references=@(@{producer=$dependency;elements=@('ClientHello','Customer')})
    copy=@(@{from=$dependent;elements=@('UsesDependency')})
  } 'explicit-reference'
  $tree = & $Oml tree $withReference UsesDependency | ConvertFrom-Json | ConvertTo-Json -Depth 60
  if ($tree -notmatch '"ref":\s*"ClientHello"') { throw 'Explicit copy dependency was not bound' }
}
if ((Get-FileHash $producer).Hash -ne $hash -or (Get-FileHash $Source).Hash -ne $sourceHash) { throw 'Source changed' }
Write-Host 'ok   clipboard, locales/translations, content, script/image binding, folders, refusals, source hashes'
