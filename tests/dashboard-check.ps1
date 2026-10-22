# oml dashboard, end to end: start it on a free port, check the security rules, call each kind of endpoint, then stop it.
# With Edge or Chrome installed it also takes light and dark screenshots of three pages into $Tmp and checks that the
# page reported no JavaScript error (theme.js counts them on <html data-js-errors>), the same way preview-check reads the DOM.
param([string]$Oml = "$PSScriptRoot\..\src\bin\Release\net8.0\oml.exe", [string]$Tmp = "$PSScriptRoot\..\tmp")
$ErrorActionPreference = 'Stop'
$Tmp = [IO.Path]::GetFullPath($Tmp)
New-Item -ItemType Directory -Force $Tmp | Out-Null
function Require($condition, $message) { if (!$condition) { throw $message } }

# A raw request, so the Host and Origin headers are exactly what the test says.
function Raw([int]$Port, [string]$Path, [hashtable]$Headers, [string]$Method = 'GET') {
    $client = [Net.Sockets.TcpClient]::new('127.0.0.1', $Port)
    try {
        $client.ReceiveTimeout = 10000
        $stream = $client.GetStream()
        $lines = @("$Method $Path HTTP/1.1") + @($Headers.GetEnumerator() | ForEach-Object { "$($_.Key): $($_.Value)" }) + @('Content-Length: 0', 'Connection: close', '', '')
        $bytes = [Text.Encoding]::ASCII.GetBytes($lines -join "`r`n")
        $stream.Write($bytes, 0, $bytes.Length)
        $status = [IO.StreamReader]::new($stream).ReadLine()
        return [int]($status -split ' ')[1]
    } finally { $client.Close() }
}

# A module with blocks and a screen to read and preview.
$id = [guid]::NewGuid().ToString('N').Substring(0, 8)
$blank = Join-Path $Tmp "dash-$id-blank.oml"
$module = Join-Path $Tmp "dash-$id.oml"
& $Oml new $blank --name DashCheck --kind CrossDevice | Out-Null
$applied = & $Oml apply $blank (Join-Path $PSScriptRoot 'preview.json') $module | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $applied.saved) 'Dashboard fixture did not apply'
# Fixtures for test scaffold: a stand-in BDDFramework and a module with public server actions (as test-scaffold-check).
function Build-Fixture($name, $fixture) {
    $b = Join-Path $Tmp "dash-$id-$name-blank.oml"; $o = Join-Path $Tmp "dash-$id-$name.oml"
    & $Oml new $b --name $name --kind CrossDevice | Out-Null
    $r = & $Oml apply $b (Join-Path $PSScriptRoot $fixture) $o | ConvertFrom-Json
    Require ($LASTEXITCODE -eq 0 -and $r.saved) "Dashboard fixture $name did not apply"
    $o
}
$bdd = Build-Fixture 'BDDFramework' 'bdd-standin.json'
$producer = Build-Fixture 'OrderCore' 'bdd-producer.json'
$browser = @($env:OML_BROWSER, "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

# The dashboard must show "LifeTime not configured" and never reach a real LifeTime.
$env:OML_LIFETIME_HOST = $null; $env:OML_LIFETIME_TOKEN = $null; $env:OML_LOG = $null
# A short download life, so the expiry of a /download id is checked without waiting 10 minutes.
$downloadSeconds = 15; $env:OML_DASHBOARD_DOWNLOAD_SECONDS = $downloadSeconds
$out = Join-Path $Tmp "dash-$id.out"
# Service Studio's session token (see src/Core/Studio.cs): creating it shows an "Allow AI agent to connect?" dialog in
# Service Studio. The dashboard must never do that on its own, so this check never calls `studio` or `pull`, and the
# token files must be exactly as they were after the Home page and the doctor endpoint ran.
# The dashboard gets its own TEMP: its upload folders and any session token it made would be there, apart from other runs.
$dashTemp = Join-Path $Tmp "dash-$id-temp"; New-Item -ItemType Directory -Force $dashTemp | Out-Null
$tokenFolder = Join-Path $dashTemp 'oml-cli'
function Token-State { @(Get-ChildItem $tokenFolder -Filter 'studio-token-*' -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name)|$($_.LastWriteTimeUtc.Ticks)" }) -join ';' }
$tokensBefore = Token-State
# doctor sends an MCP initialize; a closed port keeps it away from a real Service Studio.
$studioPort = $env:OML_STUDIO_PORT; $env:OML_STUDIO_PORT = '9'
$savedTemp = $env:TEMP, $env:TMP; $env:TEMP = $dashTemp; $env:TMP = $dashTemp
# --module (used by the Explorer entry "oml: Open in dashboard") adds #/explorer?module=<path> to the address.
$process = Start-Process -FilePath $Oml -ArgumentList 'dashboard', '--no-open', '--module', "`"$module`"" -RedirectStandardOutput $out -RedirectStandardError "$out.err" -PassThru -WindowStyle Hidden
$null = $process.Handle # without this, ExitCode stays empty after the process ends
$env:OML_DASHBOARD_DOWNLOAD_SECONDS = $null; $env:TEMP, $env:TMP = $savedTemp
try {
    $first = $null
    for ($i = 0; $i -lt 100 -and -not $first; $i++) {
        Start-Sleep -Milliseconds 200
        if (Test-Path $out) { $line = Get-Content $out -TotalCount 1 -ErrorAction SilentlyContinue; if ($line) { $first = $line | ConvertFrom-Json } }
        Require (-not $process.HasExited) "oml dashboard ended early: $(Get-Content "$out.err" -Raw -ErrorAction SilentlyContinue)"
    }
    Require $first 'oml dashboard printed no address'
    $url = [uri]$first.url
    $port = $url.Port
    $key = [Web.HttpUtility]::ParseQueryString($url.Query)['key']
    Require ($url.Host -eq '127.0.0.1' -and $key.Length -ge 32) "Unexpected address $($first.url)"
    Require ($first.url.EndsWith('#/explorer?module=' + [uri]::EscapeDataString($module), [StringComparison]::Ordinal)) "--module not in the address: $($first.url)"
    $base = "http://127.0.0.1:$port"

    # Listening on 127.0.0.1 only.
    $listen = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
    Require ($listen.Count -ge 1 -and @($listen | Where-Object { $_.LocalAddress -ne '127.0.0.1' }).Count -eq 0) "Listens on: $($listen.LocalAddress -join ', ')"

    # Key, Host and Origin.
    $hostHeader = "127.0.0.1:$port"
    Require ((Raw $port '/api/info' @{ Host = $hostHeader }) -eq 403) 'API without key must be 403'
    Require ((Raw $port '/api/info' @{ Host = $hostHeader; 'X-Oml-Key' = 'wrong' }) -eq 403) 'API with a wrong key must be 403'
    Require ((Raw $port '/api/info' @{ Host = $hostHeader; 'X-Oml-Key' = $key }) -eq 200) 'API with the key must be 200'
    Require ((Raw $port '/api/info' @{ Host = "evil.example:$port"; 'X-Oml-Key' = $key }) -eq 403) 'Wrong Host must be 403'
    Require ((Raw $port '/api/info' @{ Host = "localhost:$port"; 'X-Oml-Key' = $key }) -eq 200) 'localhost Host must work'
    Require ((Raw $port '/api/info' @{ Host = $hostHeader; 'X-Oml-Key' = $key; Origin = 'http://evil.example' }) -eq 403) 'Cross-origin request must be 403'
    Require ((Raw $port '/api/info' @{ Host = $hostHeader; 'X-Oml-Key' = $key } 'OPTIONS') -eq 403) 'OPTIONS (CORS preflight) must be 403'
    Require ((Raw $port '/' @{ Host = $hostHeader }) -eq 403) 'The page without its key must be 403'
    $page = Invoke-WebRequest "$base/?key=$key" -UseBasicParsing
    Require ($page.StatusCode -eq 200 -and $page.Headers['Content-Security-Policy'] -match "script-src 'self'" -and -not $page.Headers['Access-Control-Allow-Origin']) 'Page headers'

    $headers = @{ 'X-Oml-Key' = $key }
    function Api([string]$Path, $Body) {
        if ($null -eq $Body) { return Invoke-RestMethod "$base/api/$Path" -Headers $headers }
        return Invoke-RestMethod "$base/api/$Path" -Method Post -Headers $headers -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 6 -Compress)
    }

    # Read endpoints.
    $doctor = Api 'doctor'
    Require ($doctor.code -in 0, 1 -and @($doctor.output.checks).Count -eq 8 -and $doctor.cli -eq 'oml doctor' -and $doctor.checkedAt) 'doctor endpoint'
    # Home must not reach Service Studio on every load: the result is kept for the run until "Check again" (fresh=1).
    Require ($doctor.run -ge 1 -and (Api 'doctor').run -eq $doctor.run) 'doctor must answer from the kept result'
    Require ((Api 'doctor?fresh=1').run -gt $doctor.run) 'doctor?fresh=1 must run doctor again'
    $queue = Api 'queue'
    Require ($null -ne $queue.waiting -and $queue.PSObject.Properties.Name -contains 'running') "queue endpoint: $($queue | ConvertTo-Json -Compress)"
    $inspect = Api 'inspect' @{ path = $module }
    Require ($inspect.code -eq 0 -and $inspect.output.blocks.name -contains 'PvCard' -and $inspect.cli -like 'oml inspect *--own') 'inspect endpoint'
    $describe = Api 'describe' @{ path = $module; name = 'PvCard' }
    Require ($describe.code -eq 0 -and $describe.output.text -match 'Block PvCard') 'describe endpoint'
    $find = Api 'find' @{ name = 'Pv*'; path = $module }
    Require ($find.code -eq 0 -and @($find.output.matches).Count -ge 3) 'find endpoint'
    $preview = Api 'preview' @{ path = $module; element = 'PvCard'; theme = 'dark'; width = 390 }
    Require ($preview.code -eq 0 -and $preview.view -and @($preview.output.checklist).Count -eq 6) 'preview endpoint'
    $view = Invoke-WebRequest "$base/view/$($preview.view)" -UseBasicParsing
    Require ($view.Content -match 'id="PvRoot"' -and $view.Headers['Content-Security-Policy'] -match '^sandbox allow-scripts') 'preview view'
    Require ($view.Content -match 'parent\.postMessage\(\{omlHeight:') 'preview view must report its height for "Full height"'
    $schema = Api 'spec/schema'
    Require ($schema.code -eq 0 -and $schema.output.properties.entities) 'spec schema endpoint'
    $recipes = Api 'recipes'
    Require (@($recipes.recipes).Count -ge 10 -and $recipes.recipes[0].description) 'recipes endpoint'
    $rules = Api 'doc/rules'
    Require ($rules.markdown -match 'Rules for agents') 'rules endpoint'
    $checklist = Api 'doc/checklist'
    Require ($checklist.markdown -match '^# UI checklist') 'checklist endpoint'
    Require ((Api 'doc/best-practices').markdown -match '^# ') 'best practices endpoint'
    $lt = Api 'lt/environments'
    Require ($lt.configured -eq $false) 'LifeTime must report not configured'

    # Bad input is refused with a clear message, not run.
    try { Api 'inspect' @{ path = '--help' } | Out-Null; throw 'option-like path accepted' }
    catch { Require ($_.Exception.Response.StatusCode.value__ -eq 400) "option-like path: $_" }
    try { Api 'describe' @{ path = $module; name = 'a b; rm' } | Out-Null; throw 'bad name accepted' }
    catch { Require ($_.Exception.Response.StatusCode.value__ -eq 400) "bad name: $_" }

    # Uploads: a font and an image, converted and returned; the temporary folder is gone afterwards.
    $font = Get-ChildItem "$env:WINDIR\Fonts" -Filter *.ttf | Sort-Object Length | Select-Object -First 1
    Require $font 'No Windows font to convert'
    $fonts = Api 'convert/fonts' @{ files = @(@{ name = $font.Name; data = [Convert]::ToBase64String([IO.File]::ReadAllBytes($font.FullName)) }) }
    Require ($fonts.code -eq 0 -and $fonts.files[0].output -like '*.woff2' -and $fonts.files[0].outputBytes -gt 0) "font conversion: $($fonts | ConvertTo-Json -Depth 4 -Compress)"
    $woff2 = [Convert]::FromBase64String($fonts.files[0].data)
    Require ([Text.Encoding]::ASCII.GetString($woff2, 0, 4) -eq 'wOF2') 'font result is not WOFF2'
    Add-Type -AssemblyName System.Drawing
    $bitmap = [Drawing.Bitmap]::new(640, 320)
    $graphics = [Drawing.Graphics]::FromImage($bitmap); $graphics.Clear([Drawing.Color]::FromArgb(200, 60, 40)); $graphics.FillEllipse([Drawing.Brushes]::White, 40, 40, 240, 240); $graphics.Dispose()
    $png = Join-Path $Tmp "dash-$id.png"; $bitmap.Save($png, [Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
    $images = Api 'convert/images' @{ to = 'webp'; maxWidth = 320; quality = 80; files = @(
        @{ name = 'a.png'; data = [Convert]::ToBase64String([IO.File]::ReadAllBytes($png)) },
        @{ name = 'b.png'; data = [Convert]::ToBase64String([IO.File]::ReadAllBytes($png)) }) }
    Require ($images.code -eq 0 -and @($images.files).Count -eq 2 -and $images.files[0].output -eq 'a.optimized.webp' -and $images.files[0].after.width -eq 320 -and $images.zipDownload -and !$images.zip) "image conversion: $($images | ConvertTo-Json -Depth 4 -Compress)"
    Require (@(Get-ChildItem $dashTemp -Directory -Filter 'oml-dashboard-*' -ErrorAction SilentlyContinue).Count -eq 0) 'Upload folders were not deleted'
    $pngData = [Convert]::ToBase64String([IO.File]::ReadAllBytes($png))
    $ico = Api 'convert/images' @{ to = 'ico'; files = @(@{ name = 'logo.png'; data = $pngData }) }
    $icoBytes = if ($ico.files[0].data) { [Convert]::FromBase64String($ico.files[0].data) } else { [byte[]]@() }
    Require ($ico.code -eq 0 -and $ico.files[0].output -like '*.ico' -and $ico.files[0].outputFormat -eq 'ico' -and $icoBytes.Length -gt 6 -and
        $icoBytes[0] -eq 0 -and $icoBytes[1] -eq 0 -and $icoBytes[2] -eq 1 -and $icoBytes[3] -eq 0 -and $icoBytes[4] -eq 3) "ICO conversion must return a 3-size .ico: $($ico | ConvertTo-Json -Depth 4 -Compress)"
    try { Api 'convert/images' @{ files = @(@{ name = 'fav.ico'; data = $ico.files[0].data }) } | Out-Null; throw 'ICO input without a format accepted' }
    catch { Require ($_.Exception.Response.StatusCode.value__ -eq 400 -and $_.ErrorDetails.Message -match 'need a target format') "ICO input with Same as input must ask for a format: $_" }
    try { Api 'convert/images' @{ to = 'bmp'; files = @(@{ name = 'logo.png'; data = $pngData }) } | Out-Null; throw 'unknown image format accepted' }
    catch { Require ($_.Exception.Response.StatusCode.value__ -eq 400) "unknown image format must be refused: $_" }
    Require ($images.cli -eq 'oml image batch <folder with these files> --to webp --max-width 320 --quality 80 --out <output folder>') "batch CLI line: $($images.cli)"

    # The image form's options reach the CLI, and details come back.
    $ico2 = Api 'convert/images' @{ to = 'ico'; sizes = @(32, 16); files = @(@{ name = 'logo.png'; data = $pngData }) }
    $ico2Bytes = [Convert]::FromBase64String($ico2.files[0].data)
    Require ($ico2.code -eq 0 -and $ico2Bytes[4] -eq 2 -and (@($ico2.files[0].details.sizes) -join ',') -eq '16,32' -and $ico2.cli -eq 'oml image logo.png --to ico --sizes 16,32') "ICO --sizes: $($ico2 | ConvertTo-Json -Depth 4 -Compress)"
    $trace = Api 'convert/images' @{ to = 'svg'; colors = 2; tolerance = 0.5; minArea = 4; alphaThreshold = 128; transparentBg = $true; precision = 2; files = @(@{ name = 'logo.png'; data = $pngData }) }
    Require ($trace.code -eq 0 -and $trace.files[0].outputFormat -eq 'svg' -and $trace.files[0].details.mode -eq 'trace' -and $trace.files[0].details.removedBackground -and
        $trace.cli -eq 'oml image logo.png --to svg --colors 2 --tolerance 0.5 --min-area 4 --alpha-threshold 128 --transparent-bg --precision 2') "trace options: $($trace.files[0].details | ConvertTo-Json -Compress) $($trace.cli)"
    $embed = Api 'convert/images' @{ to = 'svg'; mode = 'embed'; files = @(@{ name = 'logo.png'; data = $pngData }) }
    Require ($embed.code -eq 0 -and $embed.files[0].details.mode -eq 'embed') 'embed mode'
    $svgIn = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><title>t</title><g id="unused"><rect x="2" y="2" width="20" height="20" fill="#ff0000"/></g></svg>'))
    $optimized = Api 'convert/images' @{ to = 'svg'; precision = 2; removeIds = $true; aggressive = $true; stripTitle = $true; files = @(@{ name = 'icon.svg'; data = $svgIn }) }
    $optimizedText = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($optimized.files[0].data))
    Require ($optimized.code -eq 0 -and $optimizedText -notmatch '<title>' -and $optimizedText -notmatch 'unused' -and $null -ne $optimized.files[0].details.decimals) "SVG optimizer options: $optimizedText"
    # One file: only the options for its type are used and shown, and the shown command runs as it is.
    function Run-Shown([string]$Cli, [string]$Path) {
        $words = ($Cli -replace '^oml image ', '') -split ' '
        $words[0] = $Path
        & $Oml image @words --out "$Path.shown.svg" --force | Out-Null
        return $LASTEXITCODE
    }
    $svgFile = Join-Path $Tmp "dash-$id-icon.svg"; [IO.File]::WriteAllBytes($svgFile, [Convert]::FromBase64String($svgIn))
    $svgOnly = Api 'convert/images' @{ to = 'svg'; colors = 3; transparentBg = $true; precision = 2; aggressive = $true; files = @(@{ name = 'icon.svg'; data = $svgIn }) }
    Require ($svgOnly.code -eq 0 -and $svgOnly.cli -eq 'oml image icon.svg --to svg --precision 2 --aggressive' -and $null -eq $svgOnly.files[0].details.note) "SVG file with tracing options: $($svgOnly.cli) $($svgOnly.files[0].details | ConvertTo-Json -Compress)"
    Require ((Run-Shown $svgOnly.cli $svgFile) -eq 0) "the shown command fails: $($svgOnly.cli)"
    $pngOnly = Api 'convert/images' @{ to = 'svg'; colors = 2; precision = 2; aggressive = $true; removeIds = $true; stripTitle = $true; files = @(@{ name = 'logo.png'; data = $pngData }) }
    Require ($pngOnly.code -eq 0 -and $pngOnly.cli -eq 'oml image logo.png --to svg --colors 2 --precision 2') "PNG file with SVG-only options: $($pngOnly.cli)"
    Require ((Run-Shown $pngOnly.cli $png) -eq 0) "the shown command fails: $($pngOnly.cli)"
    try { Api 'convert/images' @{ to = 'svg'; colors = 99; files = @(@{ name = 'logo.png'; data = $pngData }) } | Out-Null; throw 'colors 99 accepted' }
    catch { Require ($_.ErrorDetails.Message -match 'Colours \(--colors\) must be a whole number from 1 to 32') "error names the field and the option: $($_.ErrorDetails.Message)" }
    $tall = Api 'convert/images' @{ to = 'png'; maxHeight = 100; files = @(@{ name = 'logo.png'; data = $pngData }) }
    Require ($tall.code -eq 0 -and $tall.files[0].after.height -eq 100) "max height: $($tall.files[0] | ConvertTo-Json -Compress)"
    # Each wrong value is refused before anything runs.
    foreach ($bad in @(@{ to = 'svg'; colors = 99 }, @{ to = 'svg'; tolerance = 'smooth' }, @{ to = 'ico'; sizes = @(300) }, @{ to = 'ico'; sizes = @(16, 16) },
        @{ to = 'ico'; maxWidth = 64 }, @{ to = 'png'; quality = 80 }, @{ to = 'svg'; mode = 'embed'; colors = 3 }, @{ to = 'png'; sizes = @(16) },
        @{ to = 'webp'; aggressive = $true }, @{ to = 'svg'; mode = 'vector' }, @{ to = 'svg'; mode = 5 }, @{ to = 'webp'; maxHeight = 0 }, @{ to = 'svg'; removeIds = 'yes' })) {
        $bad.files = @(@{ name = 'logo.png'; data = $pngData })
        try { Api 'convert/images' $bad | Out-Null; throw "image options accepted: $($bad | ConvertTo-Json -Compress)" }
        catch { Require ($_.Exception.Response.StatusCode.value__ -eq 400) "image options must be refused: $($bad.Keys -join ',') $_" }
    }

    # Architecture from dropped files: an honest CLI line, and the report follows the dashboard theme (#theme=).
    $arch = Api 'architecture' @{ files = @(@{ name = 'DashCheck.oml'; data = [Convert]::ToBase64String([IO.File]::ReadAllBytes($module)) }) }
    try {
        Require ($arch.view -and $arch.cli -eq 'oml architecture --in <folder with these files> --out architecture.html') "architecture upload: code $($arch.code), $($arch.cli)"
        Require ((Invoke-WebRequest "$base/view/$($arch.view)" -UseBasicParsing).Content -match 'hashTheme\(\)') 'the architecture page must read #theme='
    } finally { if ($arch.path) { Remove-Item (Split-Path $arch.path) -Recurse -Force -ErrorAction SilentlyContinue } }
    foreach ($template in 'Architecture', 'Check', 'Review') {
        Require ((Get-Content (Join-Path $PSScriptRoot "..\src\Core\$template\template.html") -Raw) -match "theme\(hashTheme\(\)\|\|") "$template template must read #theme= first"
    }

    $appJs = Get-Content (Join-Path $PSScriptRoot '..\src\Dashboard\web\app.js') -Raw
    Require ($appJs -notmatch "'C:\\[A-Za-z]") 'app.js: a path in a JavaScript string needs \\ (one backslash disappears)'
    Require ($appJs -notmatch 'document\.add\(' -and ([regex]::Matches($appJs, 'document\.body\.append\(').Count -ge 2)) 'app.js: download() and the copy fallback must use document.body.append'
    # Drop guard: a file dropped outside a drop zone must not make the browser open it and leave the dashboard.
    Require ($appJs -match "window\.addEventListener\('dragover', e => \{ if \(hasFiles\(e\)\) e\.preventDefault\(\); \}\)" -and
        $appJs -match "window\.addEventListener\('drop', e => \{" -and $appJs -match 'dropGuard\(\);') 'app.js: the page-wide drop guard is missing'

    # The cookbook lists every command of oml --help (a sub-command such as "spec check" counts on its own).
    $dataJs = Get-Content (Join-Path $PSScriptRoot '..\src\Dashboard\web\data.js') -Raw
    $commands = foreach ($line in (& $Oml --help) -match '^oml ') {
        $words = $line -split '\s+'
        if ($words[2] -match '^[a-z][a-z0-9-]*(\|[a-z][a-z0-9-]*)+$') { $words[2] -split '\|' | ForEach-Object { "$($words[1]) $_" } }
        elseif ($words[2] -match '^[a-z][a-z0-9-]*$') { "$($words[1]) $($words[2])" }
        else { $words[1] }
    }
    $missing = @($commands | Sort-Object -Unique | Where-Object { $dataJs -notmatch ("'oml " + [regex]::Escape($_) + "[ ']") })
    Require (@($commands).Count -ge 40 -and $missing.Count -eq 0) "Cookbook (web/data.js) misses: $($missing -join '; ')"
    try { Api 'convert/images' @{ files = @(@{ name = '..\evil.png'; data = 'AAAA' }) } | Out-Null; throw 'path in upload name accepted' }
    catch { Require ($_.Exception.Response.StatusCode.value__ -eq 400) "path in upload name: $_" }

    # Downloads: /download/<id> needs the key (in the query, a link sends no header), and an id expires.
    $zipPath = "/download/$($images.zipDownload)"
    $zip = Invoke-WebRequest "$base$($zipPath)?key=$key" -UseBasicParsing
    Require ($zip.StatusCode -eq 200 -and $zip.Content[0] -eq 0x50 -and $zip.Content[1] -eq 0x4B -and $zip.Headers['Content-Security-Policy'] -eq 'sandbox' -and $zip.Headers['Content-Disposition'] -match 'attachment; filename="images-optimized.zip"') 'zip download'
    Require ((Raw $port $zipPath @{ Host = $hostHeader }) -eq 403) 'download without key must be 403'
    Require ((Raw $port "$($zipPath)?key=wrong" @{ Host = $hostHeader }) -eq 403) 'download with a wrong key must be 403'
    Require ((Raw $port "$($zipPath)?key=$key" @{ Host = "evil.example:$port" }) -eq 403) 'download with a wrong Host must be 403'
    Require ((Raw $port "/download/00000000000000000000000000000000?key=$key" @{ Host = $hostHeader }) -eq 404) 'unknown download id must be 404'
    $expiresAt = [DateTime]::UtcNow.AddSeconds($downloadSeconds)

    # Get modules: a folder with an .oml and an .oap, the package's modules, extraction, and an upload.
    $folder = Join-Path $Tmp "dash-$id-folder"
    New-Item -ItemType Directory -Force (Join-Path $folder 'sub') | Out-Null
    Copy-Item $module (Join-Path $folder 'sub\DashCheck.oml')
    $oap = Join-Path $folder 'DashApp.oap'
    $packed = & $Oml app pack $oap --name DashApp $module | ConvertFrom-Json
    Require ($LASTEXITCODE -eq 0 -and $packed.saved) 'app pack for the folder test'
    $listing = Api 'modules/folder' @{ folder = $folder }
    Require (@($listing.files).Count -eq 2 -and ($listing.files | Where-Object kind -eq 'oml').path -eq (Join-Path $folder 'sub\DashCheck.oml') -and ($listing.files | Where-Object kind -eq 'oap').name -eq 'DashApp.oap' -and $listing.cli -like 'Get-ChildItem *') "folder listing: $($listing | ConvertTo-Json -Depth 4 -Compress)"
    $package = Api 'modules/app' @{ path = $oap }
    Require ($package.code -eq 0 -and $package.output.modules[0].name -eq 'DashCheck' -and $package.output.applications[0].name -eq 'DashApp' -and $package.cli -like 'oml app list *') "package listing: $($package | ConvertTo-Json -Depth 5 -Compress)"
    $extracted = Api 'modules/extract' @{ path = $oap }
    Require ($extracted.code -eq 0 -and (Test-Path -LiteralPath $extracted.output.extracted[0].path) -and $extracted.output.extracted[0].path.StartsWith($first.workFolder, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $oap)) "package extraction: $($extracted | ConvertTo-Json -Depth 5 -Compress)"
    $uploaded = Api 'modules/upload' @{ files = @(@{ name = 'Up.oml'; data = [Convert]::ToBase64String([IO.File]::ReadAllBytes($module)) }) }
    Require ((Test-Path -LiteralPath $uploaded.files[0].path) -and $uploaded.files[0].kind -eq 'oml' -and $uploaded.folder.StartsWith($first.workFolder, [StringComparison]::OrdinalIgnoreCase)) 'module upload kept in the work folder'
    foreach ($bad in @(@('modules/folder', @{ folder = (Join-Path $Tmp 'no-such-folder') }), @('modules/app', @{ path = $module }), @('modules/upload', @{ files = @(@{ name = 'x.txt'; data = 'AAAA' }) }),
        @('openapi', @{ path = $module; servers = @('ftp://x') }), @('architecture', @{ in = @($folder); layers = (Join-Path $Tmp 'missing-layers.json') }))) {
        try { Api $bad[0] $bad[1] | Out-Null; throw "$($bad[0]) accepted bad input" }
        catch { Require ($_.Exception.Response.StatusCode.value__ -eq 400) "$($bad[0]) bad input must be 400: $_" }
    }
    # LifeTime is not configured here: every LifeTime action says so and reaches nothing.
    foreach ($ltPath in 'lt/teams', 'lt/team-apps?team=Commerce') { Require ((Api $ltPath).configured -eq $false) "$ltPath must report not configured" }
    Require ((Api 'lt/download' @{ app = 'Shop'; env = 'Development' }).configured -eq $false -and (Api 'lt/report' @{ team = 'Commerce' }).configured -eq $false) 'LifeTime download and report must report not configured'
    # Architecture: --json, and both files through /download.
    $arch = Api 'architecture' @{ in = @($folder); json = $true }
    Require ($arch.code -eq 0 -and $arch.view -and $arch.download -and $arch.jsonDownload -and !$arch.html -and $arch.cli -match '--json') "architecture report: $($arch | ConvertTo-Json -Depth 3 -Compress)"
    $archJson = Invoke-RestMethod "$base/download/$($arch.jsonDownload)?key=$key"
    Require ($archJson.schema -eq 1) 'architecture JSON download'
    # Extension inspect.
    $xif = Join-Path $folder 'DashExt.xif'
    $null = & $Oml extension new $xif --name DashExt
    $ext = Api 'extension/inspect' @{ path = $xif }
    Require ($ext.code -eq 0 -and $ext.output.name -eq 'DashExt' -and $ext.cli -like 'oml extension inspect *') 'extension inspect'
    Remove-Item -LiteralPath $folder, (Split-Path $extracted.folder), $uploaded.folder -Recurse -Force
    # The zip id has expired by now (OML_DASHBOARD_DOWNLOAD_SECONDS).
    while ([DateTime]::UtcNow -lt $expiresAt.AddSeconds(1)) { Start-Sleep -Milliseconds 250 }
    Require ((Raw $port "$($zipPath)?key=$key" @{ Host = $hostHeader }) -eq 404) 'an expired download must be 404'
    Write-Host 'ok   dashboard: working set sources (folder, package, upload), /download key and expiry, architecture --json, extension inspect'

    # No write endpoint exists, not even a hidden one.
    foreach ($path in 'lt/deploy', 'lt/tag', 'lt/abort', 'lt/continue', 'install', 'publish', 'references', 'apply', 'run') {
        foreach ($method in 'GET', 'POST') {
            try { Invoke-WebRequest "$base/api/$path" -Method $method -Headers $headers -UseBasicParsing | Out-Null; throw "$method $path answered" }
            catch { Require ($_.Exception.Response.StatusCode.value__ -eq 404) "$method /api/$path must be 404: $_" }
        }
    }
    Write-Host 'ok   dashboard: 127.0.0.1 only, key/Host/Origin checks, doctor, inspect, describe, find, preview, schema, uploads, no write endpoints'

    function Refused([string]$Path, $Body, [string]$What) {
        try { Api $Path $Body | Out-Null; throw "$What accepted" }
        catch { Require ($_.Exception.Response.StatusCode.value__ -eq 400) "$What must be refused with 400: $_" }
    }

    # Check: findings, the HTML report, the JSON report as a baseline, a rules file.
    $check = Api 'check' @{ path = $module }
    Require ($check.code -eq 0 -and @($check.output.findings).Count -ge 1 -and $check.view -and (Test-Path $check.json) -and $check.download -and $check.jsonDownload -and !$check.html -and $check.cli -like 'oml check *--out *--json *') "check endpoint: $($check | ConvertTo-Json -Depth 8 -Compress)"
    $rerun = Api 'check' @{ path = $module; baseline = $check.json }
    Require ($rerun.code -eq 0 -and $rerun.output.summary.new -eq 0 -and $rerun.output.summary.existing -ge 1 -and @($rerun.output.findings | Where-Object status -ne 'existing').Count -eq 0) 'check with a baseline: every finding must be existing'
    $rulesFile = Api 'check/init-rules' @{}
    Require ($rulesFile.code -eq 0 -and (Test-Path $rulesFile.path) -and (Get-Content $rulesFile.path -Raw) -match '"rules"') 'check init-rules endpoint'
    $ruled = Api 'check' @{ in = @($module); rules = $rulesFile.path }
    Require ($ruled.code -eq 0 -and $ruled.cli -like '*--in *--rules *') 'check with --in and --rules'
    Refused 'check' @{} 'check without a module or folder'
    Refused 'check' @{ path = $module; baseline = $module } 'a baseline that is not .json'

    # Review: risks, changes, findings, Markdown and release notes.
    $review = Api 'review' @{ before = $blank; after = $module }
    Require ($review.code -eq 0 -and $review.output.summary.added -ge 4 -and $review.markdown -match 'Logic bugs are not covered' -and $review.notes -and $review.view -and $review.download -and $review.markdownDownload -and $review.cli -like 'oml review *--md *--notes *') "review endpoint: $($review | ConvertTo-Json -Depth 8 -Compress)"
    # The large outputs come from /download/<id>, not inside the JSON answer.
    Require ((Invoke-WebRequest "$base/download/$($check.download)?key=$key" -UseBasicParsing).Content -match '<html') 'check HTML download'
    Require ((Invoke-RestMethod "$base/download/$($check.jsonDownload)?key=$key").findings) 'check JSON download'
    Require ((Invoke-WebRequest "$base/download/$($review.download)?key=$key" -UseBasicParsing).Content -match '<html') 'review HTML download'
    Require ([Text.Encoding]::UTF8.GetString((Invoke-WebRequest "$base/download/$($review.markdownDownload)?key=$key" -UseBasicParsing).RawContentStream.ToArray()) -match 'Logic bugs are not covered') 'review Markdown download'
    Refused 'review' @{ before = $module; after = $module } 'review of a file with itself'

    # Search and Docs: doc --search, spec --search, warn, and opening a hit. Only doc, spec, warn, --help and recipes open.
    $search = Api 'search' @{ q = 'confirm popup before delete' }
    Require ($search.code -eq 0 -and @($search.output.hits).Count -ge 1 -and $search.cli -like 'oml doc --search *') 'search endpoint'
    $weak = Api 'search' @{ q = 'zzqx flurb' }
    Require ($weak.code -eq 0 -and @($weak.output.hits).Count -eq 0 -and $weak.output.hint) 'search without a match must give a hint'
    $specSearch = Api 'search' @{ q = 'load detail on click'; scope = 'spec' }
    Require ($specSearch.code -eq 0 -and @($specSearch.output.hits).Count -ge 1 -and @($specSearch.output.hits | Where-Object { $_.kind -notin 'spec', 'key' }).Count -eq 0 -and $specSearch.cli -like 'oml spec --search *') 'spec search endpoint'
    $warn = Api 'search' @{ q = 'NoErrorHandlingInFlow'; scope = 'warn' }
    Require ($warn.code -eq 0 -and $warn.output -match 'NoErrorHandlingInFlow' -and $warn.cli -eq 'oml warn NoErrorHandlingInFlow') 'warn endpoint'
    # Every hit of several searches opens (doc, spec, key, warning, recipe and command hits).
    $opens = foreach ($q in 'confirm popup before delete', 'convert png to svg', 'search the docs', 'validation warning cause', 'load detail on click') {
        foreach ($scope in 'all', 'spec') { (Api 'search' @{ q = $q; scope = $scope }).output.hits.open } }
    $opens = @($opens | Sort-Object -Unique)
    Require ($opens.Count -ge 10) "too few search hits to open: $($opens -join ', ')"
    foreach ($open in $opens + @('oml warn SQLInjection', 'oml doc rules', 'oml spec entities', 'oml image --help', 'oml doc --help', 'oml spec --help', 'oml warn --help', 'skill/recipes/block-popup-event.jsonc')) {
        $opened = Api 'doc/open' @{ open = $open }
        Require ($opened.code -eq 0 -and "$($opened.output)".Length -gt 20) "doc/open $open"
    }
    foreach ($bad in 'oml install a.oml b.oml', 'oml doc --search x', 'skill/recipes/../x.jsonc', 'C:\Windows\win.ini') { Refused 'doc/open' @{ open = $bad } "doc/open '$bad'" }

    # Explorer: find --text, tree, history, describe --css, validate, test scaffold.
    $text = Api 'find/text' @{ path = $module; text = 'card'; top = 5 }
    Require ($text.code -eq 0 -and $text.output.hits.name -contains 'PvCard' -and $text.cli -like 'oml find * --text card --top 5') 'find/text endpoint'
    Refused 'find/text' @{ path = $module; text = '--help' } 'find --text with an option as the words'
    $tree = Api 'tree' @{ path = $module; name = 'PvCard'; depth = 1 }
    Require ($tree.code -eq 0 -and $tree.output.path -eq 'PvCard' -and @($tree.output.children).Count -ge 1 -and $tree.cli -like 'oml tree * PvCard --depth 1') 'tree endpoint'
    $sub = Api 'tree' @{ path = $module; name = 'PvCard/PvRoot'; depth = 2 }
    Require ($sub.code -eq 0 -and $sub.output.path -eq 'PvCard/PvRoot') 'tree endpoint with a path'
    Refused 'tree' @{ path = $module; name = 'PvCard; rm' } 'tree with a bad path'
    Refused 'tree' @{ path = $module; name = 'PvCard'; depth = 99 } 'tree with depth 99'
    $history = Api 'inspect' @{ path = $module; history = $true }
    Require ($history.code -eq 0 -and $history.cli -like 'oml inspect *--own --history') 'inspect --history endpoint'
    $dh = Api 'describe' @{ path = $module; name = 'PvCard'; history = $true }
    Require ($dh.code -eq 0 -and $dh.cli -like '*PvCard --history') 'describe --history endpoint'
    $css = Api 'describe' @{ path = $module; name = 'PvCard'; css = $true; history = $true }
    Require ($css.code -eq 0 -and $css.output.styleSheet -match 'pv-card' -and $css.cli -like '*PvCard --css') 'describe --css endpoint (no --history with it)'
    $validate = Api 'validate' @{ path = $module }
    $noHandler = @($validate.output.warnings | Where-Object id -eq 'NoErrorHandlingInFlow')
    Require ($validate.code -in 0, 1 -and $noHandler.Count -eq 1 -and $noHandler[0].hint -and $validate.cli -like 'oml validate * --warnings') 'validate endpoint'
    $scaffold = Api 'test/scaffold' @{ path = $producer; actions = @('PlaceOrder'); bdd = $bdd }
    Require ($scaffold.code -eq 0 -and (Test-Path $scaffold.path) -and $scaffold.spec -match 'PlaceOrder_Tests' -and @($scaffold.output.next).Count -ge 2 -and $scaffold.cli -like 'oml test scaffold * PlaceOrder --bdd *') "test scaffold endpoint: $($scaffold | ConvertTo-Json -Depth 8 -Compress)"
    Refused 'test/scaffold' @{ path = $producer; actions = @('PlaceOrder') } 'test scaffold without --bdd'
    Refused 'test/scaffold' @{ path = $producer; actions = @(); bdd = $bdd } 'test scaffold without an action'

    # Timings and the preview inputs and steps.
    $timed = Api 'inspect' @{ path = $module; timings = $true }
    Require ($timed.code -eq 0 -and $timed.output.timings.total -gt 0 -and $timed.cli -like '*--timings') 'inspect with timings'
    $timedDocs = Api 'search' @{ q = 'entity'; timings = $true }
    Require ($timedDocs.code -eq 0 -and $timedDocs.cli -notlike '*--timings*') 'timings must not reach the launcher commands'
    Refused 'preview' @{ path = $module; element = 'PvCard'; steps = @(@{ kind = 'navigate'; value = 'x' }) } 'a preview step of an unknown kind'
    Refused 'preview' @{ path = $module; element = 'PvCard'; steps = @(@{ kind = 'type'; value = '#Name' }) } 'a type step without ='
    Refused 'preview' @{ path = $module; element = 'PvCard'; args = @(@{ name = 'Ti tle'; value = '1' }) } 'a preview input with a bad name'
    Refused 'preview' @{ path = $module; element = 'PvCard'; steps = @(@{ kind = 'wait'; value = 'abc' }) } 'a wait step that is not a number'
    Refused 'preview' @{ path = $module; element = 'PvCard'; height = '500' } 'a height sent as text'
    Refused 'tree' @{ path = $module; name = 'PvCard'; depth = 'x' } 'a tree depth that is not a number'
    Refused 'preview' @{ path = $module; element = 'PvCard'; steps = @(@{ kind = 'click'; value = '-x' }) } 'a click step that starts with -'
    $negative = Api 'preview' @{ path = $module; element = 'PvCard'; args = @(@{ name = 'Count'; value = '-1' }) }
    Require ($negative.code -eq 0 -and $negative.cli -like '*--arg Count=-1*') "a negative --arg value: $($negative.cli)"
    if ($browser) {
        $stepped = Api 'preview' @{ path = $module; element = 'PvCard'; height = 500; timings = $true; args = @(@{ name = 'Title'; value = '"Hi"' }); steps = @(@{ kind = 'click'; value = '#GoButton' }, @{ kind = 'eval'; value = '1+1' }, @{ kind = 'wait'; value = 50 }) }
        Require ($stepped.code -eq 0 -and @($stepped.output.steps).Count -eq 3 -and $stepped.output.steps[1].result -eq '2' -and $stepped.output.timings -and
            $stepped.cli -like '*--arg "Title=\"Hi\"" --height 500 --click #GoButton --eval 1+1 --wait 50*--timings') "preview with inputs and steps: $($stepped.cli) $($stepped.output.steps | ConvertTo-Json -Compress)"
    }
    # The work folders this check made (one per action, under the dashboard work folder).
    foreach ($file in $check.json, $rerun.json, $ruled.json, $rulesFile.path, $scaffold.path) { if ($file) { Remove-Item (Split-Path $file) -Recurse -Force -ErrorAction SilentlyContinue } }
    if ($review.folder) { Remove-Item $review.folder -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host 'ok   dashboard: check (baseline, rules), review, search, doc/open, warn, find --text, tree, history, css, validate, test scaffold, timings, preview steps'

    # Screenshots, when a browser exists.
    if ($browser) {
        $shots = @()
        $m = [uri]::EscapeDataString($module)
        $targets = @(@{ page = 'home'; hash = '#/home' }, @{ page = 'explorer'; hash = "#/explorer?module=$m" }, @{ page = 'tools'; hash = '#/tools' },
            @{ page = 'check'; hash = "#/check?module=$m"; expect = 'missing-description' },
            @{ page = 'review'; hash = "#/review?before=$([uri]::EscapeDataString($blank))&after=$m"; expect = 'PvCard' },
            @{ page = 'docs'; hash = '#/docs?q=confirm%20popup%20before%20delete&open=oml%20doc%20rules'; expect = 'block-popup-event' },
            @{ page = 'previews'; hash = "#/previews?module=$m"; expect = 'Inputs and interaction steps' },
            @{ page = 'modules'; hash = '#/modules' }, @{ page = 'prompt'; hash = '#/prompt?selftest=1' }, @{ page = 'architecture'; hash = '#/architecture' }, @{ page = 'lifetime'; hash = '#/lifetime' }) +
            @('recipes', 'assets', 'openapi', 'activity', 'cookbook', 'rules' | ForEach-Object { @{ page = $_; hash = "#/$_"; themes = @('light') } })
        foreach ($target in $targets) {
            foreach ($theme in $(if ($target.themes) { $target.themes } else { 'light', 'dark' })) {
                $png = Join-Path $Tmp "dashboard-$($target.page)-$theme.png"
                $profile = Join-Path $Tmp "dash-profile-$([guid]::NewGuid().ToString('N'))"
                $dom = Join-Path $Tmp "dashboard-$($target.page)-$theme.html"
                $address = "$base/?key=$key&theme=$theme$($target.hash)"
                $arguments = @('--headless=new', '--disable-gpu', '--no-first-run', '--hide-scrollbars', "--user-data-dir=`"$profile`"", '--window-size=1440,1000',
                    '--virtual-time-budget=20000', "--screenshot=`"$png`"", '--dump-dom', "`"$address`"")
                # A cold browser start can stall under load: one retry, killing the whole browser tree first.
                foreach ($attempt in 1, 2) {
                    $run = Start-Process -FilePath $browser -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $dom -RedirectStandardError "$dom.err"
                    if ($run.WaitForExit(60000)) { break }
                    & taskkill /T /F /PID $run.Id 2>&1 | Out-Null
                    if ($attempt -eq 2) { throw "Browser timed out twice on $($target.page) $theme" }
                }
                $text = ''
                for ($i = 0; $i -lt 50 -and $text -notmatch '</html>'; $i++) { Start-Sleep -Milliseconds 100; $text = Get-Content $dom -Raw -ErrorAction SilentlyContinue }
                Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue
                Require ($text -match 'data-js-errors="0"') "JavaScript errors on $($target.page) ($theme): $([regex]::Match($text, 'data-js-errors="\d+"').Value)"
                Require ($text -match "data-ready=`"$($target.page)`"" -and $text -match "data-theme=`"$theme`"") "Page $($target.page) ($theme) did not finish rendering"
                Require ((Test-Path $png) -and (Get-Item $png).Length -gt 5000) "No screenshot for $($target.page) ($theme)"
                if ($target.expect) { Require ($text -match [regex]::Escape($target.expect)) "Page $($target.page) ($theme) does not show '$($target.expect)'" }
                $shots += $png
            }
        }
        Require ((Get-Content (Join-Path $Tmp 'dashboard-explorer-light.html') -Raw) -match 'PvCard') 'Explorer screenshot did not load the module'
        # The viewer got the page height from a sandboxed preview frame ("Full height").
        Require ((Get-Content (Join-Path $Tmp 'dashboard-previews-light.html') -Raw) -match '<iframe [^>]*data-height="\d+"') 'Preview frames did not report their height to the viewer'
        foreach ($theme in 'light', 'dark') {
            $promptDom = Get-Content (Join-Path $Tmp "dashboard-prompt-$theme.html") -Raw
            Require ($promptDom -match 'data-selftest="ok"') "Prompt self-test (working set API and prompt text) did not pass ($theme): $([regex]::Match($promptDom, 'Prompt self-test[^<]*').Value)"
            Require ((Get-Content (Join-Path $Tmp "dashboard-modules-$theme.html") -Raw) -match 'Working set') "Get modules page ($theme)"
        }
        Write-Host "ok   dashboard pages (home, explorer, tools, check, review, docs, previews, modules, prompt, architecture, lifetime; the rest in light): no JavaScript errors, screenshots $($shots -join ', ')"
    } else { Write-Host 'skip dashboard screenshots: no Edge or Chrome' }

    Require ((Token-State) -eq $tokensBefore) 'The dashboard created or changed a Service Studio session token without a click'
    Write-Host 'ok   dashboard: no Service Studio session token without a click'

    # Stop cleanly.
    $stop = Api 'shutdown' @{}
    Require $stop.stopping 'shutdown endpoint'
    Require ($process.WaitForExit(15000)) 'oml dashboard did not stop'
    Require ($process.ExitCode -eq 0) "oml dashboard stopped with exit code $($process.ExitCode)"
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    $env:OML_STUDIO_PORT = $studioPort
}
Write-Host 'ok   dashboard stopped cleanly'
