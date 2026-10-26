# oml setup: the Explorer right-click entries and the actions they call.
# Registry: every install/status/remove here uses a throwaway root HKCU\Software\oml-setup-test-<id> (--registry-root),
# never the person's real HKCU\Software\Classes. A snapshot of real Classes keys before and after proves nothing else changed.
# Actions: `oml setup run ... --no-ui` does the work and prints what it would open, so no window or message box appears.
# Runs in the alone lane: it writes to HKCU.
param([string]$Oml = "$PSScriptRoot\..\src\bin\Release\net10.0\oml.exe", [string]$Tmp = "$PSScriptRoot\..\tmp")
$ErrorActionPreference = 'Stop'
$Tmp = [IO.Path]::GetFullPath($Tmp)
New-Item -ItemType Directory -Force $Tmp | Out-Null
function Require($condition, $message) { if (!$condition) { throw $message } }
$hkcu = [Microsoft.Win32.Registry]::CurrentUser

# Every value and subkey under an HKCU-relative path, as text, so two states can be compared.
function Dump([string]$Path) {
    $key = $hkcu.OpenSubKey($Path)
    if (-not $key) { return "[$Path] absent" }
    try {
        $lines = @("[$Path]") + @($key.GetValueNames() | Sort-Object | ForEach-Object { "$_=$($key.GetValue($_, $null, 'DoNotExpandEnvironmentNames'))" })
        foreach ($name in ($key.GetSubKeyNames() | Sort-Object)) { $lines += Dump "$Path\$name" }
        return $lines -join "`n"
    } finally { $key.Dispose() }
}
# The real keys oml setup would touch: every file type it uses, Directory (with Directory\Background), and the subkey
# names of Classes and SystemFileAssociations (a key added or removed next to them).
function Names([string]$Path) { $key = $hkcu.OpenSubKey($Path); if (-not $key) { return "[$Path] absent" }; try { "[$Path] " + (($key.GetSubKeyNames() | Sort-Object) -join '|') } finally { $key.Dispose() } }
$extensions = '.ttf', '.otf', '.woff', '.png', '.jpg', '.jpeg', '.webp', '.gif', '.bmp', '.svg', '.oml'
function Real-State {
    $parts = @($extensions | ForEach-Object { Dump "Software\Classes\SystemFileAssociations\$_" }) + (Dump 'Software\Classes\Directory')
    $parts += Names 'Software\Classes\SystemFileAssociations'
    $parts += Names 'Software\Classes'
    return $parts -join "`n"
}
$realBefore = Real-State

# oml.exe from a folder with spaces, to check the quoting of the registered commands.
$bin = Join-Path $Tmp 'oml bin with spaces'
Remove-Item -LiteralPath $bin -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Split-Path ([IO.Path]::GetFullPath($Oml)) -Parent) $bin -Recurse -Force
$exe = Join-Path $bin 'oml.exe'

$id = [guid]::NewGuid().ToString('N')
$root = "Software\oml-setup-test-$id"
$hkcu.CreateSubKey($root).Dispose()
function Setup([string[]]$More) {
    $text = & $exe setup --registry-root $root @More
    return [pscustomobject]@{ Code = $LASTEXITCODE; Json = ($text | ConvertFrom-Json) }
}

# The expected entries, written out here on purpose (not computed from the code under test).
$expected = [Collections.Generic.List[object]]::new()
foreach ($ext in '.ttf', '.otf', '.woff') { $expected.Add(@("SystemFileAssociations\$ext\shell", 'oml.font.woff2', 'oml: Convert to WOFF2', 'font', '%1')) }
foreach ($ext in '.png', '.jpg', '.jpeg', '.webp', '.gif', '.bmp', '.svg') { $expected.Add(@("SystemFileAssociations\$ext\shell", 'oml.image.optimize', 'oml: Optimize image', 'image', '%1')) }
foreach ($ext in '.png', '.jpg', '.jpeg', '.webp', '.gif', '.bmp', '.svg') { $expected.Add(@("SystemFileAssociations\$ext\shell", 'oml.image.ico', 'oml: Convert to ICO', 'ico', '%1')) }
foreach ($ext in '.png', '.jpg', '.jpeg', '.webp', '.gif', '.bmp') { $expected.Add(@("SystemFileAssociations\$ext\shell", 'oml.image.trace', 'oml: Trace to SVG', 'trace', '%1')) }
$expected.Add(@('SystemFileAssociations\.oml\shell', 'oml.openapi', 'oml: OpenAPI', 'openapi', '%1'))
$expected.Add(@('SystemFileAssociations\.oml\shell', 'oml.dashboard', 'oml: Open in dashboard', 'dashboard', '%1'))
foreach ($p in @(@('Directory\shell', '%1'), @('Directory\Background\shell', '%V'))) {
    $expected.Add(@($p[0], 'oml.folder.fonts', 'oml: Convert all fonts to WOFF2', 'fonts', $p[1]))
    $expected.Add(@($p[0], 'oml.folder.images', 'oml: Optimize all images', 'images', $p[1]))
    $expected.Add(@($p[0], 'oml.folder.architecture', 'oml: Architecture report', 'architecture', $p[1]))
}

try {
    # Install: every key, label, marker and command with the quoted exe path.
    $r = Setup @()
    Require ($r.Code -eq 0 -and @($r.Json.installed).Count -eq $expected.Count) "setup install: exit $($r.Code) $($r.Json | ConvertTo-Json -Compress)"
    foreach ($e in $expected) {
        $key = $hkcu.OpenSubKey("$root\$($e[0])\$($e[1])")
        Require $key "Missing key $($e[0])\$($e[1])"
        try {
            Require ($key.GetValue('') -eq $e[2]) "Label of $($e[0])\$($e[1]): $($key.GetValue(''))"
            Require ($key.GetValue('OmlSetup') -eq 'oml-cli') "Marker missing on $($e[0])\$($e[1])"
            Require ($key.GetValue('MultiSelectModel') -eq 'Single') "MultiSelectModel on $($e[0])\$($e[1])"
            $command = $key.OpenSubKey('command').GetValue('')
            $want = "`"$exe`" setup run $($e[3]) `"$($e[4])`""
            Require ($command -ceq $want) "Command of $($e[0])\$($e[1]): $command (want $want)"
        } finally { $key.Dispose() }
    }
    Require (@($hkcu.OpenSubKey("$root\Directory\shell").GetSubKeyNames()).Count -eq 3) 'Unexpected extra keys under Directory\shell'
    Write-Host "ok   setup: $($expected.Count) entries, labels, markers, quoted commands with spaces in the exe path"

    # Status.
    $s = Setup @('--status')
    Require ($s.Code -eq 0 -and $s.Json.installed -eq $expected.Count -and $s.Json.total -eq $expected.Count) "status after install: $($s.Json | ConvertTo-Json -Compress)"
    Require (@($s.Json.entries | Where-Object state -ne 'installed').Count -eq 0) 'status: not every entry is installed'
    # Another oml.exe (moved or a second copy) shows as "other oml.exe".
    $other = & $Oml setup --status --registry-root $root | ConvertFrom-Json
    Require (@($other.entries | Where-Object state -eq 'other oml.exe').Count -eq $expected.Count) 'status from another oml.exe must say "other oml.exe"'

    # A second install changes nothing.
    $installed = Dump $root
    $r = Setup @()
    Require ($r.Code -eq 0) "second install exit $($r.Code)"
    Require ((Dump $root) -ceq $installed) 'second install changed the registry'
    # A marked entry without its command shows as "incomplete"; oml setup repairs it.
    $hkcu.DeleteSubKeyTree("$root\SystemFileAssociations\.gif\shell\oml.image.optimize\command")
    $s = Setup @('--status')
    Require (@($s.Json.entries | Where-Object state -eq 'incomplete').Count -eq 1 -and $s.Json.installed -eq $expected.Count - 1) "status with a missing command: $($s.Json | ConvertTo-Json -Compress)"
    $r = Setup @()
    Require ($r.Code -eq 0 -and (Dump $root) -ceq $installed) 'oml setup did not repair the incomplete entry'
    # An exe path with '%' could turn into an Explorer placeholder: refused, nothing written.
    $percentBin = Join-Path $Tmp 'oml 100% bin'
    Remove-Item -LiteralPath $percentBin -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item $bin $percentBin -Recurse
    $bad = & (Join-Path $percentBin 'oml.exe') setup --registry-root $root | ConvertFrom-Json
    Require ($LASTEXITCODE -eq 2 -and $bad.error -match "contains '%'" -and (Dump $root) -ceq $installed) "exe path with %: $($bad | ConvertTo-Json -Compress)"
    Write-Host 'ok   setup: --status, other oml.exe, incomplete entry repaired, idempotent second install, % in the exe path refused'

    # The registered command runs as Explorer starts it: the command line with the path put in, given to CreateProcess
    # (no shell), so &, %, ', commas, semicolons, equals signs and non-ASCII letters in names must pass as they are.
    $files = Join-Path $Tmp 'setup files'
    New-Item -ItemType Directory -Force $files | Out-Null
    $font = Get-ChildItem "$env:WINDIR\Fonts" -Filter *.ttf | Select-Object -First 1
    Require $font 'A Windows .ttf font is required'
    $ttf = Join-Path $files 'My Font.ttf'
    Copy-Item -LiteralPath $font.FullName $ttf
    $env:OML_SETUP_LOG = Join-Path $Tmp 'setup logs\setup.log'
    # --no-ui goes right after the action, so the path stays the last argument, as in the real entry.
    function Run-Registered([string]$Verb, [string]$Placeholder, [string]$Path) {
        $command = $hkcu.OpenSubKey("$root\$Verb\command").GetValue('')
        Require ($command -match '^"([^"]+)" (setup run \w+) (.*)$') "Unexpected command shape: $command"
        $start = [Diagnostics.ProcessStartInfo]::new($Matches[1], "$($Matches[2]) --no-ui $($Matches[3].Replace($Placeholder, $Path))")
        $start.RedirectStandardOutput = $true; $start.UseShellExecute = $false
        $process = [Diagnostics.Process]::Start($start)
        $stdout = $process.StandardOutput.ReadToEnd(); $process.WaitForExit()
        Require $stdout "No JSON from: $($start.FileName) $($start.Arguments) (exit $($process.ExitCode))"
        $json = $stdout | ConvertFrom-Json
        $json | Add-Member Code $process.ExitCode
        return $json
    }
    $run = Run-Registered 'SystemFileAssociations\.ttf\shell\oml.font.woff2' '%1' $ttf
    Require ($run.Code -eq 0 -and $run.ok -and $run.output -eq (Join-Path $files 'My Font.woff2')) "Registered font command -> $($run | ConvertTo-Json -Compress)"
    Require ($run.open.kind -eq 'select' -and $run.open.path -eq $run.output -and (Test-Path -LiteralPath $run.output)) 'Font action must select the new file'
    $odd = Join-Path $Tmp 'setup & 100% %USERNAME% folder'
    New-Item -ItemType Directory -Force $odd | Out-Null
    $oddName = "a&b 100% 'q' ,;= $([char]0xFC)$([char]0x65E5)$([char]0x672C)"
    Copy-Item -LiteralPath $ttf (Join-Path $odd "$oddName.ttf")
    $run = Run-Registered 'SystemFileAssociations\.ttf\shell\oml.font.woff2' '%1' (Join-Path $odd "$oddName.ttf")
    # Stdout uses the console code page, so the non-ASCII name is checked on disk, not in the JSON.
    Require ($run.Code -eq 0 -and $run.ok -and (Test-Path -LiteralPath (Join-Path $odd "$oddName.woff2"))) "Font with &, %, quote, ,;= and non-ASCII in its name: $($run | ConvertTo-Json -Compress)"
    # The empty space in an open folder passes %V; for a drive root it ends with a backslash ("D:\"), which the
    # command line reads as an escaped quote. The action must still get the folder.
    $empty = Join-Path $Tmp 'setup empty'
    New-Item -ItemType Directory -Force $empty | Out-Null
    $run = Run-Registered 'Directory\Background\shell\oml.folder.fonts' '%V' "$empty\"
    Require ($run.Code -eq 0 -and $run.ok -and $run.message -match '^No fonts' -and $run.input.TrimEnd('\') -eq $empty) "Folder path with a trailing backslash: $($run | ConvertTo-Json -Compress)"
    $run = Run-Registered 'Directory\shell\oml.folder.fonts' '%1' $odd
    Require ($run.Code -eq 0 -and $run.ok -and (Test-Path -LiteralPath (Join-Path $odd "woff2\$oddName.woff2"))) "Folder with & and % in its name: $($run | ConvertTo-Json -Compress)"
    Write-Host 'ok   setup: registered command lines run like Explorer (CreateProcess) with spaces, &, %, quotes, ,;=, non-ASCII and a trailing backslash'

    # Remove: only oml's keys, then the parents it created; nothing left under the root.
    $r = Setup @('--remove')
    Require ($r.Code -eq 0 -and @($r.Json.removed).Count -eq $expected.Count -and -not $r.Json.skipped) "remove: $($r.Json | ConvertTo-Json -Compress)"
    $left = $hkcu.OpenSubKey($root)
    Require ($left.SubKeyCount -eq 0 -and $left.ValueCount -eq 0) "remove left keys: $(Dump $root)"
    $left.Dispose()
    $s = Setup @('--status')
    Require ($s.Json.installed -eq 0 -and @($s.Json.entries | Where-Object state -ne 'missing').Count -eq 0) 'status after remove'
    Write-Host 'ok   setup: --remove leaves nothing under the root'

    # A key with oml's verb name that oml did not make: install refuses and writes nothing; remove leaves it.
    $foreign = $hkcu.CreateSubKey("$root\Directory\shell\oml.folder.fonts")
    $foreign.SetValue('', 'someone else'); $foreign.Dispose()
    $beforeRefusal = Dump $root
    $r = Setup @()
    Require ($r.Code -eq 1 -and $r.Json.error -match 'oml did not make' -and $r.Json.error -match [regex]::Escape("oml-setup-test-$id\Directory\shell\oml.folder.fonts")) "foreign key refusal: exit $($r.Code) $($r.Json | ConvertTo-Json -Compress)"
    Require ((Dump $root) -ceq $beforeRefusal) 'A refused install changed the registry'
    $s = Setup @('--status')
    Require (@($s.Json.entries | Where-Object state -eq 'not made by oml').Count -eq 1) 'status must report the foreign key'
    $r = Setup @('--remove')
    Require ($r.Code -eq 0 -and $r.Json.skipped.Count -eq 1 -and $r.Json.skipped[0] -match 'oml\.folder\.fonts' -and -not $r.Json.removed) "remove with a foreign key: $($r.Json | ConvertTo-Json -Compress)"
    Require ((Dump $root) -ceq $beforeRefusal) 'remove changed a key oml did not make'
    $hkcu.DeleteSubKeyTree("$root\Directory")
    Write-Host 'ok   setup: refuses and keeps a key with an oml name that oml did not make'

    # Keys that existed before stay as they were; a parent oml created stays when another program added a key under it.
    $pre = $hkcu.CreateSubKey("$root\Directory"); $pre.SetValue('Keep', 'yes'); $pre.Dispose()
    $pre = $hkcu.CreateSubKey("$root\SystemFileAssociations\.png\shell\other.verb"); $pre.SetValue('', 'Other program'); $pre.Dispose()
    $directoryBefore = Dump "$root\Directory"
    $pngBefore = Dump "$root\SystemFileAssociations\.png"
    $r = Setup @()
    Require ($r.Code -eq 0) "install next to existing keys: $($r.Json | ConvertTo-Json -Compress)"
    Require ($null -eq $hkcu.OpenSubKey("$root\Directory").GetValue('OmlSetup')) 'oml marked a key it did not create'
    $late = $hkcu.CreateSubKey("$root\SystemFileAssociations\.ttf\shell\other.verb"); $late.SetValue('', 'Added later'); $late.Dispose()
    $r = Setup @('--remove')
    Require ($r.Code -eq 0 -and @($r.Json.removed).Count -eq $expected.Count) "remove next to existing keys: $($r.Json | ConvertTo-Json -Compress)"
    Require ((Dump "$root\Directory") -ceq $directoryBefore) "remove changed the existing Directory key: $(Dump "$root\Directory")"
    Require ((Dump "$root\SystemFileAssociations\.png") -ceq $pngBefore) 'remove changed the existing .png keys'
    Require ($hkcu.OpenSubKey("$root\SystemFileAssociations\.ttf\shell\other.verb")) 'remove deleted a key another program added under an oml parent'
    Require ($null -eq ((Dump $root) -split "`n" | Where-Object { $_ -match '\\oml\.' })) "oml keys left: $(Dump $root)"
    Write-Host 'ok   setup: --remove keeps existing parent keys and keys other programs added'

    $r = Setup @('--status', '--remove')
    Require ($r.Code -eq 2) 'two modes must be refused'
    $bad = & $exe setup --registry-root 'Software\Classes\Evil' | ConvertFrom-Json
    Require ($LASTEXITCODE -eq 2 -and $bad.error -match 'tests only') 'a registry root outside the test prefix must be refused'
} finally {
    $hkcu.DeleteSubKeyTree($root, $false)
}
$realAfter = Real-State
Require ($realAfter -ceq $realBefore) 'Real HKCU\Software\Classes keys changed during the check'
Write-Host 'ok   setup: the real HKCU\Software\Classes keys did not change'

# ---- The actions, without UI. ----
function Act([string]$Action, [string]$Path) {
    $text = & $exe setup run $Action $Path --no-ui
    $json = $text | ConvertFrom-Json
    $json | Add-Member Code $LASTEXITCODE
    return $json
}

# Fonts: a new file next to the font, never a replacement.
$second = Act 'font' $ttf
Require ($second.ok -and $second.output -eq (Join-Path $files 'My Font (2).woff2')) "Second font conversion must pick a new name: $($second | ConvertTo-Json -Compress)"
Require ((Get-FileHash -LiteralPath $ttf).Hash -eq (Get-FileHash -LiteralPath $font.FullName).Hash) 'The font input changed'

# Images: WebP for raster, SVG for SVG.
$svg = Join-Path $files 'Icon.svg'
Set-Content -LiteralPath $svg -Encoding utf8 -Value "<?xml version=`"1.0`"?>`n<!-- comment -->`n<svg xmlns=`"http://www.w3.org/2000/svg`" width=`"10`" height=`"10`">`n  <rect x=`"1.123456`" y=`"1`" width=`"8`" height=`"8`" fill=`"#c00`"/>`n</svg>"
$png = Join-Path $files 'Logo.png'
& $exe image $svg --to png --out $png | Out-Null
Require ($LASTEXITCODE -eq 0) 'Could not make the test PNG'
$img = Act 'image' $png
Require ($img.ok -and $img.output -eq (Join-Path $files 'Logo.optimized.webp') -and $img.open.kind -eq 'select') "PNG action: $($img | ConvertTo-Json -Compress)"
$bytes = [IO.File]::ReadAllBytes($img.output)
Require ([Text.Encoding]::ASCII.GetString($bytes, 8, 4) -eq 'WEBP') 'PNG action did not write WebP'
$vec = Act 'image' $svg
Require ($vec.ok -and $vec.output -eq (Join-Path $files 'Icon.optimized.svg') -and (Get-Content -LiteralPath $vec.output -Raw) -notmatch 'comment') "SVG action: $($vec | ConvertTo-Json -Compress)"
$ico = Act 'ico' $png
Require ($ico.ok -and $ico.output -eq (Join-Path $files 'Logo.ico') -and $ico.open.kind -eq 'select') "ICO action: $($ico | ConvertTo-Json -Compress)"
$icoBytes = [IO.File]::ReadAllBytes($ico.output)
Require ($icoBytes[0] -eq 0 -and $icoBytes[2] -eq 1 -and $icoBytes[4] -eq 3) 'ICO action did not write a 3-size icon'
$traced = Act 'trace' $png
Require ($traced.ok -and $traced.output -eq (Join-Path $files 'Logo.svg') -and (Get-Content -LiteralPath $traced.output -Raw) -match '<path') "Trace action: $($traced | ConvertTo-Json -Compress)"
$wrong = Act 'trace' $svg
Require (-not $wrong.ok -and $wrong.message -match 'works on') "Trace on an SVG must be refused: $($wrong | ConvertTo-Json -Compress)"
Write-Host 'ok   setup run: font and image next to the input, new name when the output exists, WebP, SVG, ICO and trace'

# Folder actions: a new subfolder each time; one bad file does not stop the others and is reported.
$batch = Join-Path $Tmp 'setup batch'
New-Item -ItemType Directory -Force $batch | Out-Null
Copy-Item -LiteralPath $ttf (Join-Path $batch 'A.ttf'); Copy-Item -LiteralPath $ttf (Join-Path $batch 'B.ttf')
Copy-Item -LiteralPath $png (Join-Path $batch 'Logo.png'); Copy-Item -LiteralPath $png (Join-Path $batch 'Logo.jpg')
Copy-Item -LiteralPath $svg (Join-Path $batch 'Icon.svg')
[IO.File]::WriteAllBytes((Join-Path $batch 'Broken.gif'), [byte[]](1..64))
$fonts = Act 'fonts' $batch
Require ($fonts.ok -and $fonts.output -eq (Join-Path $batch 'woff2') -and $fonts.open.kind -eq 'folder') "fonts folder: $($fonts | ConvertTo-Json -Compress)"
Require ((@(Get-ChildItem -LiteralPath $fonts.output | ForEach-Object Name) -join ',') -eq 'A.woff2,B.woff2') 'fonts folder outputs'
$again = Act 'fonts' $batch
Require ($again.ok -and $again.output -eq (Join-Path $batch 'woff2 (2)')) 'A second folder conversion must use a new subfolder'
$images = Act 'images' $batch
Require (-not $images.ok -and $images.Code -eq 1 -and $images.message -match '1 of 4 images failed' -and $images.message -match 'Broken\.gif') "images folder with a bad file: $($images | ConvertTo-Json -Compress)"
Require ($images.open.kind -eq 'folder' -and $images.output -eq (Join-Path $batch 'optimized')) 'The images folder must still open with the good results'
Require ((@(Get-ChildItem -LiteralPath $images.output | ForEach-Object Name | Sort-Object) -join ',') -eq 'Icon.svg,Logo (2).webp,Logo.webp') "images folder outputs: $(@(Get-ChildItem -LiteralPath $images.output).Name)"
$none = Act 'fonts' $empty
Require ($none.ok -and $none.message -match '^No fonts' -and -not $none.open) "empty folder: $($none | ConvertTo-Json -Compress)"
$noModules = Act 'architecture' $empty
Require ($noModules.ok -and $noModules.message -match 'No \.oml or \.oap') 'architecture on a folder without modules'
Write-Host 'ok   setup run: folder fonts and images into new subfolders, partial failure reported, empty folders explained'

# .oml actions.
$modules = Join-Path $Tmp 'setup modules'
New-Item -ItemType Directory -Force $modules | Out-Null
Copy-Item (Join-Path $Tmp 'r0.oml') (Join-Path $modules 'Blank App.oml')
$arch = Act 'architecture' $modules
Require ($arch.ok -and $arch.output -eq (Join-Path $modules 'architecture.html') -and $arch.open.kind -eq 'file' -and (Test-Path -LiteralPath $arch.output)) "architecture: $($arch | ConvertTo-Json -Compress)"
$dash = Act 'dashboard' (Join-Path $modules 'Blank App.oml')
Require ($dash.ok -and $dash.open.kind -eq 'dashboard' -and $dash.open.path -eq (Join-Path $modules 'Blank App.oml')) 'dashboard action'
$restModule = Join-Path $Tmp 'out-rest.json.oml'
if (Test-Path $restModule) {
    Copy-Item $restModule (Join-Path $modules 'Rest App.oml')
    $api = Act 'openapi' (Join-Path $modules 'Rest App.oml')
    Require ($api.ok -and $api.output -like (Join-Path $modules '*.openapi.json') -and $api.open.kind -eq 'select' -and (Test-Path -LiteralPath $api.output)) "openapi: $($api | ConvertTo-Json -Compress)"
    $twice = Act 'openapi' (Join-Path $modules 'Rest App.oml')
    Require (-not $twice.ok -and $twice.message -match 'exists') 'openapi must refuse to overwrite'
    Write-Host 'ok   setup run: architecture page, OpenAPI next to the module (refuses to overwrite), dashboard'
} else { throw "Missing $restModule (made by the rest.json unit)" }

# Failures: clear message, exit 1, and a line in the log.
$missing = Act 'font' (Join-Path $files 'Nope.ttf')
Require (-not $missing.ok -and $missing.Code -eq 1 -and $missing.message -match 'not found') 'missing file'
$wrong = Act 'font' $png
Require (-not $wrong.ok -and $wrong.message -match '\.ttf') 'wrong file type'
$unknown = Act 'explode' $png
Require (-not $unknown.ok -and $unknown.message -match 'Unknown action') 'unknown action'
$blank = Act 'font' ''
Require (-not $blank.ok -and $blank.Code -eq 1 -and $blank.message -match 'No file or folder') "empty path: $($blank | ConvertTo-Json -Compress)"
$drive = Act 'architecture' ([IO.Path]::GetPathRoot($Tmp))
Require (-not $drive.ok -and $drive.message -match 'whole drive') "architecture on a drive root: $($drive | ConvertTo-Json -Compress)"
$deep = Join-Path $Tmp 'setup deep'
New-Item -ItemType Directory -Force (Join-Path $deep (@(1..10) -join '\')) | Out-Null
$tooDeep = Act 'architecture' $deep
Require (-not $tooDeep.ok -and $tooDeep.message -match 'too big' -and -not $tooDeep.output) "architecture on a deep folder: $($tooDeep | ConvertTo-Json -Compress)"
$log = Get-Content -LiteralPath $env:OML_SETUP_LOG
Require ($missing.log -eq $env:OML_SETUP_LOG) 'OML_SETUP_LOG not used'
Require (@($log | Where-Object { $_ -match ' font ".*Nope\.ttf" FAILED : File not found' }).Count -eq 1) "log: $($log -join "`n")"
Require (@($log | Where-Object { $_ -match ' font ".*My Font\.ttf" ok -> ' }).Count -eq 2) 'log must have the successful runs'
Write-Host 'ok   setup run: failures exit 1 with a message and a log line'
