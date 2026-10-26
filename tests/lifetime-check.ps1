param([string]$Oml = (Join-Path $PSScriptRoot '..\src\bin\Release\net10.0\oml.exe'), [string]$Tmp = (Join-Path $PSScriptRoot '..\tmp'))
$ErrorActionPreference = 'Stop'
$names = @('OML_LIFETIME_HOST','OML_LIFETIME_TOKEN','OML_LIFETIME_INSECURE_LOCALHOST','OML_SERVICE_STUDIO','OML_DEBUG_ARGS','TEMP','TMP')
$saved = @{}; foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$job = $null; $packages = $null
New-Item -ItemType Directory -Force $Tmp | Out-Null
$ready = Join-Path $Tmp ('lt-ready-' + [guid]::NewGuid().ToString('N'))
$progress = Join-Path $Tmp ('lt-progress-' + [guid]::NewGuid().ToString('N'))
function Assert($Condition, $Message) { if (!$Condition) { throw $Message } }
# The Service Studio the run started with: for the steps that need Core (pack, extract, team reports).
function Use-RealStudio { if ($null -eq $saved['OML_SERVICE_STUDIO']) { Remove-Item Env:OML_SERVICE_STUDIO -ErrorAction SilentlyContinue } else { $env:OML_SERVICE_STUDIO = $saved['OML_SERVICE_STUDIO'] } }
function Invoke-Lt([string[]]$Arguments, [int]$Exit = 0) {
    $output = & $Oml lt @Arguments 2> $progress
    $code = $LASTEXITCODE
    $text = $output -join "`n"
    $events = Get-Content -LiteralPath $progress -Raw -ErrorAction SilentlyContinue
    $marker = $env:OML_LIFETIME_TOKEN.Split('"')[0]
    Assert (!$text.Contains($marker)) 'Token leaked on stdout'
    Assert (!$events -or !$events.Contains($marker)) 'Token leaked on stderr'
    Assert ($code -eq $Exit) "Expected exit $Exit, got $code for $($Arguments -join ' '): $text"
    return ($text | ConvertFrom-Json)
}
function Invoke-Architecture([string[]]$Arguments, [int]$Exit = 2) {
    $output = & $Oml architecture @Arguments 2> $progress
    $code = $LASTEXITCODE
    $text = $output -join "`n"
    $errorText = (Get-Content -LiteralPath $progress -Raw -ErrorAction SilentlyContinue)
    $marker = $env:OML_LIFETIME_TOKEN.Split('"')[0]
    if ($marker) { Assert (!$text.Contains($marker) -and (!$errorText -or !$errorText.Contains($marker))) 'Architecture token leaked' }
    Assert ($code -eq $Exit) "Architecture expected exit $Exit, got ${code}: $text $errorText"
    if ($Exit -eq 0) { return ($text | ConvertFrom-Json) }
    Assert ($text.Length -eq 0) 'Architecture failure wrote stdout'
    Assert ($errorText.Trim() -match '^LifeTime is not available \(.+\)\. Export the team''s applications as \.oap files into a folder and run: oml architecture --in <folder>$') 'Architecture fallback message'
    return $errorText
}
try {
    $env:TEMP = [IO.Path]::GetFullPath($Tmp); $env:TMP = $env:TEMP
    # Use a random credential held only in process environments and memory.
    $env:OML_LIFETIME_TOKEN = 'mock-' + [guid]::NewGuid().ToString('N') + '"\'
    $env:OML_LIFETIME_HOST = ''
    $null = Invoke-Architecture @('--team','Commerce')
    $r = Invoke-Lt @('environments') 2
    Assert ($r.error -match 'OML_LIFETIME_HOST') 'Missing host error'
    $env:OML_LIFETIME_HOST = '127.0.0.1:1'
    $credential = $env:OML_LIFETIME_TOKEN; $env:OML_LIFETIME_TOKEN = ''
    $null = Invoke-Architecture @('--team','Commerce')
    # Missing token needs a direct check because the leak assertion requires a nonempty token.
    $missing = & $Oml lt environments | ConvertFrom-Json
    Assert ($LASTEXITCODE -eq 2 -and $missing.error -match 'OML_LIFETIME_TOKEN') 'Missing token error'
    $env:OML_LIFETIME_TOKEN = $credential
    # Real packages for the mock to serve (oml app pack), so --extract and the dashboard's team reports run for real.
    $packages = Join-Path $env:TEMP ('lt-packages-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $packages | Out-Null
    foreach ($app in 'Shop', 'Orders') {
        $source = Join-Path $packages "${app}Core.oml"
        & $Oml new $source --name "${app}Core" --kind CrossDevice | Out-Null
        $packed = & $Oml app pack (Join-Path $packages "$($app.ToLowerInvariant()).oap") --name $app $source | ConvertFrom-Json
        Assert ($LASTEXITCODE -eq 0 -and $packed.saved) "Package $app for the mock"
    }
    $shopBytes = [IO.File]::ReadAllBytes((Join-Path $packages 'shop.oap'))
    $ordersBytes = (Get-Item (Join-Path $packages 'orders.oap')).Length
    $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $socket.Start(); $port = $socket.LocalEndpoint.Port; $socket.Stop()
    $env:OML_LIFETIME_HOST = "127.0.0.1:$port"; $env:OML_LIFETIME_INSECURE_LOCALHOST = '1'
    $job = Start-Job -FilePath (Join-Path $PSScriptRoot 'lifetime-mock.ps1') -ArgumentList $port,$ready,$packages
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (!(Test-Path -LiteralPath $ready)) {
        if ($job.State -eq 'Failed' -or [DateTime]::UtcNow -gt $deadline) { throw 'LifeTime mock did not start' }
        Start-Sleep -Milliseconds 100
    }
    $env:OML_SERVICE_STUDIO = Join-Path $Tmp 'no-service-studio'
    $env:OML_DEBUG_ARGS = '1'
    $r = @(Invoke-Lt @('teams'))
    Assert ($r[0].name -eq 'Commerce' -and $r[0].key -eq 'commerce') 'Team list'
    $r = @(Invoke-Lt @('team-apps','COMMERCE'))
    Assert ($r.Count -eq 2 -and $r[0].Key -eq 'shop') 'Team applications with keys'
    $r = Invoke-Lt @('team-apps','missing') 2
    Assert ($r.error -match 'matching team') 'Team not found'
    $r = Invoke-Architecture @('--in','existing folder','--team','Commerce','--app','Shop','--out','report.html','--json','report.json','--pretty') 0
    Assert (($r.args[0..3] -join '|') -eq 'architecture|--in|existing folder|--in') 'Preserve existing inputs'
    $downloadFolder = $r.args[4]
    Assert (($r.args[5..13] -join '|') -eq '--team-label|Commerce|--app|Shop|--out|report.html|--json|report.json|--pretty') 'Argument rewrite preserves options'
    Assert ($r.args -notcontains '--team') 'Remove team flag'
    Assert ($r.files.Count -eq 2 -and $r.files[0].bytes -eq $shopBytes.Length -and $r.files[1].bytes -eq $ordersBytes) 'Both current OAP files downloaded'
    Assert (!(Test-Path -LiteralPath $downloadFolder)) 'Temporary downloads removed after debug exit'
    # oml check --team uses the same download and rewrite; its fallback names its own command.
    $rules = Join-Path $Tmp 'check-rules.json'; '{"rules":[]}' | Set-Content $rules
    $r = (& $Oml check --team Commerce --rules $rules 2> $progress) | ConvertFrom-Json
    Assert ($LASTEXITCODE -eq 0 -and $r.args[0] -eq 'check' -and $r.args[1] -eq '--in' -and (($r.args[3..6]) -join '|') -eq "--team-label|Commerce|--rules|$rules") 'Check team rewrite'
    # Bad check input is refused before anything is downloaded.
    $existing = Join-Path $Tmp 'existing-report.html'; 'x' | Set-Content $existing
    $refused = (& $Oml check --team Commerce --out $existing 2> $progress) | ConvertFrom-Json
    Assert ($LASTEXITCODE -eq 2 -and $refused.error -like 'Output already exists:*' -and !$refused.args) 'Check team input checked before download'
    $refused = (& $Oml check --team Commerce --rules (Join-Path $Tmp 'missing-rules.json') 2> $progress) | ConvertFrom-Json
    Assert ($LASTEXITCODE -eq 2 -and $refused.error -like 'Rules file *') 'Check team rules file checked before download'
    $null = & $Oml check --team missing 2> $progress
    Assert ($LASTEXITCODE -eq 2 -and (Get-Content -LiteralPath $progress -Raw).Trim() -match 'run: oml check --in <folder>$') 'Check team fallback message'
    $failures = @{missing='matching team';Forbidden='403';NoDownload='404';Unsafe='safe package download URL';Redirect='302';Partial='404';Malformed='invalid LifeTime response';Empty='no applications'}
    foreach ($team in $failures.Keys) {
        $message = Invoke-Architecture @('--team',$team)
        Assert ($message -match $failures[$team]) "Architecture reason for $team"
        Assert (@(Get-ChildItem -LiteralPath $env:TEMP -Directory -Filter 'oml-architecture-*').Count -eq 0) "Temporary downloads removed after $team"
    }
    $null = Invoke-Architecture @('--team')
    $null = Invoke-Architecture @('--team','Commerce','--team','Commerce')
    $null = Invoke-Architecture @('--team','Commerce','--team-label','other')
    $env:OML_DEBUG_ARGS = ''
    $null = & $Oml architecture --team Commerce 2> $progress
    Assert ($LASTEXITCODE -eq 2 -and (Get-Content -LiteralPath $progress -Raw) -match 'Service Studio not found') 'Core invoked after team rewrite'
    Assert (@(Get-ChildItem -LiteralPath $env:TEMP -Directory -Filter 'oml-architecture-*').Count -eq 0) 'Temporary downloads removed after Core failure'
    $compact = & $Oml lt environments
    Assert ($LASTEXITCODE -eq 0 -and @($compact).Count -eq 1) 'LifeTime compact output'
    $pretty = & $Oml --pretty lt environments
    Assert ($LASTEXITCODE -eq 0 -and @($pretty).Count -gt 1) 'LifeTime global pretty output'
    $pretty = & $Oml lt environments --pretty
    Assert ($LASTEXITCODE -eq 0 -and @($pretty).Count -gt 1) 'LifeTime trailing pretty output'
    $r = @(Invoke-Lt @('environments'))
    Assert ($r.Count -eq 2 -and $r[0].Key -eq 'dev') 'Environment list'
    $r = @(Invoke-Lt @('apps'))
    Assert ($r.Count -eq 3 -and $r[0].environments.Count -eq 2 -and $r[0].environments[0].version -eq '1.0.0' -and $r[2].name -eq 'Unsafe' -and !$r[2].environments[0].version) 'App versions'
    $r = @(Invoke-Lt @('apps','--env','development'))
    Assert ($r[0].environments.Count -eq 1 -and $r[0].environments[0].isModified) 'App env filter'
    # lt download: GET only, the package running in one environment, never over an existing file.
    $dl = [IO.Path]::GetFullPath((Join-Path $Tmp ('lt-download-' + [guid]::NewGuid().ToString('N'))))
    $r = Invoke-Lt @('download','shop','--env','Development','--out',$dl)
    $oap = Join-Path $dl 'Shop.oap'
    $same = { param($path) [Linq.Enumerable]::SequenceEqual([byte[]][IO.File]::ReadAllBytes($path), [byte[]]$shopBytes) }
    Assert ($r.application -eq 'Shop' -and $r.environment -eq 'Development' -and $r.file -eq $oap -and $r.bytes -eq $shopBytes.Length -and (& $same $oap) -and !$r.extracted) 'Download writes the .oap'
    $r = Invoke-Lt @('download','Shop','--env','Development','--out',$dl) 2
    Assert ($r.error -like 'Output already exists:*' -and (& $same $oap)) 'Download never overwrites'
    # The download address must be on the LifeTime host: the mock offers https://other.invalid for Unsafe.
    $r = Invoke-Lt @('download','Unsafe','--env','Development','--out',$dl) 2
    Assert ($r.error -match 'safe package download URL' -and !(Test-Path -LiteralPath (Join-Path $dl 'Unsafe.oap'))) 'Download refuses another host and writes nothing'
    $r = Invoke-Lt @('download','Shop','--env','missing','--out',$dl) 2
    Assert ($r.error -match 'matching environment') 'Download unknown environment'
    $null = Invoke-Lt @('download','Shop','--out',$dl) 2
    $null = Invoke-Lt @('download','Shop','--env','Development') 2
    $null = Invoke-Lt @('download','--env','Development','--out',$dl) 2
    # --extract runs `app extract` in Core; without Service Studio the package stays and the error says so.
    $r = Invoke-Lt @('download','Orders','--env','dev','--out',$dl,'--extract') 2
    Assert ($r.file -eq (Join-Path $dl 'Orders.oap') -and (Test-Path -LiteralPath $r.file) -and $r.error -match 'not extracted' -and $r.folder -eq (Join-Path $dl 'Orders')) 'Download --extract failure keeps the .oap'
    # With Service Studio, --extract writes the modules next to the package.
    Use-RealStudio
    $r = Invoke-Lt @('download','Orders','--env','Development','--out',(Join-Path $dl 'ok'),'--extract')
    $env:OML_SERVICE_STUDIO = Join-Path $Tmp 'no-service-studio'
    Assert ($r.bytes -eq $ordersBytes -and $r.folder -eq (Join-Path $dl 'ok\Orders') -and @($r.extracted).Count -eq 1 -and $r.extracted[0].name -eq 'OrdersCore' -and
        (Test-Path -LiteralPath $r.extracted[0].path) -and !$r.error) "Download --extract writes the modules: $($r | ConvertTo-Json -Depth 4 -Compress)"
    Remove-Item -LiteralPath $dl -Recurse -Force

    # The dashboard's LifeTime actions against the same mock: read only, and the token never reaches the page.
    $dashOut = Join-Path $Tmp ('lt-dashboard-' + [guid]::NewGuid().ToString('N') + '.out')
    Use-RealStudio # for this dashboard process only: extract and the team reports run in Core
    $dashboard = Start-Process -FilePath $Oml -ArgumentList 'dashboard', '--no-open' -RedirectStandardOutput $dashOut -RedirectStandardError "$dashOut.err" -PassThru -WindowStyle Hidden
    $null = $dashboard.Handle
    $env:OML_SERVICE_STUDIO = Join-Path $Tmp 'no-service-studio'
    try {
        $first = $null
        for ($i = 0; $i -lt 100 -and -not $first; $i++) {
            Start-Sleep -Milliseconds 200
            $line = Get-Content -LiteralPath $dashOut -TotalCount 1 -ErrorAction SilentlyContinue
            if ($line) { $first = $line | ConvertFrom-Json }
            Assert (-not $dashboard.HasExited) 'oml dashboard ended early'
        }
        Assert $first 'oml dashboard printed no address'
        $base = ([uri]$first.url).GetLeftPart('Authority'); $dashKey = [Web.HttpUtility]::ParseQueryString(([uri]$first.url).Query)['key']
        $marker = $env:OML_LIFETIME_TOKEN.Split('"')[0]
        function Dash([string]$Path, $Body) {
            $response = if ($null -eq $Body) { Invoke-WebRequest "$base/api/$Path" -Headers @{ 'X-Oml-Key' = $dashKey } -UseBasicParsing }
                else { Invoke-WebRequest "$base/api/$Path" -Method Post -Headers @{ 'X-Oml-Key' = $dashKey } -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Compress) -UseBasicParsing }
            Assert (!$response.Content.Contains($marker)) "Token leaked to the page from $Path"
            return $response.Content | ConvertFrom-Json
        }
        $r = Dash 'lt/teams'
        Assert ($r.configured -and $r.code -eq 0 -and $r.output[0].name -eq 'Commerce' -and $r.cli -eq 'oml lt teams') 'Dashboard teams'
        $r = Dash 'lt/team-apps?team=Commerce'
        Assert ($r.code -eq 0 -and @($r.output).Count -eq 2 -and $r.cli -eq 'oml lt team-apps Commerce') 'Dashboard team applications'
        $r = Dash 'lt/download' @{ app = 'Shop'; env = 'Development' }
        Assert ($r.code -eq 0 -and $r.download -and $r.output.bytes -eq $shopBytes.Length -and $r.cli -eq 'oml lt download Shop --env Development --out C:\Exports' -and $r.folder.StartsWith($first.workFolder)) "Dashboard download: $($r | ConvertTo-Json -Compress)"
        $package = Invoke-WebRequest "$base/download/$($r.download)?key=$dashKey" -UseBasicParsing
        Assert ([Linq.Enumerable]::SequenceEqual([byte[]]$package.Content, [byte[]]$shopBytes) -and $package.Headers['Content-Security-Policy'] -eq 'sandbox' -and $package.Headers['Content-Disposition'] -match 'filename="Shop.oap"') 'Dashboard .oap through /download'
        Remove-Item -LiteralPath $r.folder -Recurse -Force
        $r = Dash 'lt/download' @{ app = 'Orders'; env = 'Development'; extract = $true }
        Assert ($r.code -eq 0 -and $r.download -and $r.output.extracted[0].name -eq 'OrdersCore' -and (Test-Path -LiteralPath $r.output.extracted[0].path) -and $r.cli -match '--extract$') "Dashboard download and extract: $($r | ConvertTo-Json -Depth 4 -Compress)"
        Remove-Item -LiteralPath $r.folder -Recurse -Force
        # architecture --team and check --team run in the launcher (LifeTime download first), then in Core, as on the command line.
        foreach ($kind in 'architecture', 'check') {
            $r = Dash 'lt/report' @{ team = 'Commerce'; kind = $kind }
            Assert ($r.code -eq 0 -and $r.view -and $r.download -and $r.jsonDownload -and (Test-Path -LiteralPath $r.path) -and
                $r.cli -eq "oml $kind --team Commerce --out $kind.html --json $kind.json") "Dashboard $kind --team: $($r | ConvertTo-Json -Depth 4 -Compress)"
            $json = Invoke-RestMethod "$base/download/$($r.jsonDownload)?key=$dashKey"
            Assert ($json.schema -eq 1) "Dashboard $kind --team JSON through /download"
            Remove-Item -LiteralPath (Split-Path $r.path) -Recurse -Force
        }
        Assert (@(Get-ChildItem -LiteralPath $env:TEMP -Directory -Filter 'oml-architecture-*').Count -eq 0) 'Dashboard team reports removed their temporary packages'
        try { Dash 'lt/report' @{ team = '--in' } | Out-Null; throw 'option-like team accepted' }
        catch { Assert ($_.Exception.Response.StatusCode.value__ -eq 400) "option-like team must be 400: $_" }
        foreach ($path in 'lt/deploy', 'lt/tag', 'lt/abort', 'lt/continue') {
            try { Invoke-WebRequest "$base/api/$path" -Method Post -Headers @{ 'X-Oml-Key' = $dashKey } -UseBasicParsing | Out-Null; throw "$path answered" }
            catch { Assert ($_.Exception.Response.StatusCode.value__ -eq 404) "$path must be 404" }
        }
        Write-Host 'ok   dashboard LifeTime: teams, team apps, download through /download, architecture and check --team (mock only)'
    } finally {
        if (-not $dashboard.HasExited) { Stop-Process -Id $dashboard.Id -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $dashOut, "$dashOut.err" -ErrorAction SilentlyContinue
    }
    $r = Invoke-Lt @('tag','Shop','--version','2.0.0','--description','release notes')
    Assert ($r.result -eq 'v2') 'Tag version'
    $r = Invoke-Lt @('deploy','Shop','--from','Development','--to','Test','--notes','latest','--wait')
    Assert ($r.status.DeploymentStatus -eq 'finished_successful' -and $r.deploymentKey) 'Wait for successful deployment'
    $events = Get-Content -LiteralPath $progress -Raw
    Assert ($events.Contains('Publishing modules') -and $events.Contains('running') -and $events.Contains('[REDACTED]')) 'Progress messages and redaction'
    $r = Invoke-Lt @('deploy','Shop,Orders','--from','dev','--to','test','--notes','multi')
    Assert ($r.started -and $r.applications.Count -eq 2) 'Multiple applications without wait'
    $r = Invoke-Lt @('deploy','Shop','--from','dev','--to','test','--version','2.0.0','--notes','pinned')
    Assert ($r.applications[0].versionKey -eq 'v2') 'Pinned version'
    $r = Invoke-Lt @('deploy','Shop','--from','dev','--to','test','--notes','waiting','--wait') 1
    Assert ($r.needsContinue -and $r.status.DeploymentStatus -eq 'needs_user_intervention') 'Waiting for user'
    $waiting = $r.deploymentKey
    $r = Invoke-Lt @('status',$waiting)
    Assert ($r.status.DeploymentStatus -eq 'needs_user_intervention') 'Status waiting'
    $r = Invoke-Lt @('continue',$waiting)
    Assert ($r.status.DeploymentStatus -eq 'finished_successful') 'Continue command'
    $r = Invoke-Lt @('deploy','Shop','--from','dev','--to','test','--notes','error','--wait') 1
    Assert ($r.status.DeploymentStatus -eq 'finished_with_errors' -and $r.status.Messages.Count -gt 0) 'Deployment error messages'
    $r = Invoke-Lt @('deploy','Shop','--from','dev','--to','test')
    $r = Invoke-Lt @('abort',$r.deploymentKey) 1
    Assert ($r.status.DeploymentStatus -eq 'aborted') 'Abort command'
    $r = Invoke-Lt @('deploy','Shop','--from','dev','--to','test','--notes','start-error') 1
    Assert ($r.deploymentKey -and $r.error -match '400') 'Keep key after start error'
    $r = Invoke-Lt @('apps','--env','missing') 2
    Assert ($r.error -match 'matching environment') 'Unknown environment'
    $r = Invoke-Lt @('status','forbidden') 2
    Assert ($r.error -match 'permission') 'Permission error'
    $null = Invoke-Lt @('status','invalid') 2
    $null = Invoke-Lt @('status','empty') 2
    $r = Invoke-Lt @('status','redirect') 2
    Assert ($r.error -match 'redirect') 'Redirect refusal'
    $env:OML_LIFETIME_TOKEN = 'wrong-' + [guid]::NewGuid().ToString('N')
    $null = Invoke-Architecture @('--team','Commerce')
    $r = Invoke-Lt @('environments') 2
    Assert ($r.error -match '401' -and $r.error -match 'authentication') 'Clear 401 message'
    $env:OML_LIFETIME_TOKEN = $credential
    $null = Invoke-Lt @('apps','--token',$credential) 2
    $null = Invoke-Lt @('deploy','Shop','--from','dev') 2
    $null = Invoke-Lt @('deploy','Shop','--from','dev','--to','dev') 2
    $null = Invoke-Lt @('deploy','Shop,Shop','--from','dev','--to','test') 2
    $env:OML_LIFETIME_HOST = 'http://127.0.0.1:1'
    $null = Invoke-Architecture @('--team','Commerce')
    $null = Invoke-Lt @('environments') 2
    $env:OML_LIFETIME_HOST = 'nonlocal.invalid'
    $r = Invoke-Lt @('environments') 2
    Assert ($r.error -match 'only for') 'HTTP must be loopback only'
    $env:OML_LIFETIME_HOST = '127.0.0.1:1'
    $null = Invoke-Architecture @('--team','Commerce')
    Write-Host 'ok   LifeTime (local mock only)'
} finally {
    if ($job) { Stop-Job $job; Remove-Job $job -Force }
    if ($packages) { Remove-Item -LiteralPath $packages -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $ready,$progress -ErrorAction SilentlyContinue
    foreach ($name in $names) {
        if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
}
