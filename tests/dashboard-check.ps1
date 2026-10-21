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

# The dashboard must show "LifeTime not configured" and never reach a real LifeTime.
$env:OML_LIFETIME_HOST = $null; $env:OML_LIFETIME_TOKEN = $null; $env:OML_LOG = $null
$out = Join-Path $Tmp "dash-$id.out"
# Service Studio's session token (see src/Core/Studio.cs): creating it shows an "Allow AI agent to connect?" dialog in
# Service Studio. The dashboard must never do that on its own, so this check never calls `studio` or `pull`, and the
# token files must be exactly as they were after the Home page and the doctor endpoint ran.
$tokenFolder = Join-Path ([IO.Path]::GetTempPath()) 'oml-cli'
function Token-State { @(Get-ChildItem $tokenFolder -Filter 'studio-token-*' -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name)|$($_.LastWriteTimeUtc.Ticks)" }) -join ';' }
$tokensBefore = Token-State
# --module (used by the Explorer entry "oml: Open in dashboard") adds #/explorer?module=<path> to the address.
$process = Start-Process -FilePath $Oml -ArgumentList 'dashboard', '--no-open', '--module', "`"$module`"" -RedirectStandardOutput $out -RedirectStandardError "$out.err" -PassThru -WindowStyle Hidden
$null = $process.Handle # without this, ExitCode stays empty after the process ends
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
    Require ($doctor.code -in 0, 1 -and @($doctor.output.checks).Count -eq 8 -and $doctor.cli -eq 'oml doctor') 'doctor endpoint'
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
    $schema = Api 'spec/schema'
    Require ($schema.code -eq 0 -and $schema.output.properties.entities) 'spec schema endpoint'
    $recipes = Api 'recipes'
    Require (@($recipes.recipes).Count -ge 10 -and $recipes.recipes[0].description) 'recipes endpoint'
    $rules = Api 'doc/rules'
    Require ($rules.markdown -match 'Rules for agents') 'rules endpoint'
    $checklist = Api 'doc/checklist'
    Require ($checklist.markdown -match '^# UI checklist') 'checklist endpoint'
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
    Require ($images.code -eq 0 -and @($images.files).Count -eq 2 -and $images.files[0].output -eq 'a.optimized.webp' -and $images.files[0].after.width -eq 320 -and $images.zip) "image conversion: $($images | ConvertTo-Json -Depth 4 -Compress)"
    Require (@(Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter 'oml-dashboard-*' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt $process.StartTime }).Count -eq 0) 'Upload folders were not deleted'
    try { Api 'convert/images' @{ files = @(@{ name = '..\evil.png'; data = 'AAAA' }) } | Out-Null; throw 'path in upload name accepted' }
    catch { Require ($_.Exception.Response.StatusCode.value__ -eq 400) "path in upload name: $_" }

    # No write endpoint exists, not even a hidden one.
    foreach ($path in 'lt/deploy', 'lt/tag', 'lt/abort', 'lt/continue', 'install', 'publish', 'references', 'apply', 'run') {
        foreach ($method in 'GET', 'POST') {
            try { Invoke-WebRequest "$base/api/$path" -Method $method -Headers $headers -UseBasicParsing | Out-Null; throw "$method $path answered" }
            catch { Require ($_.Exception.Response.StatusCode.value__ -eq 404) "$method /api/$path must be 404: $_" }
        }
    }
    Write-Host 'ok   dashboard: 127.0.0.1 only, key/Host/Origin checks, doctor, inspect, describe, find, preview, schema, uploads, no write endpoints'

    # Screenshots, when a browser exists.
    $browser = @($env:OML_BROWSER, "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($browser) {
        $shots = @()
        foreach ($target in @(@{ page = 'home'; hash = '#/home' }, @{ page = 'explorer'; hash = '#/explorer?module=' + [uri]::EscapeDataString($module) }, @{ page = 'tools'; hash = '#/tools' })) {
            foreach ($theme in 'light', 'dark') {
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
                $shots += $png
            }
        }
        Require ((Get-Content (Join-Path $Tmp 'dashboard-explorer-light.html') -Raw) -match 'PvCard') 'Explorer screenshot did not load the module'
        Write-Host "ok   dashboard pages: no JavaScript errors, screenshots $($shots -join ', ')"
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
}
Write-Host 'ok   dashboard stopped cleanly'
