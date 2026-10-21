param([string]$Oml = "$PSScriptRoot\..\src\bin\Release\net8.0\oml.exe", [string]$Tmp = "$PSScriptRoot\..\tmp")
# oml image: ICO write and read, PNG to SVG tracing (quality against the source), embed mode, the SVG optimizer and its
# pixel gate, batches and refusals. Fixtures are written here; raster inputs are drawn from them by oml itself.
$ErrorActionPreference = 'Stop'
$Oml = (Resolve-Path -LiteralPath $Oml).Path
$dir = Join-Path $Tmp 'image-check'
if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
New-Item -ItemType Directory -Force $dir | Out-Null
$project = Join-Path $PSScriptRoot 'AssetsTests\AssetsTests.csproj'
& 'C:\Program Files\dotnet\dotnet.exe' build -c Release -v q -nologo $project | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Asset test harness build failed' }
$harness = Join-Path $PSScriptRoot 'AssetsTests\bin\Release\net8.0\AssetsTests.exe'
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Harness([string[]]$Arguments) {
    $text = & $harness @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Harness failed: $Arguments" }
    return $text
}
function Run([string[]]$Arguments) {
    $text = & $Oml @Arguments
    if ($LASTEXITCODE -ne 0) { throw "oml $Arguments failed: $text" }
    return ($text | ConvertFrom-Json)
}
function Refuse([string[]]$Arguments, [string]$Pattern, [int]$Code = 2) {
    $text = & $Oml @Arguments
    $json = $text | ConvertFrom-Json
    $message = if ($json.error) { $json.error } else { (@($json.results) | ForEach-Object error) -join ' | ' }
    if ($LASTEXITCODE -ne $Code -or $message -notmatch $Pattern) { throw "Expected refusal ($Pattern, exit $Code): oml $Arguments -> exit $LASTEXITCODE $text" }
}
# Percent of pixels that differ by more than 64 of 255 on white (see AssetsTests Compare).
# Also sets $script:Window: the worst 16 x 16 window in percent, which shows a local defect (a blob in a letter).
function Mismatch([string]$Reference, [string]$Candidate, [double]$Scale = 1) {
    $line = Harness @('compare', $Reference, $Candidate, "$Scale")
    if ($line -notmatch 'mismatch=([\d.]+) .* window=([\d.]+)') { throw "Bad compare output: $line" }
    $script:Window = [double]$Matches[2]
    return [double]$Matches[1]
}
function Write-Svg([string]$Name, [string]$Text) { $path = Join-Path $dir $Name; [IO.File]::WriteAllText($path, $Text); return $path }

# ---- Fixtures: two-colour logo, multi-colour flat icon, text, shapes with holes, a 2x outline icon. ----
$logo = Write-Svg 'logo.svg' '<svg xmlns="http://www.w3.org/2000/svg" width="512" height="512" viewBox="0 0 40 40"><rect width="40" height="40" rx="9" fill="#e5322d"/><path d="m10 12 8 8-8 8m12 0h9" fill="none" stroke="white" stroke-width="3"/></svg>'
$flat = Write-Svg 'flat.svg' '<svg xmlns="http://www.w3.org/2000/svg" width="512" height="512" viewBox="0 0 64 64"><circle cx="32" cy="32" r="30" fill="#2b7bd6"/><circle cx="24" cy="24" r="10" fill="#ffc83d"/><path d="M14 46a8 8 0 0 1 2-15.7A11 11 0 0 1 37 27a9 9 0 0 1 13 9.5A6 6 0 0 1 48 46z" fill="#fff"/><path d="M20 52l6-8 6 8z" fill="#f26a1b"/><rect x="36" y="48" width="14" height="4" rx="2" fill="#0d2a4a"/></svg>'
$text = Write-Svg 'text.svg' '<svg xmlns="http://www.w3.org/2000/svg" width="640" height="256" viewBox="0 0 640 256"><text x="20" y="120" font-family="Arial" font-weight="bold" font-size="110" fill="#1a1a1a">OAB 8g</text><text x="24" y="220" font-family="Georgia" font-size="72" fill="#c0392b">Recipe @</text></svg>'
$holes = Write-Svg 'holes.svg' '<svg xmlns="http://www.w3.org/2000/svg" width="512" height="512" viewBox="0 0 512 512"><path d="M128 40a88 88 0 1 0 0.01 0zm0 40a48 48 0 1 1-0.01 0z" fill="#5b2a86"/><path d="M280 40h192v192H280zm40 40v112h112V80z" fill-rule="evenodd" fill="#118a7e"/><path d="M40 300h180v180H40z M70 330v40h40v-40z M140 400v50h50v-50z" fill-rule="evenodd" fill="#222"/><circle cx="380" cy="390" r="90" fill="none" stroke="#e67e22" stroke-width="6"/><path d="M300 470L460 310" stroke="#e67e22" stroke-width="3"/></svg>'
$stroke = Write-Svg 'stroke.svg' '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="#111" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 9l9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><path d="M9 22V12h6v10"/></svg>'
# A solid mark with two-colour text (the review's fixture): thin light letters next to bold dark ones.
$brand = Write-Svg 'brand.svg' '<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="400" viewBox="0 0 1200 400"><circle cx="200" cy="200" r="150" fill="#e2001a"/><text x="380" y="225" font-family="Arial" font-weight="bold" font-size="140" fill="#1e1e28">Acme</text><text x="385" y="320" font-family="Arial" font-size="56" fill="#6e6e78">Insurance Group</text></svg>'
# Thin lines (1.5 and 2 px) over a colour: their ends must stay square, not swell.
$thin = Write-Svg 'thin.svg' '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64"><rect width="64" height="64" fill="#0057b8"/><path d="M10 20.5H50" stroke="orange" stroke-width="1.5"/><path d="M10 40H50" stroke="orange" stroke-width="2"/></svg>'
$gradient = Write-Svg 'gradient.svg' '<svg xmlns="http://www.w3.org/2000/svg" width="320" height="160"><defs><linearGradient id="g"><stop offset="0" stop-color="#f00"/><stop offset="1" stop-color="#00f"/></linearGradient></defs><rect width="320" height="160" fill="url(#g)"/></svg>'
foreach ($svg in $logo, $flat, $text, $holes, $stroke, $brand, $thin, $gradient) { $null = Run @('image', $svg, '--to', 'png', '--out', [IO.Path]::ChangeExtension($svg, '.png'), '--force') }

# ---- Trace: quality against the source PNG at 1x and the source SVG at 2x, size, colours, speed. ----
# Limits are percent of pixels off by more than 64/255; measured values are about a third of each limit. Window is
# the worst 16 x 16 window at 1x: clean traces measure 0-8% (edge pixels); a blob inside a letter measured 34%.
$cases = @(
    @{ Name = 'logo'; Colors = 2; At1 = 0.2; At2 = 0.3; MaxBytes = 1500 },
    @{ Name = 'flat'; Colors = 5; At1 = 0.3; At2 = 0.5; MaxBytes = 4000 },
    @{ Name = 'text'; Colors = 2; At1 = 0.4; At2 = 1.5; MaxBytes = 10000 },  # 2x text differs by font hinting, not by the trace
    @{ Name = 'holes'; Colors = 4; At1 = 0.3; At2 = 0.5; MaxBytes = 7000 },
    @{ Name = 'stroke'; Colors = 1; At1 = 1.5; At2 = 3.0; MaxBytes = 1500 },  # 48 px: edges are a large share of the pixels
    @{ Name = 'brand'; Colors = 3; At1 = 0.3; At2 = 1.0; MaxBytes = 8000 },
    @{ Name = 'thin'; Colors = 2; At1 = 0.5; At2 = 1.5; MaxBytes = 1000 })
$rows = foreach ($case in $cases) {
    $png = Join-Path $dir "$($case.Name).png"; $out = Join-Path $dir "$($case.Name).trace.svg"
    $r = Run @('image', $png, '--to', 'svg', '--out', $out, '--force')
    Require ($r.details.mode -eq 'trace' -and $r.details.colors -eq $case.Colors -and -not $r.details.note) "trace $($case.Name): $($r | ConvertTo-Json -Compress -Depth 5)"
    $svgText = Get-Content -LiteralPath $out -Raw
    Require ($svgText -match "viewBox=`"0 0 $($r.before.width) $($r.before.height)`"" -and ([regex]::Matches($svgText, '<path ')).Count -eq $r.details.paths -and $svgText -notmatch '<image') "trace $($case.Name) output: $svgText"
    $at1 = Mismatch $png $out 1; $worst = $script:Window
    $at2 = Mismatch (Join-Path $dir "$($case.Name).svg") $out 2
    Require ($at1 -le $case.At1 -and $at2 -le $case.At2 -and $worst -le 15 -and $r.outputBytes -le $case.MaxBytes) "trace $($case.Name): mismatch $at1% at 1x (limit $($case.At1)), $at2% at 2x (limit $($case.At2)), worst window $worst% (limit 15), $($r.outputBytes) bytes (limit $($case.MaxBytes))"
    [pscustomobject]@{ fixture = $case.Name; png = $r.inputBytes; svg = $r.outputBytes; colors = $r.details.colors; mismatch1x = $at1; mismatch2x = $at2; window = $worst }
}
$rows | Format-Table | Out-Host
# Holes stay holes: the middle of the ring and of the square frame are empty in the traced drawing.
foreach ($p in @(@(128, 128), @(376, 136))) {
    $pixel = Harness @('pixel', (Join-Path $dir 'holes.trace.svg'), "$($p[0])", "$($p[1])")
    Require ($pixel -match ',0$') "A hole was filled at $p : $pixel"
}
# Speed: a 1024 x 1024 logo traces in a few seconds.
$big = Write-Svg 'big.svg' ((Get-Content -LiteralPath $flat -Raw).Replace('width="512" height="512"', 'width="1024" height="1024"'))
$null = Run @('image', $big, '--to', 'png', '--out', (Join-Path $dir 'big.png'), '--force')
$watch = [Diagnostics.Stopwatch]::StartNew()
$r = Run @('image', (Join-Path $dir 'big.png'), '--to', 'svg', '--out', (Join-Path $dir 'big.trace.svg'), '--force')
$watch.Stop()
Require ($watch.Elapsed.TotalSeconds -lt 5 -and (Mismatch (Join-Path $dir 'big.png') (Join-Path $dir 'big.trace.svg')) -le 0.2) "1024 trace took $($watch.Elapsed.TotalSeconds) s"
# Options: fixed colours, a looser tolerance gives a smaller file, speckles under --min-area go.
$loose = Run @('image', (Join-Path $dir 'flat.png'), '--to', 'svg', '--tolerance', '1.5', '--out', (Join-Path $dir 'flat.loose.svg'), '--force')
Require ($loose.outputBytes -lt ($rows | Where-Object fixture -eq 'flat').svg) 'A larger --tolerance must give a smaller SVG'
$two = Run @('image', (Join-Path $dir 'flat.png'), '--to', 'svg', '--colors', '2', '--out', (Join-Path $dir 'flat.two.svg'), '--force')
Require ($two.details.colors -eq 2 -and $two.details.paths -le 2) "--colors 2: $($two.details | ConvertTo-Json -Compress)"
$noSmall = Run @('image', (Join-Path $dir 'holes.png'), '--to', 'svg', '--min-area', '20000', '--out', (Join-Path $dir 'holes.big-only.svg'), '--force')
Require ($noSmall.outputBytes -lt ($rows | Where-Object fixture -eq 'holes').svg) '--min-area must drop small shapes'
# A gradient is not a logo: the trace says so and points to embed mode.
$g = Run @('image', (Join-Path $dir 'gradient.png'), '--to', 'svg', '--out', (Join-Path $dir 'gradient.trace.svg'), '--force')
Require ($g.details.note -match 'photo or a gradient' -and $g.details.note -match '--mode embed') "gradient note: $($g.details | ConvertTo-Json -Compress)"
# Alpha at exactly the threshold is opaque; one below is not. An image with only a speck gives a clear error.
foreach ($alpha in 128, 127) { $null = Harness @('alphapng', (Join-Path $dir "alpha$alpha.png"), '20', '10', "$alpha") }
$null = Run @('image', (Join-Path $dir 'alpha128.png'), '--to', 'svg', '--out', (Join-Path $dir 'alpha128.svg'), '--force')
Require ((Harness @('pixel', (Join-Path $dir 'alpha128.svg'), '10', '10')) -eq '0,0,0,255') 'Alpha 128 must count as opaque'
Refuse @('image', (Join-Path $dir 'alpha127.png'), '--to', 'svg') 'no pixels with alpha 128 or more'
$null = Harness @('alphapng', (Join-Path $dir 'speck.png'), '20', '1', '255')
Refuse @('image', (Join-Path $dir 'speck.png'), '--to', 'svg') 'smaller than --min-area'
# Semi-transparent areas (a shadow) get a note; the logos above got none.
$shadow = Write-Svg 'shadow.svg' '<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256"><rect x="40" y="48" width="180" height="180" rx="20" fill="#000" fill-opacity=".3"/><rect x="28" y="28" width="180" height="180" rx="20" fill="#2ea043"/></svg>'
$null = Run @('image', $shadow, '--to', 'png', '--out', (Join-Path $dir 'shadow.png'), '--force')
$r = Run @('image', (Join-Path $dir 'shadow.png'), '--to', 'svg', '--out', (Join-Path $dir 'shadow.trace.svg'), '--force')
Require ($r.details.note -match 'semi-transparent') "Shadow note: $($r.details | ConvertTo-Json -Compress)"
# --transparent-bg drops the colour around the logo; without it, the white stays as a shape.
$onWhite = Write-Svg 'on-white.svg' ((Get-Content -LiteralPath $brand -Raw).Replace('<circle', '<rect width="1200" height="400" fill="#fff"/><circle'))
$null = Run @('image', $onWhite, '--to', 'png', '--out', (Join-Path $dir 'on-white.png'), '--force')
$null = Run @('image', (Join-Path $dir 'on-white.png'), '--to', 'svg', '--out', (Join-Path $dir 'on-white.svg.svg'), '--force')
Require ((Harness @('pixel', (Join-Path $dir 'on-white.svg.svg'), '3', '3')) -eq '255,255,255,255') 'Without --transparent-bg the white background stays'
$r = Run @('image', (Join-Path $dir 'on-white.png'), '--to', 'svg', '--transparent-bg', '--out', (Join-Path $dir 'on-white.clear.svg'), '--force')
Require ($r.details.removedBackground -eq '#ffffff' -and (Harness @('pixel', (Join-Path $dir 'on-white.clear.svg'), '3', '3')) -match ',0$' -and (Harness @('pixel', (Join-Path $dir 'on-white.clear.svg'), '200', '200')) -match ',255$') "--transparent-bg: $($r.details | ConvertTo-Json -Compress)"
$r = Run @('image', (Join-Path $dir 'flat.png'), '--to', 'svg', '--transparent-bg', '--out', (Join-Path $dir 'flat.clear.svg'), '--force')
Require ($r.details.note -match 'nothing was left out' -and -not $r.details.removedBackground) "--transparent-bg without a background: $($r.details | ConvertTo-Json -Compress)"
Write-Host 'ok   image trace: logo, flat icon, text, holes, 2x outline icon, two-colour text, thin lines within limits (also per 16 px window); 1024 px in under 5 s; options; alpha threshold; speck; photo and shadow notes; --transparent-bg'

# ---- Embed: the PNG inside an SVG, drawn the same. ----
$e = Run @('image', (Join-Path $dir 'gradient.png'), '--to', 'svg', '--mode', 'embed', '--out', (Join-Path $dir 'gradient.embed.svg'), '--force')
$embedText = Get-Content -LiteralPath (Join-Path $dir 'gradient.embed.svg') -Raw
Require ($e.details.mode -eq 'embed' -and $e.details.note -match 'not a vector' -and $embedText -match '<image width="320" height="160" href="data:image/png;base64,' -and $embedText -match 'viewBox="0 0 320 160"') "embed: $embedText"
Require ((Mismatch (Join-Path $dir 'gradient.png') (Join-Path $dir 'gradient.embed.svg')) -eq 0) 'Embedded SVG must draw like its PNG'
Write-Host 'ok   image embed: PNG inside SVG, named as such, same pixels'

# ---- ICO: write, independent parse, Windows reads every size, read back to PNG/WebP. ----
$app = Join-Path $dir 'app.ico'
$r = Run @('image', (Join-Path $dir 'flat.png'), '--to', 'ico', '--sizes', '16,24,32,48,64,128,256', '--out', $app, '--force')
Require (((@($r.details.sizes) -join ',') -eq '16,24,32,48,64,128,256') -and ((@($r.details.frames) -join ',') -eq 'bmp,bmp,bmp,bmp,png,png,png')) "ICO report: $($r.details | ConvertTo-Json -Compress)"
$null = Harness @('ico', $app, '16,24,32,48,64,128,256')
$favicon = Join-Path $dir 'favicon.ico'
$r = Run @('image', $logo, '--to', 'ico', '--out', $favicon, '--force')  # from SVG, default favicon sizes
Require ((@($r.details.sizes) -join ',') -eq '16,32,48') "Default ICO sizes: $($r.details.sizes)"
$null = Harness @('ico', $favicon, '16,32,48')
# Windows' own loader (what Explorer and the shell use) reads each frame at its size.
Add-Type -Namespace OmlImageCheck -Name User32 -MemberDefinition @'
[DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr LoadImage(IntPtr h, string name, uint type, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr h, out ICONINFO info);
[DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
[DllImport("gdi32.dll")] public static extern int GetObject(IntPtr h, int size, out BITMAP bm);
[DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
public struct ICONINFO { public bool fIcon; public int x; public int y; public IntPtr mask; public IntPtr color; }
public struct BITMAP { public int type; public int width; public int height; public int widthBytes; public ushort planes; public ushort bits; public IntPtr bitsPtr; }
'@
function Windows-IconSize([string]$Path, [int]$Size) {
    $h = [OmlImageCheck.User32]::LoadImage([IntPtr]::Zero, $Path, 1, $Size, $Size, 0x10)
    if ($h -eq [IntPtr]::Zero) { throw "Windows cannot load $Path at $Size" }
    $info = New-Object OmlImageCheck.User32+ICONINFO; [void][OmlImageCheck.User32]::GetIconInfo($h, [ref]$info)
    $bm = New-Object OmlImageCheck.User32+BITMAP; [void][OmlImageCheck.User32]::GetObject($info.color, [Runtime.InteropServices.Marshal]::SizeOf($bm), [ref]$bm)
    [void][OmlImageCheck.User32]::DeleteObject($info.color); [void][OmlImageCheck.User32]::DeleteObject($info.mask); [void][OmlImageCheck.User32]::DestroyIcon($h)
    return "$($bm.width)x$($bm.height)x$($bm.bits)"
}
foreach ($s in 16, 24, 32, 48, 64, 128, 256) { Require ((Windows-IconSize $app $s) -eq "${s}x${s}x32") "Windows LoadImage $s" }
foreach ($s in 16, 32, 48) { Require ((Windows-IconSize $favicon $s) -eq "${s}x${s}x32") "Windows LoadImage favicon $s" }
# System.Drawing reads the BMP frames (.NET picks frames by its own rules, so only exact BMP sizes are asked for).
Add-Type -AssemblyName System.Drawing
foreach ($s in 16, 32, 48) {
    $icon = New-Object System.Drawing.Icon($app, $s, $s); $bitmap = $icon.ToBitmap()
    Require ($bitmap.Width -eq $s -and $bitmap.GetPixel($s / 2, $s / 2).A -eq 255 -and $bitmap.GetPixel(0, 0).A -eq 0) "System.Drawing.Icon $s"
    $bitmap.Dispose(); $icon.Dispose()
}
# Reading: the largest frame, from PNG (256) and from BMP (48), to PNG and WebP.
$r = Run @('image', $app, '--to', 'png', '--out', (Join-Path $dir 'app.png'), '--force')
Require ($r.before.width -eq 256 -and $r.after.width -eq 256) "ICO read (PNG frame): $($r | ConvertTo-Json -Compress)"
$null = Harness @('image', (Join-Path $dir 'app.png'), '256', '256')
$r = Run @('image', $favicon, '--to', 'webp', '--out', (Join-Path $dir 'favicon.webp'), '--force')
Require ($r.before.width -eq 48) "ICO read (BMP frame): $($r | ConvertTo-Json -Compress)"
$null = Run @('image', $favicon, '--to', 'png', '--out', (Join-Path $dir 'favicon.png'), '--force')
$logo48 = Write-Svg 'logo48.svg' ((Get-Content -LiteralPath $logo -Raw).Replace('width="512" height="512"', 'width="48" height="48"'))
Require ((Mismatch $logo48 (Join-Path $dir 'favicon.png')) -le 3) 'The 48 px BMP frame must read back as the drawing'
# Old icons: 24-bit and 8-bit palette frames whose transparency is only the AND mask.
$null = Harness @('oldico', $dir)
foreach ($old in 'old24', 'old8') {
    $null = Run @('image', (Join-Path $dir "$old.ico"), '--to', 'png', '--out', (Join-Path $dir "$old.png"), '--force')
    $px = @(0..1 | ForEach-Object { $y = $_; 0..1 | ForEach-Object { Harness @('pixel', (Join-Path $dir "$old.png"), "$_", "$y") } })
    Require ($px[0] -match ',0$' -and $px[1] -eq '255,0,0,255' -and $px[2] -eq '0,0,255,255' -and $px[3] -eq '0,0,255,255') "$old.ico pixels: $px"
}
# Non-square input is centred on a transparent square.
$wide = Write-Svg 'wide.svg' '<svg xmlns="http://www.w3.org/2000/svg" width="300" height="100"><rect width="300" height="100" fill="#0a0"/></svg>'
$null = Run @('image', $wide, '--to', 'png', '--out', (Join-Path $dir 'wide.png'), '--force')
$null = Run @('image', (Join-Path $dir 'wide.png'), '--to', 'ico', '--sizes', '48', '--out', (Join-Path $dir 'wide.ico'), '--force')
$null = Run @('image', (Join-Path $dir 'wide.ico'), '--to', 'png', '--out', (Join-Path $dir 'wide.ico.png'), '--force')
Require ((Harness @('pixel', (Join-Path $dir 'wide.ico.png'), '24', '2')) -match ',0$' -and (Harness @('pixel', (Join-Path $dir 'wide.ico.png'), '24', '24')) -eq '0,170,0,255') 'Wide image must be padded, not stretched'
# Real icons on this machine, if any: each reads to its largest frame.
$real = @("$env:WINDIR\System32\OneDrive.ico", "${env:CommonProgramFiles}\OutSystems\11\ServiceStudio.ico") | Where-Object { Test-Path -LiteralPath $_ }
foreach ($ico in $real) { $null = Run @('image', $ico, '--to', 'png', '--out', (Join-Path $dir ((Split-Path $ico -Leaf) + '.png')), '--force') }
# A very wide image is scaled down before it is padded to a square: no 20000 x 20000 canvas. Peak memory is sampled
# every 20 ms while oml runs.
function Peak-MB([string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($Oml); foreach ($a in $Arguments) { $info.ArgumentList.Add($a) }
    $info.RedirectStandardOutput = $true; $info.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($info); $read = $process.StandardOutput.ReadToEndAsync(); $peak = 0
    while (-not $process.HasExited) { try { $process.Refresh(); $peak = [Math]::Max($peak, $process.PeakWorkingSet64) } catch {}; Start-Sleep -Milliseconds 20 }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "oml $Arguments failed: $($read.Result)" }
    return [Math]::Round($peak / 1MB)
}
foreach ($size in @(@(20000, 100), @(50000, 200))) {
    $stripSvg = Write-Svg "strip-$($size[0]).svg" "<svg xmlns=`"http://www.w3.org/2000/svg`" width=`"$($size[0])`" height=`"$($size[1])`"><rect width=`"$($size[0])`" height=`"$($size[1])`" fill=`"#36c`"/></svg>"
    $strip = [IO.Path]::ChangeExtension($stripSvg, '.png')
    $null = Run @('image', $stripSvg, '--to', 'png', '--out', $strip, '--force')
    $mb = Peak-MB @('image', $strip, '--to', 'ico', '--sizes', '16,32,48,256', '--out', [IO.Path]::ChangeExtension($strip, '.ico'), '--force')
    Require ($mb -lt 200) "A $($size[0]) x $($size[1]) image to ICO peaked at $mb MB"
    $null = Harness @('ico', [IO.Path]::ChangeExtension($strip, '.ico'), '16,32,48,256')
}
# A tiny ICO whose PNG frame claims 60000 x 60000 pixels is refused before decoding; so is a palette over 256 colours.
$null = Harness @('hugeclaim', (Join-Path $dir 'huge-claim.ico'))
$watch = [Diagnostics.Stopwatch]::StartNew()
Refuse @('image', (Join-Path $dir 'huge-claim.ico'), '--to', 'png') '60000 x 60000 pixels, over the 100 million pixel decode limit'
Require ($watch.Elapsed.TotalSeconds -lt 5) "The huge PNG claim took $($watch.Elapsed.TotalSeconds) s to refuse"
Refuse @('image', (Join-Path $dir 'used300.ico'), '--to', 'png') 'claims 300 palette colours'
Write-Host "ok   image ICO: 7 sizes and favicon from SVG, Windows LoadImage and System.Drawing read them, ICO to PNG/WebP, old mask-only icons, padding, wide images in little memory, huge and broken headers refused, $(@($real).Count) installed icons"

# ---- SVG optimizer: structure rules, accessibility, gate. ----
$tricky = Write-Svg 'tricky.svg' @'
<?xml version="1.0" encoding="UTF-8" standalone="no"?>
<!-- Created with Inkscape -->
<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape" xmlns:sodipodi="http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd" width="200" height="120" viewBox="0 0 200 120" version="1.1" id="svg1" inkscape:version="1.3" enable-background="new 0 0 200 120">
  <title>Tricky test drawing</title>
  <desc>Shapes that exercise every optimization</desc>
  <sodipodi:namedview id="base" pagecolor="#ffffff"/>
  <metadata><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"/></metadata>
  <defs id="defs1">
    <linearGradient id="base-grad"><stop offset="0" style="stop-color:#ff0000;stop-opacity:1"/><stop offset="1" stop-color="rgb(0,0,255)"/></linearGradient>
    <linearGradient id="used-grad" xlink:href="#base-grad" x1="10" y1="0" x2="90" y2="0" gradientUnits="userSpaceOnUse"/>
    <linearGradient id="unused-grad"><stop offset="0" stop-color="#00ff00"/></linearGradient>
    <clipPath id="clip"><circle cx="150" cy="60" r="30"/></clipPath>
    <symbol id="dot" viewBox="0 0 10 10"><circle cx="5" cy="5" r="5"/></symbol>
    <path id="unused-path" d="M0 0h10v10z"/>
  </defs>
  <g id="layer1" inkscape:label="Layer 1">
    <g transform="translate(10.000000,10.000000)"><rect x="0.000000" y="0.000000" width="80.000000" height="40.000000" style="fill:url(#used-grad);fill-opacity:1;stroke:none;stroke-width:1"/></g>
    <g style="fill:#000000"><g transform="translate(0,60) scale(2)"><path d="M 5.000000,5.000000 L 25.000000,5.000000 L 25.000000,20.000000 L 5.000000,20.000000 Z"/></g></g>
    <rect x="120" y="30" width="60" height="60" fill="#FFA500" clip-path="url(#clip)"/>
    <use xlink:href="#dot" x="100" y="100" width="10" height="10" fill="#008000"/>
    <polygon points="60,70 90,70 75,100" fill="white" stroke="black" stroke-width="2"/>
    <line x1="100" y1="10" x2="190" y2="10" stroke="#333333" stroke-width="3" stroke-linecap="round"/>
    <path d="M 100 20 L 110 20 L 110 25 Z" fill="#336699"/><path d="M 130 20 L 140 20 L 140 25 Z" fill="#336699"/>
    <text x="100" y="115" font-family="Arial" font-size="10" fill="#222">  Keep  spaces  </text>
    <g></g>
  </g>
</svg>
'@
$r = Run @('image', $tricky, '--to', 'svg', '--out', (Join-Path $dir 'tricky.min.svg'), '--force')
$min = Get-Content -LiteralPath (Join-Path $dir 'tricky.min.svg') -Raw
Require (-not $r.details.kept -and $r.outputBytes -lt $r.inputBytes * 0.7) "tricky size: $($r | ConvertTo-Json -Compress -Depth 5)"
# The default keeps what other files and page CSS can use: ids (with their definitions), style attributes and
# explicit initial values.
foreach ($keep in 'id="unused-grad"', 'id="unused-path"', 'id="layer1"', 'stroke-width:1', '<rect') { Require ($min.Contains($keep)) "Default optimization must keep: $keep" }
foreach ($scale in 1, 4) { Require ((Mismatch $tricky (Join-Path $dir 'tricky.min.svg') $scale) -eq 0) "tricky default optimized differs at ${scale}x" }
$r = Run @('image', $tricky, '--to', 'svg', '--aggressive', '--out', (Join-Path $dir 'tricky.min.svg'), '--force')
$min = Get-Content -LiteralPath (Join-Path $dir 'tricky.min.svg') -Raw
Require (-not $r.details.kept -and $r.outputBytes -lt $r.inputBytes * 0.6) "tricky --aggressive size: $($r | ConvertTo-Json -Compress -Depth 5)"
foreach ($keep in '<title>Tricky test drawing</title>', '<desc>', 'viewBox="0 0 200 120"', 'id="used-grad"', 'xlink:href="#base-grad"', 'clip-path="url(#clip)"', '<symbol id="dot"', '>  Keep  spaces  </text>', 'fill="orange"', 'stroke-linecap="round"') {
    Require ($min.Contains($keep)) "Optimized SVG lost: $keep"
}
foreach ($gone in 'inkscape', 'sodipodi', 'metadata', '<!--', 'unused-grad', 'unused-path', 'enable-background', 'layer1', '<g', 'stroke-width:1', '.000', ' />') {
    Require (-not $min.Contains($gone)) "Optimized SVG still has: $gone"
}
foreach ($scale in 1, 4) { Require ((Mismatch $tricky (Join-Path $dir 'tricky.min.svg') $scale) -eq 0) "tricky optimized differs at ${scale}x" }
# A stylesheet keeps the structure (it can select by element and position) and the style attributes (they beat it).
$styled = Write-Svg 'styled.svg' ((Get-Content -LiteralPath $tricky -Raw).Replace('<title>', '<style>rect { stroke: purple; opacity: .5 }</style><title>').Replace('stroke-width:1"', 'stroke-width:1;opacity:1"'))
$null = Run @('image', $styled, '--to', 'svg', '--out', (Join-Path $dir 'styled.min.svg'), '--force')
$min = Get-Content -LiteralPath (Join-Path $dir 'styled.min.svg') -Raw
Require ($min.Contains('<style>rect { stroke: purple; opacity: .5 }</style>') -and $min.Contains('<rect') -and $min.Contains('stroke:none') -and $min.Contains('opacity:1"') -and $min.Contains('unused-grad')) "styled optimize: $min"
$null = Run @('image', $styled, '--to', 'svg', '--remove-ids', '--out', (Join-Path $dir 'styled.noids.svg'), '--force')
$min = Get-Content -LiteralPath (Join-Path $dir 'styled.noids.svg') -Raw
Require (-not $min.Contains('unused-grad') -and -not $min.Contains('unused-path') -and $min.Contains('stroke:none') -and $min.Contains('id="used-grad"')) "--remove-ids: $min"
Require ((Mismatch $styled (Join-Path $dir 'styled.min.svg') 4) -eq 0) 'styled optimized differs'
$null = Run @('image', $tricky, '--to', 'svg', '--strip-title', '--out', (Join-Path $dir 'tricky.notitle.svg'), '--force')
$min = Get-Content -LiteralPath (Join-Path $dir 'tricky.notitle.svg') -Raw
Require (-not $min.Contains('<title>') -and -not $min.Contains('<desc>')) '--strip-title must remove title and desc'
# A plain DOCTYPE (old editors) is fine; the gate keeps the original when a setting would change the drawing.
$doctype = Write-Svg 'doctype.svg' ('<?xml version="1.0"?><!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">' + (Get-Content -LiteralPath $stroke -Raw))
$r = Run @('image', $doctype, '--to', 'svg', '--out', (Join-Path $dir 'doctype.min.svg'), '--force')
Require (-not $r.details.kept -and -not (Get-Content -LiteralPath (Join-Path $dir 'doctype.min.svg') -Raw).Contains('DOCTYPE')) "DOCTYPE SVG: $($r | ConvertTo-Json -Compress -Depth 5)"
$r = Run @('image', $flat, '--to', 'svg', '--precision', '0', '--out', (Join-Path $dir 'flat.p0.svg'), '--force')
Require ($r.details.kept -eq 'original' -and $r.details.reason -match 'changes .*% of the pixels; try a higher --precision' -and $r.outputBytes -eq $r.inputBytes) "Gate: $($r | ConvertTo-Json -Compress -Depth 5)"
Require ((Get-FileHash -LiteralPath (Join-Path $dir 'flat.p0.svg')).Hash -eq (Get-FileHash -LiteralPath $flat).Hash) 'The gate must write the original bytes'
# Animation (here a path morph, which needs the same commands in every frame) keeps every attribute value as written.
$morph = Write-Svg 'morph.svg' '<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40"><!-- note --><path fill="#FF0000" d="M 0.000 0.000 L 40.000 0.000 L 20.000 40.000 Z"><animate attributeName="d" dur="2s" repeatCount="indefinite" values="M 0.000 0.000 L 40.000 0.000 L 20.000 40.000 Z;M 0.000 40.000 L 40.000 40.000 L 20.000 0.000 Z"/></path></svg>'
$null = Run @('image', $morph, '--to', 'svg', '--out', (Join-Path $dir 'morph.min.svg'), '--force')
$min = Get-Content -LiteralPath (Join-Path $dir 'morph.min.svg') -Raw
Require ($min.Contains('fill="#FF0000" d="M 0.000 0.000 L 40.000 0.000 L 20.000 40.000 Z"') -and -not $min.Contains('note')) "Animated SVG: $min"
# Sprites: other files show the definitions by id (<use href="icons.svg#icon-home">, <img src="icons.svg#icon-a">).
$sprite = Write-Svg 'sprite-defs.svg' '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><defs><linearGradient id="brand-gradient"><stop offset="0" stop-color="#05f"/><stop offset="1" stop-color="#f05"/></linearGradient><path id="icon-close" d="M4 4L20 20M20 4L4 20" stroke="#000" stroke-width="2"/><g id="icon-home"><path d="M3 12L12 3L21 12V21H3Z"/></g><clipPath id="round-clip"><circle cx="12" cy="12" r="10"/></clipPath></defs><rect id="preview" x="2" y="2" width="20" height="20" fill="#ccc"/></svg>'
$stack = Write-Svg 'stack-sprite.svg' '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><style>svg .icon{display:none} svg .icon:target{display:inline}</style><svg id="icon-a" class="icon" viewBox="0 0 24 24"><circle cx="12" cy="12" r="8" fill="red"/></svg><svg id="icon-b" class="icon" viewBox="0 0 24 24"><rect x="4" y="4" width="16" height="16" fill="blue"/></svg><rect x="0" y="0" width="2" height="2" fill="#000"/></svg>'
foreach ($case in @(@($sprite, @('brand-gradient', 'icon-close', 'icon-home', 'round-clip', 'preview')), @($stack, @('icon-a', 'icon-b')))) {
    $out = [IO.Path]::ChangeExtension($case[0], '.min.svg')
    $null = Run @('image', $case[0], '--to', 'svg', '--out', $out, '--force')
    $min = Get-Content -LiteralPath $out -Raw
    foreach ($id in $case[1]) { Require ($min.Contains("id=`"$id`"")) "Sprite lost $id : $min" }
    Require ($min.Contains('M4 4 20 20') -or $min.Contains('class="icon"')) "Sprite content changed: $min"
}
# Page CSS on an inline SVG: by default style attributes and explicit black stay; --aggressive changes them.
$inline = Write-Svg 'inline.svg' '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" class="icon"><circle cx="12" cy="12" r="10" style="fill:#ffcc00"/><circle cx="8" cy="10" r="2" fill="#000"/></svg>'
$null = Run @('image', $inline, '--to', 'svg', '--out', (Join-Path $dir 'inline.min.svg'), '--force')
$min = Get-Content -LiteralPath (Join-Path $dir 'inline.min.svg') -Raw
Require ($min.Contains('style="fill:#ffcc00"') -and $min.Contains('fill="#000"')) "Default inline: $min"
$null = Run @('image', $inline, '--to', 'svg', '--aggressive', '--out', (Join-Path $dir 'inline.agg.svg'), '--force')
$min = Get-Content -LiteralPath (Join-Path $dir 'inline.agg.svg') -Raw
Require (-not $min.Contains('style=') -and -not $min.Contains('fill="#000"')) "--aggressive inline: $min"
# transform-origin and transform-box keep the transform where it is; so does a path that a <use> strokes.
$origin = Write-Svg 'origin.svg' '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24"><path fill="#1565c0" transform="rotate(45)" transform-origin="12 12" d="M8 2h8v20H8z"/><g transform="scale(.5)"><path fill="#c62828" transform="rotate(90)" transform-origin="12 12" d="M2 2h20v4H2z"/></g><g transform="scale(.5)"><path fill="#2e7d32" transform="rotate(30)" style="transform-box:fill-box;transform-origin:center" d="M8 2h8v20H8z"/></g></svg>'
$null = Run @('image', $origin, '--to', 'svg', '--aggressive', '--out', (Join-Path $dir 'origin.min.svg'), '--force')
$min = Get-Content -LiteralPath (Join-Path $dir 'origin.min.svg') -Raw
Require ($min.Contains('transform="rotate(45)" transform-origin="12 12"') -and ([regex]::Matches($min, 'transform="scale\(.5\)"')).Count -eq 2 -and $min.Contains('transform="rotate(90)"') -and $min.Contains('transform-box:fill-box')) "transform-origin: $min"
$useStroke = Write-Svg 'use-stroke.svg' '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100"><defs><path id="shape" d="M10 10 H40 V40 H10 Z" transform="scale(2 1)"/></defs><use href="#shape" stroke="#000" stroke-width="4" fill="none"/></svg>'
$null = Run @('image', $useStroke, '--to', 'svg', '--aggressive', '--out', (Join-Path $dir 'use-stroke.min.svg'), '--force')
Require ((Get-Content -LiteralPath (Join-Path $dir 'use-stroke.min.svg') -Raw).Contains('transform="scale(2 1)"')) 'A path shown by a stroked <use> must keep its transform'
# An arc with radii too small to reach its end point is drawn larger: it must not be merged with a path it overlaps.
$arc = Write-Svg 'arc.svg' '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100" height="100"><path fill="#000" fill-opacity=".5" d="M10 50 A0.001 0.001 0 0 1 90 50"/><path fill="#000" fill-opacity=".5" d="M40 20 H60 V45 H40 Z"/></svg>'
$r = Run @('image', $arc, '--to', 'svg', '--out', (Join-Path $dir 'arc.min.svg'), '--force')
Require (-not $r.details.kept -and ([regex]::Matches((Get-Content -LiteralPath (Join-Path $dir 'arc.min.svg') -Raw), '<path ')).Count -eq 2) "Small-radius arc: $($r | ConvertTo-Json -Compress -Depth 5)"
Write-Host 'ok   image SVG optimize: editor data, unused defs, groups, colours, defaults go; ids, sprites, style attributes stay by default (--aggressive, --remove-ids); title, desc, refs, text stay; stylesheet safe; transform-origin; gate keeps the original; animation untouched'

# ---- Batches: --to ico and --to svg over a folder of PNG, JPEG and SVG. ----
$in = Join-Path $dir 'batch-in'; New-Item -ItemType Directory -Force $in | Out-Null
Copy-Item (Join-Path $dir 'logo.png') $in; Copy-Item $tricky $in
$null = Run @('image', (Join-Path $dir 'logo.png'), '--to', 'jpeg', '--out', (Join-Path $in 'photo.jpg'), '--force')
$b = Run @('image', 'batch', $in, '--to', 'ico', '--out', (Join-Path $dir 'batch-ico'), '--force')
Require ($b.failed -eq 0 -and @($b.results).Count -eq 3 -and @(Get-ChildItem (Join-Path $dir 'batch-ico') -Filter *.ico).Count -eq 3) "batch ico: $($b | ConvertTo-Json -Compress -Depth 6)"
foreach ($f in Get-ChildItem (Join-Path $dir 'batch-ico') -Filter *.ico) { $null = Harness @('ico', $f.FullName, '16,32,48') }
$b = Run @('image', 'batch', $in, '--to', 'svg', '--out', (Join-Path $dir 'batch-svg'), '--force')
$modes = @($b.results | ForEach-Object { if ($_.details.mode) { $_.details.mode } else { 'optimize' } }) -join ','
Require ($b.failed -eq 0 -and $modes -eq 'trace,trace,optimize') "batch svg: $modes $($b | ConvertTo-Json -Compress -Depth 6)"
# In a batch, tracing options apply to the rasters; the SVG is optimized without them and says so.
$b = Run @('image', 'batch', $in, '--to', 'svg', '--colors', '3', '--out', (Join-Path $dir 'batch-svg3'), '--force')
$svgResult = @($b.results | Where-Object { $_.inputFormat -eq 'svg' })[0]
Require ($b.failed -eq 0 -and $svgResult.details.note -match 'raster input only' -and @($b.results | Where-Object { $_.details.mode -eq 'trace' }).Count -eq 2) "batch svg --colors: $($b | ConvertTo-Json -Compress -Depth 6)"
Write-Host 'ok   image batch: --to ico and --to svg (trace rasters, optimize SVG; tracing options noted as not used for SVG)'

# ---- Refusals. ----
$png = Join-Path $dir 'logo.png'
Refuse @('image', $png, '--to', 'ico', '--sizes', '16,abc') 'whole numbers'
Refuse @('image', $png, '--to', 'ico', '--sizes', '16,,32') 'whole numbers'
Refuse @('image', $png, '--to', 'ico', '--sizes', '16,16') 'twice'
Refuse @('image', $png, '--to', 'ico', '--sizes', '16,300') 'outside 1-256'
Refuse @('image', $png, '--to', 'ico', '--sizes', '0') 'outside 1-256'
Refuse @('image', $png, '--sizes', '16') 'needs --to ico'
Refuse @('image', $png, '--to', 'ico', '--max-width', '64') '--sizes sets'
Refuse @('image', $png, '--to', 'png', '--colors', '3') 'only applies to tracing'
Refuse @('image', $png, '--to', 'svg', '--mode', 'embed', '--tolerance', '1') 'only applies to tracing'
Refuse @('image', $png, '--to', 'svg', '--mode', 'vector') 'trace or embed'
Refuse @('image', $png, '--to', 'png', '--mode', 'embed') 'needs --to svg'
Refuse @('image', $png, '--to', 'svg', '--tolerance', '9') 'between 0.1 and 5'
Refuse @('image', $png, '--to', 'gif') 'png, jpeg, webp, svg or ico'
Refuse @('image', $app) 'ICO input needs --to'
Refuse @('image', $tricky, '--to', 'svg', '--colors', '3') 'applies to raster input'
Refuse @('image', $tricky, '--to', 'svg', '--mode', 'embed') 'applies to raster input'
Refuse @('image', $png, '--to', 'svg', '--aggressive') 'only applies to SVG input'
Refuse @('image', $png, '--to', 'png', '--remove-ids') 'only applies to SVG optimization'
$empty = Join-Path $dir 'empty'; New-Item -ItemType Directory -Force $empty | Out-Null
Refuse @('image', 'batch', $empty, '--to', 'ico') 'No images'
Refuse @('image', 'batch', (Join-Path $dir 'missing'), '--to', 'ico') 'Folder not found'
[IO.File]::WriteAllBytes((Join-Path $dir 'cut.ico'), [byte[]](0, 0, 1, 0, 2, 0, 16, 16))
Refuse @('image', (Join-Path $dir 'cut.ico'), '--to', 'png') 'cut off'
[IO.File]::WriteAllBytes((Join-Path $dir 'notico.ico'), [byte[]](0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0))
Refuse @('image', (Join-Path $dir 'notico.ico'), '--to', 'png') 'Not an ICO'
$clear = Write-Svg 'clear.svg' '<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><rect width="10" height="10" fill-opacity="0.1"/></svg>'
$null = Run @('image', $clear, '--to', 'png', '--out', (Join-Path $dir 'clear.png'), '--force')
Refuse @('image', (Join-Path $dir 'clear.png'), '--to', 'svg') 'nothing to trace'
Write-Host 'ok   image refusals: size lists, option combinations, empty and missing folders, broken ICO, blank image'

# ---- Browsers, when installed: Chrome shows the ICO frames and draws optimized and traced SVGs like the originals. ----
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
if (Test-Path -LiteralPath $chrome) {
    function DataUri([string]$Path, [string]$Type) { "data:$Type;base64," + [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) }
    $pairs = @(@($tricky, (Join-Path $dir 'tricky.min.svg')), @((Join-Path $dir 'flat.svg'), (Join-Path $dir 'flat.trace.svg')), @((Join-Path $dir 'holes.svg'), (Join-Path $dir 'holes.trace.svg'))) |
        ForEach-Object { "['$(DataUri $_[0] 'image/svg+xml')','$(DataUri $_[1] 'image/svg+xml')']" }
    $html = @"
<html><body>WAIT<script>
const load=s=>new Promise((ok,no)=>{const i=new Image();i.onload=()=>ok(i);i.onerror=()=>no(s.slice(0,30));i.src=s;});
const draw=(i,w,h)=>{const c=document.createElement('canvas');c.width=w;c.height=h;const x=c.getContext('2d');x.fillStyle='#fff';x.fillRect(0,0,w,h);x.drawImage(i,0,0,w,h);return x.getImageData(0,0,w,h).data;};
(async()=>{try{const out=[];
const app=await load('$(DataUri $app 'image/x-icon')'), fav=await load('$(DataUri $favicon 'image/x-icon')');
out.push('ico '+app.naturalWidth+' '+fav.naturalWidth);
for(const [a,b] of [$($pairs -join ',')]){const A=await load(a),B=await load(b);const w=A.naturalWidth*2,h=A.naturalHeight*2;const p=draw(A,w,h),q=draw(B,w,h);let bad=0;
for(let i=0;i<p.length;i+=4)if(Math.max(Math.abs(p[i]-q[i]),Math.abs(p[i+1]-q[i+1]),Math.abs(p[i+2]-q[i+2]))>64)bad++;out.push((100*bad/(w*h)).toFixed(3));}
document.body.textContent='RESULT '+out.join(' ')+' END';}catch(e){document.body.textContent='RESULT FAIL '+e+' END';}})();
</script></body></html>
"@
    $page = Join-Path $dir 'browser.html'; [IO.File]::WriteAllText($page, $html)
    $profile = Join-Path $dir ('chrome-' + [Guid]::NewGuid().ToString('N'))
    $browserOut = Join-Path $dir 'browser-result.txt'
    $browserArgs = @('--headless', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--disable-background-networking', '--dump-dom', '--virtual-time-budget=15000', "--user-data-dir=`"$profile`"", ('"file:///' + ($page -replace '\\', '/') + '"'))
    $browser = Start-Process -FilePath $chrome -ArgumentList $browserArgs -WindowStyle Hidden -PassThru -RedirectStandardOutput $browserOut -RedirectStandardError (Join-Path $dir 'browser-stderr.txt')
    if (-not $browser.WaitForExit(30000)) { $browser.Kill($true); Write-Host 'Browser check skipped: headless Chrome timed out.' }
    else {
        $result = (Get-Content -LiteralPath $browserOut -Raw) -replace '(?s).*RESULT (.*?) END.*', '$1'
        $parts = $result -split ' '
        # Chrome picks the largest ICO frame for <img>; tricky must match exactly, traced shapes like the Skia check.
        Require ($parts[0] -eq 'ico' -and $parts[1] -eq '256' -and $parts[2] -eq '48' -and [double]$parts[3] -le 0.05 -and [double]$parts[4] -le 0.5 -and [double]$parts[5] -le 0.5) "Chrome: $result"
        Write-Host "ok   image Chrome: ICO frames 256/48, optimized and traced SVGs draw the same ($result)"
    }
} else { Write-Host 'Chrome check skipped: Chrome is not installed.' }
