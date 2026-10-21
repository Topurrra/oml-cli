param([string]$Oml = "$PSScriptRoot\..\src\bin\Release\net8.0\oml.exe", [string]$Tmp = "$PSScriptRoot\..\tmp")
$ErrorActionPreference = 'Stop'
$assetDir = Join-Path $Tmp 'assets'
New-Item -ItemType Directory -Force $assetDir | Out-Null
$env:TEMP = $Tmp; $env:TMP = $Tmp
$project = Join-Path $PSScriptRoot 'AssetsTests\AssetsTests.csproj'
& 'C:\Program Files\dotnet\dotnet.exe' build -c Release -v q -nologo $project
if ($LASTEXITCODE -ne 0) { throw 'Asset test harness build failed' }
$checks = Join-Path $PSScriptRoot 'AssetsTests\bin\Release\net8.0\AssetsTests.exe'
function Check-Asset([string[]]$Arguments) {
    & $checks @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Asset verification failed: $Arguments" }
}
function Run-Asset([string[]]$Arguments) {
    $text = & $Oml @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Asset command failed: $Arguments : $text" }
    return ($text | ConvertFrom-Json)
}
function Refuse-Asset([string[]]$Arguments, [string]$Pattern) {
    $text = & $Oml @Arguments
    if ($LASTEXITCODE -eq 0 -or ($text | ConvertFrom-Json).error -notmatch $Pattern) { throw "Expected refusal: $Arguments : $text" }
}
$fontFiles = @(Get-ChildItem "$env:WINDIR\Fonts" -Filter *.ttf | Select-Object -First 2)
$otf = Get-ChildItem "$env:WINDIR\Fonts" -Filter *.otf | Select-Object -First 1
if ($otf) { $fontFiles = @($fontFiles[0], $otf) }
if ($fontFiles.Count -lt 2) { throw 'Two Windows fonts required' }
foreach ($font in $fontFiles) { Copy-Item -LiteralPath $font.FullName -Destination $assetDir -Force }
Check-Asset @('generate', $assetDir, (Join-Path $assetDir $fontFiles[0].Name))
$reports = @()
foreach ($name in @($fontFiles.Name) + @('source.woff')) {
    $inputFile = Join-Path $assetDir $name
    $inputHash = (Get-FileHash -LiteralPath $inputFile).Hash
    $outputFile = "$inputFile.woff2"
    $reports += Run-Asset @('font','woff2',$inputFile,$outputFile,'--force')
    Check-Asset @('font',$inputFile,$outputFile)
    Refuse-Asset @('font','woff2',$inputFile,$outputFile) 'exists'
    $already = Run-Asset @('font','woff2',$outputFile)
    if ($already.status -ne 'already woff2') { throw 'Already-WOFF2 handling' }
    if ((Get-FileHash -LiteralPath $inputFile).Hash -ne $inputHash) { throw 'Font input changed' }
}
# Reconstructed sfnt has normalized head bytes: a second round trip must preserve every byte of every table.
$normalized = Join-Path $assetDir ($fontFiles[0].Name + '.woff2.sfnt')
Copy-Item $normalized "$assetDir\normalized.ttf" -Force
$reports += Run-Asset @('font','woff2',"$assetDir\normalized.ttf","$assetDir\normalized.woff2",'--force')
Check-Asset @('font',"$assetDir\normalized.ttf","$assetDir\normalized.woff2")
# Optional browser check; no browser installation is required by this suite.
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
if (Test-Path -LiteralPath $chrome) {
    $fontLoads = foreach ($name in @($fontFiles.Name) + @('source.woff')) {
        $base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $assetDir "$name.woff2")))
        'new FontFace("f' + [Guid]::NewGuid().ToString('N') + '","url(data:font/woff2;base64,' + $base64 + ')").load()'
    }
    $html = '<html><body>WAIT<script>Promise.all([' + ($fontLoads -join ',') + ']).then(()=>document.body.textContent="FONT_OK").catch(e=>document.body.textContent="FONT_FAIL:"+e);</script></body></html>'
    $page = [IO.Path]::GetFullPath((Join-Path $assetDir 'browser.html'))
    [IO.File]::WriteAllText($page, $html)
    $browserOut = Join-Path $assetDir 'browser-result.txt'
    $profile = [IO.Path]::GetFullPath((Join-Path $assetDir ('chrome-' + [Guid]::NewGuid().ToString('N'))))
    $browserArgs = @('--headless','--disable-gpu','--no-first-run','--no-default-browser-check','--disable-background-networking','--dump-dom','--virtual-time-budget=10000',"--user-data-dir=`"$profile`"",('"file:///' + ($page -replace '\\','/') + '"'))
    $browser = Start-Process -FilePath $chrome -ArgumentList $browserArgs -WindowStyle Hidden -PassThru -RedirectStandardOutput $browserOut -RedirectStandardError (Join-Path $assetDir 'browser-stderr.txt')
    if (-not $browser.WaitForExit(20000)) { $browser.Kill($true); Write-Host 'Browser check skipped: headless Chrome timed out.' }
    elseif ((Get-Content -LiteralPath $browserOut -Raw) -notmatch '<body>FONT_OK</body>') { throw 'Chrome rejected a generated WOFF2 font' }
    else { Write-Host 'ok   Chrome loads WOFF2 from both system fonts and generated WOFF1' }
}
$cases = @(@('gradient.png','webp',320,160), @('flat.png','png',160,80), @('photo.jpeg','jpeg',320,160), @('drawing.svg','png',320,160))
foreach ($case in $cases) {
    $inputFile = Join-Path $assetDir $case[0]; $outputFile = "$inputFile.out.$($case[1])"
    $r = Run-Asset @('image',$inputFile,'--to',$case[1],'--max-width',"$($case[2])",'--quality','75','--out',$outputFile,'--force')
    $reports += $r
    Check-Asset @('image',$outputFile,"$($case[2])","$($case[3])")
    if ($case[0] -ne 'drawing.svg' -and $r.outputBytes -ge $r.inputBytes) { throw "No reduction: $($case[0])" }
}
$reports += Run-Asset @('--pretty','image',"$assetDir\drawing.svg",'--to','svg','--out',"$assetDir\optimized.svg",'--force')
Check-Asset @('svg',"$assetDir\optimized.svg",'title')
$reports += Run-Asset @('image',"$assetDir\drawing.svg",'--strip-title','--precision','2','--out',"$assetDir\stripped.svg",'--force')
Check-Asset @('svg',"$assetDir\stripped.svg",'no-title')
$r = Run-Asset @('image',"$assetDir\flat.png",'--max-width','999','--out',"$assetDir\no-upscale.png",'--force')
Check-Asset @('image',"$assetDir\no-upscale.png",'640','320')
foreach ($case in @(@('first-frame.gif',1,1),@('tiny.bmp',2,1))) {
    $null = Run-Asset @('image',(Join-Path $assetDir $case[0]),'--to','png','--out',"$assetDir\$($case[0]).png",'--force')
    Check-Asset @('image',"$assetDir\$($case[0]).png","$($case[1])","$($case[2])")
}
Refuse-Asset @('image',"$assetDir\tiny.bmp",'--force') 'cannot encode'
$traced = Run-Asset @('image',"$assetDir\flat.png",'--to','svg','--out',"$assetDir\flat.traced.svg",'--force')
if ($traced.details.mode -ne 'trace' -or $traced.details.colors -ne 1) { throw "Flat PNG trace: $($traced | ConvertTo-Json -Compress -Depth 5)" }
Refuse-Asset @('image',"$assetDir\flat.png",'--quality','101') 'between'
Refuse-Asset @('image',"$assetDir\flat.png",'--out',"$assetDir\no-upscale.png") 'exists'
Refuse-Asset @('image',"$assetDir\flat.png",'--max-width','0') 'between'
$inputHash = (Get-FileHash "$assetDir\flat.png").Hash
Refuse-Asset @('image',"$assetDir\flat.png",'--out',"$assetDir\flat.png",'--force') 'input'
if ((Get-FileHash "$assetDir\flat.png").Hash -ne $inputHash) { throw 'Image input changed' }
[IO.File]::WriteAllBytes("$assetDir\collection.ttc", [byte[]](0x74,0x74,0x63,0x66,0,0,0,0,0,0,0,0))
Refuse-Asset @('font','woff2',"$assetDir\collection.ttc") 'collections'
[IO.File]::WriteAllText("$assetDir\bad.svg", '<!DOCTYPE svg [<!ENTITY x "bad">]><svg xmlns="http://www.w3.org/2000/svg">&x;</svg>')
Refuse-Asset @('image',"$assetDir\bad.svg",'--force') 'DTD'
[IO.File]::WriteAllBytes("$assetDir\bad.ttf", [byte[]](0,1,2))
Refuse-Asset @('font','woff2',"$assetDir\bad.ttf") 'Truncated'
$batchIn = Join-Path $assetDir 'batch-in'; New-Item -ItemType Directory -Force $batchIn | Out-Null
Copy-Item "$assetDir\flat.png" $batchIn -Force
$batch = Run-Asset @('image','batch',$batchIn,'--to','webp','--out',"$assetDir\batch-images",'--force')
if ($batch.failed -ne 0 -or @($batch.results).Count -ne 1) { throw 'Image batch failed' }
Copy-Item (Join-Path $assetDir $fontFiles[0].Name) $batchIn -Force
$batch = Run-Asset @('font','batch',$batchIn,'--out',"$assetDir\batch-fonts",'--force')
if ($batch.failed -ne 0 -or @($batch.results).Count -ne 1) { throw 'Font batch failed' }
$reports | ConvertTo-Json -Depth 10 | Set-Content "$assetDir\report.json"
$reports | Select-Object @{n='input';e={Split-Path $_.input -Leaf}},inputBytes,outputBytes,ratio | Format-Table | Out-Host
if (-not $otf) { Write-Host 'No OTF font installed in Windows Fonts; verified two TTF fonts plus generated WOFF1.' }
Write-Host 'ok   assets: font table round trips, sfnt checksums, image decodes, SVG parsing, batch and refusals'
