# oml server: the platform server's REST API against a local mock (tests/server-mock.ps1), never a real server.
# Checks the login form, the routes, module and version lookup, pull (published, by number, refusals, no partial file),
# the "no REST route" answers that point to oml pull, redirects, settings errors, and that neither the password nor
# the token reaches stdout or stderr. Needs no Service Studio.
param([string]$Oml = (Join-Path $PSScriptRoot '..\src\bin\Release\net10.0\oml.exe'), [string]$Tmp = (Join-Path $PSScriptRoot '..\tmp'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'server'
try {
$names = @('OML_SERVER_HOST', 'OML_SERVER_USER', 'OML_SERVER_PASSWORD', 'OML_SERVER_INSECURE_LOCALHOST', 'OML_TEST_SERVER_TOKEN')
$saved = @{}; foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$job = $null
$ready = Join-Path $Tmp 'server-ready'
$problems = Join-Path $Tmp 'server-problems.txt'
$stderr = Join-Path $Tmp 'server-stderr.txt'
function Assert($Condition, $Message) { if (!$Condition) { throw "server: $Message" } }
function Invoke-Server([string[]]$Arguments, [int]$Exit = 0) {
    $output = & $Oml server @Arguments 2> $stderr
    $code = $LASTEXITCODE
    $text = $output -join "`n"
    $errors = Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue
    foreach ($secret in $env:OML_SERVER_PASSWORD, $env:OML_TEST_SERVER_TOKEN) {
        if ($secret) { Assert (!$text.Contains($secret) -and (!$errors -or !$errors.Contains($secret))) "a secret leaked for $($Arguments -join ' '): $text" }
    }
    Assert ($code -eq $Exit) "expected exit $Exit, got $code for $($Arguments -join ' '): $text"
    return ($text | ConvertFrom-Json)
}
try {
    # 1. Settings and usage, before any request.
    foreach ($name in $names) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
    $r = Invoke-Server @('modules') 2
    Assert ($r.error -match 'OML_SERVER_HOST, OML_SERVER_USER, OML_SERVER_PASSWORD') 'missing settings are all named'
    $env:OML_SERVER_USER = 'dev.user'
    # A password with quotes, a backslash, & and = checks the form encoding; the token is random per run.
    $env:OML_SERVER_PASSWORD = 'pw-' + [guid]::NewGuid().ToString('N') + '"\&='
    $env:OML_TEST_SERVER_TOKEN = 'tok-' + [guid]::NewGuid().ToString('N')
    $r = Invoke-Server @('modules') 2
    Assert ($r.error -match '^Set OML_SERVER_HOST in') 'only the missing setting is named'
    foreach ($bad in 'https://dev.example.com', 'dev.example.com/path', 'user@dev.example.com', 'dev example') {
        $env:OML_SERVER_HOST = $bad
        $r = Invoke-Server @('modules') 2
        Assert ($r.error -match 'OML_SERVER_HOST must be a hostname') "bad host $bad refused"
    }
    $env:OML_SERVER_HOST = 'dev.example.invalid'; $env:OML_SERVER_INSECURE_LOCALHOST = '1'
    $r = Invoke-Server @('modules') 2
    Assert ($r.error -match 'only for 127.0.0.1 or localhost') 'HTTP only for loopback'
    foreach ($a in @(@(), @('bogus'), @('versions'), @('pull', 'OrdersCore'), @('pull', 'OrdersCore', '--out'), @('modules', '--app', 'A', '--app', 'B'), @('versions', 'X', '--version', '1'))) {
        $r = Invoke-Server $a 2
        Assert ($r.error -match 'oml server modules') "usage for '$($a -join ' ')'"
    }
    $r = Invoke-Server @('pull', 'OrdersCore', '--out', (Join-Path $Tmp 'x.txt')) 2
    Assert ($r.error -match '\.oml file') '--out must be .oml'
    $r = Invoke-Server @('pull', 'OrdersCore', '--version', 'two', '--out', (Join-Path $Tmp 'x.oml')) 2
    Assert ($r.error -match 'version number') '--version must be a number'
    $help = (& $Oml server --help) -join "`n"
    Assert ($LASTEXITCODE -eq 0 -and $help -match 'oml server pull <Module>') 'server --help'

    # 2. The mock: four servers on four ports.
    for ($try = 0; $try -lt 3 -and !(Test-Path -LiteralPath $ready); $try++) {
        $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $socket.Start(); $port = $socket.LocalEndpoint.Port; $socket.Stop()
        if ($port -gt 65530) { continue }
        $job = Start-Job -FilePath (Join-Path $PSScriptRoot 'server-mock.ps1') -ArgumentList $port, $ready, $problems
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (!(Test-Path -LiteralPath $ready) -and $job.State -ne 'Failed' -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        if (!(Test-Path -LiteralPath $ready)) { Stop-Job $job; Remove-Job $job -Force; $job = $null }
    }
    Assert (Test-Path -LiteralPath $ready) 'the mock server did not start'
    $env:OML_SERVER_HOST = "127.0.0.1:$port"

    # 3. modules: every module, flat, sorted by application then name; --app filters by name or key.
    $r = Invoke-Server @('modules')
    Assert ($r.count -eq 7 -and $r.modules.Count -eq 7 -and $r.host -eq "127.0.0.1:$port") "module list: $($r | ConvertTo-Json -Depth 5 -Compress)"
    $core = $r.modules | Where-Object name -eq 'OrdersCore'
    Assert ($core.key -eq 'k-orders-core' -and $core.application -eq 'Orders' -and $core.applicationKey -eq 'app-orders' -and $core.publishedVersionId -eq 102 -and $core.lastVersion -eq 3 -and $core.lastVersionId -eq 103 -and $core.uploadedBy -eq 'rui') "module row: $($core | ConvertTo-Json -Compress)"
    Assert ($r.modules[0].application -eq 'Orders' -and $r.modules[-1].application -eq 'Shop') 'sorted by application'
    $shop = $r.modules | Where-Object name -eq 'ShopCore'
    Assert ($shop.uploadedBy -match '\[REDACTED\].*\[REDACTED\]') "a server text that holds the password and the token is redacted: $($shop.uploadedBy)"
    $r = Invoke-Server @('modules', '--app', 'orders')
    Assert ($r.count -eq 3 -and !($r.modules | Where-Object application -ne 'Orders')) '--app by name, any case'
    $r = Invoke-Server @('modules', '--app', 'app-shop')
    Assert ($r.count -eq 4) '--app by key'
    $r = Invoke-Server @('modules', '--app', 'Nope') 2
    Assert ($r.error -match "No module of application 'Nope'") 'unknown application'

    # 4. versions: newest first, the published one marked; a name or a key; ambiguous names and 403.
    $r = Invoke-Server @('versions', 'orderscore')
    Assert ($r.module -eq 'OrdersCore' -and $r.key -eq 'k-orders-core' -and $r.count -eq 3 -and $r.versions[0].version -eq 3 -and $r.versions[0].versionId -eq 103) "versions: $($r | ConvertTo-Json -Depth 5 -Compress)"
    Assert ((@($r.versions | Where-Object published).versionId -join ',') -eq '102' -and $r.versions[1].uploadedBy -eq 'ana' -and $r.next -match 'oml server pull OrdersCore --version') 'published version marked'
    $r = Invoke-Server @('versions', 'Twin') 2
    Assert ($r.error -match "2 modules are named 'Twin'" -and $r.error -match 'k-twin-1, k-twin-2') 'ambiguous name lists the keys'
    $r = Invoke-Server @('versions', 'k-twin-2')
    Assert ($r.application -eq 'Orders' -and $r.count -eq 1) 'a key picks one of two same-name modules'
    $r = Invoke-Server @('versions', 'Forbidden') 2
    Assert ($r.error -match 'HTTP 403') 'permission denied'
    $r = Invoke-Server @('versions', 'Missing') 2
    Assert ($r.error -match "Module 'Missing' is not on this server" -and $r.next -eq 'oml server modules') 'unknown module'

    # 5. pull: the published version by default, a version by number, never over a file, no file on failure.
    $out = Join-Path $Tmp 'pulled\OrdersCore.oml'
    $r = Invoke-Server @('pull', 'OrdersCore', '--out', $out)
    Assert ($r.versionId -eq 102 -and $r.version -eq 2 -and $r.published -eq $true -and $r.file -eq $out -and $r.bytes -eq (Get-Item $out).Length) "pull published: $($r | ConvertTo-Json -Compress)"
    Assert ([IO.File]::ReadAllText($out) -eq 'mock-oml k-orders-core 102' -and $r.next -match 'oml header') 'pulled bytes'
    $r = Invoke-Server @('pull', 'OrdersCore', '--out', $out) 2
    Assert ($r.error -match 'Output already exists' -and [IO.File]::ReadAllText($out) -eq 'mock-oml k-orders-core 102') 'never overwrites'
    $old = Join-Path $Tmp 'OrdersCore-v1.oml'
    $r = Invoke-Server @('pull', 'k-orders-core', '--version', '1', '--out', $old)
    Assert ($r.versionId -eq 101 -and $r.version -eq 1 -and $r.published -eq $false -and [IO.File]::ReadAllText($old) -eq 'mock-oml k-orders-core 101') 'pull by version number'
    $r = Invoke-Server @('pull', 'OrdersCore', '--version', '9', '--out', (Join-Path $Tmp 'v9.oml')) 2
    Assert ($r.error -match 'has no version 9' -and !(Test-Path (Join-Path $Tmp 'v9.oml'))) 'unknown version number'
    $empty = Join-Path $Tmp 'Empty.oml'
    $r = Invoke-Server @('pull', 'Empty', '--out', $empty) 2
    Assert ($r.error -match 'empty file' -and !(Test-Path $empty)) 'an empty download leaves no file'

    # 6. Login refused: wrong password; the error never repeats it (the mock echoes it in its answer).
    $good = $env:OML_SERVER_PASSWORD
    $env:OML_SERVER_PASSWORD = 'wrong-' + [guid]::NewGuid().ToString('N')
    $r = Invoke-Server @('modules') 2
    Assert ($r.error -match 'Login refused \(HTTP 400\)' -and $r.error -match 'OML_SERVER_PASSWORD') 'login refused'
    $env:OML_SERVER_PASSWORD = $good

    # 7. No REST route: no server API at all, then the API without the module routes; both point to oml pull.
    $env:OML_SERVER_HOST = "127.0.0.1:$($port + 1)"
    $r = Invoke-Server @('pull', 'OrdersCore', '--out', (Join-Path $Tmp 'none.oml')) 2
    Assert ($r.noRestRoute -eq $true -and $r.error -match 'no REST server API' -and $r.error -match 'oml pull') "no server API: $($r | ConvertTo-Json -Compress)"
    $env:OML_SERVER_HOST = "127.0.0.1:$($port + 2)"
    $r = Invoke-Server @('pull', 'OrdersCore', '--out', (Join-Path $Tmp 'none.oml')) 2
    Assert ($r.noRestRoute -eq $true -and $r.error -match 'module list' -and $r.error -match 'oml pull' -and $r.warning -match 'SoapWSRestMigration=ListReferenceable has no Download') "no module route: $($r | ConvertTo-Json -Compress)"
    $r = Invoke-Server @('modules') 2
    Assert ($r.noRestRoute -eq $true -and $r.error -match 'oml studio' -and $r.warning -match 'has no Modules') 'no module route for the list'
    Assert (!(Test-Path (Join-Path $Tmp 'none.oml'))) 'no file without a route'

    # 8. Redirects are refused, and a closed port is a plain connection error.
    $env:OML_SERVER_HOST = "127.0.0.1:$($port + 3)"
    $r = Invoke-Server @('modules') 2
    Assert ($r.error -match 'redirect') 'redirect refused'
    $env:OML_SERVER_HOST = '127.0.0.1:1'
    $r = Invoke-Server @('modules') 2
    Assert ($r.error -match 'Request to the server failed') 'connection failure'

    $mockProblems = Get-Content -LiteralPath $problems -Raw -ErrorAction SilentlyContinue
    Assert (!$mockProblems) "the mock saw requests of the wrong shape: $mockProblems"
    Write-Host 'ok   server (local mock only): login form, modules, versions, pull, no REST route, redirects, settings, redaction'
} finally {
    if ($job) { Stop-Job $job; Remove-Job $job -Force }
    foreach ($name in $names) {
        if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
}
} finally { Remove-TestWorkFolder $Tmp }
