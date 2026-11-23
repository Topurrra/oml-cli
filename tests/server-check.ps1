# oml server: the platform server's REST API against a local mock (tests/server-mock.ps1), never a real server.
# Checks the login form, the routes, module and version lookup, pull (published, by number, refusals, no partial file),
# the "no REST route" answers that point to oml pull, redirects, settings errors, and that neither the password nor
# the token reaches stdout or stderr. Then the three commands built on it, over real modules the mock serves:
# pull --app (unchanged files, overwrite refusal, unknown application), review --server (versions by number,
# published and latest, --keep, refusals before the login) and server check (cache, history, score change). Those
# need Service Studio (oml new, apply, check and review).
param([string]$Oml = (Join-Path $PSScriptRoot '..\src\bin\Release\net10.0\oml.exe'), [string]$Tmp = (Join-Path $PSScriptRoot '..\tmp'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'server'
try {
$names = @('OML_SERVER_HOST', 'OML_SERVER_USER', 'OML_SERVER_PASSWORD', 'OML_SERVER_INSECURE_LOCALHOST', 'OML_TEST_SERVER_TOKEN', 'OML_SERVER_CACHE', 'OML_AUTHOR', 'OML_SERVER_USER_VARIABLES')
$saved = @{}; foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
# Never the person's real login from the Windows user variables, also when this test runs on its own.
$env:OML_SERVER_USER_VARIABLES = '0'
$job = $null
$ready = Join-Path $Tmp 'server-ready'
$problems = Join-Path $Tmp 'server-problems.txt'
$stderr = Join-Path $Tmp 'server-stderr.txt'
function Assert($Condition, $Message) { if (!$Condition) { throw "server: $Message" } }
function Invoke-Server([string[]]$Arguments, [int]$Exit = 0) { Invoke-Oml (@('server') + $Arguments) $Exit }
function Invoke-Oml([string[]]$Arguments, [int]$Exit = 0) {
    $output = & $Oml @Arguments 2> $stderr
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
    $env:OML_SERVER_USER_VARIABLES = '0'
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
    foreach ($a in @(@(), @('bogus'), @('versions'), @('pull', 'OrdersCore'), @('pull', 'OrdersCore', '--out'), @('modules', '--app', 'A', '--app', 'B'), @('versions', 'X', '--version', '1'),
            @('pull', '--app'), @('pull', '--app', 'Sales'), @('pull', '--app', 'Sales', '--version', '1', '--out', 'x'), @('check', '--out'), @('check', '--bogus', 'x'), @('check', '--rules', 'a', '--rules', 'b'))) {
        $r = Invoke-Server $a 2
        Assert ($r.error -match 'oml server modules') "usage for '$($a -join ' ')'"
    }
    $r = Invoke-Server @('pull', 'OrdersCore', '--out', (Join-Path $Tmp 'x.txt')) 2
    Assert ($r.error -match '\.oml file') '--out must be .oml'
    $r = Invoke-Server @('pull', 'OrdersCore', '--version', 'two', '--out', (Join-Path $Tmp 'x.oml')) 2
    Assert ($r.error -match 'version number') '--version must be a number'
    $r = Invoke-Server @('pull', '--app', 'Sales', '--out', (Join-Path $Tmp 'x.oml')) 2
    Assert ($r.error -match 'With --app, --out is a folder') 'pull --app takes a folder'
    $r = Invoke-Server @('check', '--out', (Join-Path $Tmp 'x.txt')) 2
    Assert ($r.error -match '\.html file') 'check --out must be .html'
    # review --server: wrong input is refused before the login (the host here is not even allowed).
    foreach ($case in @(@(@('review', '--server', 'SalesCore', '1'), '--server takes <Module> <v1> <v2>'), @(@('review', 'a.oml', '--server', 'SalesCore', '1', '2'), 'in place of two module files'),
            @(@('review', '--server', 'SalesCore', '1', 'two'), "'two' is not a version"), @(@('review', '--server', 'SalesCore', '1', '2', '--bogus'), 'Unknown option --bogus'),
            @(@('review', '--server', 'SalesCore', '1', '2', '--prepare', 'p', '--md', 'x.md'), '--prepare writes only'))) {
        $r = Invoke-Oml $case[0] 2
        Assert ($r.error -match [regex]::Escape($case[1])) "review --server refusal '$($case[1])': $($r | ConvertTo-Json -Compress)"
    }
    $help = (& $Oml server --help) -join "`n"
    Assert ($LASTEXITCODE -eq 0 -and $help -match 'oml server pull <Module>') 'server --help'

    # 2. Real modules for the Sales application: SalesCore v1, v2 (adds the site property ApiToken: a high finding of
    #    oml check) and v3 (v1 plus an Integer site property); SalesUI v1 and v2 (the same bytes); SalesData v1.
    $omls = Join-Path $Tmp 'omls'; New-Item -ItemType Directory $omls | Out-Null
    $downloads = Join-Path $Tmp 'downloads.txt'
    $env:OML_AUTHOR = 'Server Check'
    function New-Module($Name, $File) { & $Oml new (Join-Path $omls $File) --kind CrossDevice --name $Name | Out-Null; Assert ($LASTEXITCODE -eq 0) "oml new $Name" }
    function Apply-Spec($From, $Json, $To) {
        $spec = Join-Path $Tmp "$To.json"; Set-Content -LiteralPath $spec -Value $Json
        $a = (& $Oml apply (Join-Path $omls $From) $spec (Join-Path $omls $To)) | ConvertFrom-Json
        Assert ($a.saved -eq $true) "apply $To"
    }
    New-Module 'SalesCore' 'k-sales-core-801.oml'
    Apply-Spec 'k-sales-core-801.oml' '{"siteProperties":[{"name":"ApiToken","type":"Text","default":"\"\""}]}' 'k-sales-core-802.oml'
    Apply-Spec 'k-sales-core-801.oml' '{"siteProperties":[{"name":"PageSize","type":"Integer","default":"20"}]}' 'k-sales-core-803.oml'
    New-Module 'SalesUI' 'k-sales-ui-901.oml'
    Copy-Item (Join-Path $omls 'k-sales-ui-901.oml') (Join-Path $omls 'k-sales-ui-902.oml')
    New-Module 'SalesData' 'k-sales-data-1001.oml'
    function Downloads { if (Test-Path -LiteralPath $downloads) { @(Get-Content -LiteralPath $downloads) } else { @() } }

    # The mock: four servers on four ports.
    for ($try = 0; $try -lt 3 -and !(Test-Path -LiteralPath $ready); $try++) {
        $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $socket.Start(); $port = $socket.LocalEndpoint.Port; $socket.Stop()
        if ($port -gt 65530) { continue }
        $job = Start-Job -FilePath (Join-Path $PSScriptRoot 'server-mock.ps1') -ArgumentList $port, $ready, $problems, $omls, $downloads
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (!(Test-Path -LiteralPath $ready) -and $job.State -ne 'Failed' -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        if (!(Test-Path -LiteralPath $ready)) { Stop-Job $job; Remove-Job $job -Force; $job = $null }
    }
    Assert (Test-Path -LiteralPath $ready) 'the mock server did not start'
    $env:OML_SERVER_HOST = "127.0.0.1:$port"

    # 3. modules: every module, flat, sorted by application then name; --app filters by name or key.
    $r = Invoke-Server @('modules')
    Assert ($r.count -eq 10 -and $r.modules.Count -eq 10 -and $r.host -eq "127.0.0.1:$port") "module list: $($r | ConvertTo-Json -Depth 5 -Compress)"
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
    Assert ($r.error -match "No module of application 'Nope'" -and $r.error -match 'Applications: Orders, Sales, Shop\.' -and $r.next -eq 'oml server modules') "unknown application: $($r.error)"
    $r = Invoke-Server @('modules', '--app', 'Sale') 2
    Assert ($r.error -match 'Closest: Sales\.') "closest application names: $($r.error)"

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
    Assert ([IO.File]::ReadAllText($out) -eq 'mock-oml k-orders-core 102' -and $r.next -match 'oml inspect --header' -and $r.publish -match 'oml studio open OrdersCore --environment 127\.0\.0\.1:\d+' -and $r.publish -match 'oml pull OrdersCore') 'pulled bytes, and how to publish a change'
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

    # 5b. pull --app: every module of the application, published versions; a second pull reports unchanged files and
    #     downloads nothing; a file that is not that version from this server is never overwritten.
    $sales = Join-Path $Tmp 'sales'
    $r = Invoke-Server @('pull', '--app', 'sales', '--out', $sales)
    Assert ($r.application -eq 'Sales' -and $r.count -eq 3 -and (($r.modules.name) -join ',') -eq 'SalesCore,SalesData,SalesUI' -and !$r.skipped -and !$r.failed) "pull --app: $($r | ConvertTo-Json -Depth 5 -Compress)"
    $c = $r.modules | Where-Object name -eq 'SalesCore'
    Assert ($c.version -eq 2 -and $c.versionId -eq 802 -and $c.file -eq (Join-Path $sales 'SalesCore.oml') -and $c.bytes -eq (Get-Item $c.file).Length -and !$c.unchanged) "pull --app row: $($c | ConvertTo-Json -Compress)"
    Assert ((Get-FileHash $c.file).Hash -eq (Get-FileHash (Join-Path $omls 'k-sales-core-802.oml')).Hash) 'pull --app saved the published version'
    Assert ($r.next -match 'oml check --in' -and $r.next -match 'oml architecture --in' -and (Test-Path (Join-Path $sales 'oml-server.json'))) 'pull --app next and manifest'
    $seen = (Downloads).Count
    $r = Invoke-Server @('pull', '--app', 'Sales', '--out', $sales)
    Assert ($r.count -eq 3 -and @($r.modules | Where-Object unchanged).Count -eq 3 -and (Downloads).Count -eq $seen) "a second pull --app downloads nothing: $($r | ConvertTo-Json -Depth 5 -Compress)"
    $other = Join-Path $Tmp 'sales2'; New-Item -ItemType Directory $other | Out-Null
    Set-Content -LiteralPath (Join-Path $other 'SalesUI.oml') -Value 'my own file'
    $r = Invoke-Server @('pull', '--app', 'Sales', '--out', $other) 1
    Assert ($r.count -eq 2 -and $r.skipped.Count -eq 1 -and $r.skipped[0].name -eq 'SalesUI' -and $r.skipped[0].reason -match 'exists' -and (Get-Content -Raw (Join-Path $other 'SalesUI.oml')).Trim() -eq 'my own file') "overwrite refused: $($r | ConvertTo-Json -Depth 5 -Compress)"
    [IO.File]::AppendAllText((Join-Path $sales 'SalesData.oml'), 'x')
    $r = Invoke-Server @('pull', '--app', 'Sales', '--out', $sales) 1
    Assert ($r.skipped.name -eq 'SalesData' -and @($r.modules | Where-Object unchanged).Count -eq 2) 'a changed file is not unchanged'
    $r = Invoke-Server @('pull', '--app', 'Ordrs', '--out', (Join-Path $Tmp 'none')) 2
    Assert ($r.error -match "No module of application 'Ordrs'" -and $r.error -match 'Closest: Orders' -and !(Test-Path (Join-Path $Tmp 'none'))) "pull --app unknown application: $($r.error)"

    # 5c. review --server: two versions pulled and reviewed; the server object; a temporary folder that is removed.
    $tempReviews = Join-Path ([IO.Path]::GetTempPath()) 'oml-server'
    $before = @(Get-ChildItem -LiteralPath $tempReviews -Directory -Filter 'review-*' -ErrorAction SilentlyContinue).Count
    $md = Join-Path $Tmp 'review.md'
    $r = Invoke-Oml @('review', '--server', 'SalesCore', '1', '2', '--md', $md)
    Assert ($r.module -eq 'SalesCore' -and $r.server.module -eq 'SalesCore' -and $r.server.host -eq "127.0.0.1:$port" -and $r.server.before.version -eq 1 -and $r.server.after.version -eq 2) "review --server: $($r | ConvertTo-Json -Depth 4 -Compress)"
    Assert ($r.server.after.published -eq $true -and $r.server.after.uploadedBy -eq 'rui' -and $r.server.before.uploadedDate -and !$r.server.before.file -and $r.summary.added -ge 1 -and $r.md -eq $md -and (Test-Path $md)) "review --server output: $($r | ConvertTo-Json -Depth 4 -Compress)"
    Assert (@($r.PSObject.Properties.Name)[1] -eq 'server') "server comes right after module: $(@($r.PSObject.Properties.Name) -join ',')"
    Assert (@(Get-ChildItem -LiteralPath $tempReviews -Directory -Filter 'review-*' -ErrorAction SilentlyContinue).Count -eq $before) 'the temporary review folder is removed'
    $keep = Join-Path $Tmp 'keep'
    $r = Invoke-Oml @('review', '--server', 'k-sales-core', 'published', 'latest', '--keep', $keep)
    Assert ($r.server.before.versionId -eq 802 -and $r.server.after.versionId -eq 803 -and $r.server.before.file -eq (Join-Path $keep 'SalesCore-v2.oml') -and (Test-Path (Join-Path $keep 'SalesCore-v3.oml'))) "review --server published latest --keep: $($r.server | ConvertTo-Json -Compress)"
    $seen = (Downloads).Count
    $r = Invoke-Oml @('review', '--server', 'SalesCore', 'latest', 'published', '--keep', $keep)
    Assert ($r.server.before.version -eq 3 -and (Downloads).Count -eq $seen) 'review --keep reuses the versions already there'
    $r = Invoke-Oml @('review', '--server', 'SalesCore', '2', 'published') 2
    Assert ($r.error -match 'same version') 'the same version twice'
    $r = Invoke-Oml @('review', '--server', 'SalesCore', '1', '9') 2
    Assert ($r.error -match 'has no version 9') 'an unknown version number'
    $r = Invoke-Oml @('review', '--server', 'SalesCore', '1', '2', '--md', $downloads) 2
    Assert ($r.error -match 'Output already exists' -and (Downloads).Count -eq $seen) 'an existing output is refused before any download'

    # 5d. server check: run 1 downloads the three modules; run 2 takes them from the cache (same scores); after a new
    #     publish of SalesCore only that module is downloaded again and its score changes; history.jsonl gets a line per run.
    $env:OML_SERVER_CACHE = Join-Path $Tmp 'cache'
    $history = Join-Path $env:OML_SERVER_CACHE "127.0.0.1_$port\history.jsonl"
    $seen = (Downloads).Count
    $r = Invoke-Server @('check', '--app', 'Sales')
    $core = $r.modules | Where-Object name -eq 'SalesCore'
    Assert ($r.summary.modules -eq 3 -and $r.summary.new -eq 3 -and $r.summary.downloaded -eq 3 -and $r.summary.cached -eq 0 -and (($r.apps) -join ',') -eq 'Sales' -and $null -eq $r.summary.change) "check run 1: $($r | ConvertTo-Json -Depth 4 -Compress)"
    Assert ($core.high -ge 1 -and $core.score -lt ($r.modules | Where-Object name -eq 'SalesData').score -and $core.status -eq 'new' -and (Test-Path $r.report) -and (Test-Path $r.json) -and $r.history -eq $history) "check run 1 SalesCore: $($core | ConvertTo-Json -Compress)"
    Assert ((Downloads).Count -eq $seen + 3 -and @(Get-Content $history).Count -eq 1) 'check run 1 downloads and records'
    $r = Invoke-Server @('check', '--app', 'Sales')
    Assert ($r.summary.downloaded -eq 0 -and $r.summary.cached -eq 3 -and $r.summary.same -eq 3 -and $r.summary.change -eq 0 -and $r.summary.newFindings -eq 0 -and !$r.versionChanged -and (Downloads).Count -eq $seen + 3) "check run 2 (cache): $($r | ConvertTo-Json -Depth 4 -Compress)"
    Assert (@(Get-Content $history).Count -eq 2 -and (Get-Content $history)[1] -match '"apps":\["Sales"\]') 'check run 2 appends to the history'
    Invoke-RestMethod -Method Post "http://127.0.0.1:$port/mock/publish?key=k-sales-core&id=803" | Out-Null
    $r = Invoke-Server @('check', '--app', 'Sales')
    $core = $r.modules | Where-Object name -eq 'SalesCore'
    Assert ($r.summary.downloaded -eq 1 -and $r.summary.cached -eq 2 -and $r.versionChanged.Count -eq 1 -and $r.versionChanged[0].name -eq 'SalesCore' -and $r.versionChanged[0].from -eq 2 -and $r.versionChanged[0].to -eq 3 -and $r.versionChanged[0].uploadedBy -eq 'ana') "check run 3 (new publish): $($r | ConvertTo-Json -Depth 4 -Compress)"
    Assert ($core.status -eq 'better' -and $core.change -gt 0 -and $r.modules[0].name -eq 'SalesCore' -and $r.summary.better -eq 1 -and $r.summary.change -gt 0 -and (Downloads)[-1] -eq 'k-sales-core 803') "check run 3 SalesCore better: $($r | ConvertTo-Json -Depth 4 -Compress)"
    Invoke-RestMethod -Method Post "http://127.0.0.1:$port/mock/publish?key=k-sales-core&id=802" | Out-Null
    $out = Join-Path $Tmp 'env.html'
    $r = Invoke-Server @('check', '--app', 'Sales', '--out', $out)
    Assert ($r.modules[0].name -eq 'SalesCore' -and $r.modules[0].status -eq 'worse' -and $r.summary.worse -eq 1 -and $r.next -match 'got worse \(SalesCore\)' -and $r.report -eq $out -and (Test-Path $out) -and @(Get-Content $history).Count -eq 4) "check run 4 (worse): $($r | ConvertTo-Json -Depth 4 -Compress)"
    $r = Invoke-Server @('check', '--app', 'Sales', '--out', $out) 2
    Assert ($r.error -match 'Output already exists' -and @(Get-Content $history).Count -eq 4) 'check refuses an existing report'
    $r = Invoke-Server @('check', '--app', 'Sles') 2
    Assert ($r.error -match 'Closest: Sales') 'check: unknown application'
    # Every module: unreadable files, an empty download and a second module of the same name are listed, the rest checked.
    $env:OML_SERVER_CACHE = Join-Path $Tmp 'cache-all'
    $r = Invoke-Server @('check')
    Assert ($r.apps -eq 'all' -and $r.summary.modules -eq 3 -and ($r.failed.name -contains 'Empty') -and ($r.skipped.name -contains 'Twin') -and ($r.skipped.name -contains 'OrdersCore')) "check every module: $($r | ConvertTo-Json -Depth 4 -Compress)"

    # 5e. The dashboard's Platform server page runs the same commands: history from the Sales cache, modules,
    #     versions, a pull into a new file of the work folder, a compare of two versions, and a check run that adds
    #     to the history. No answer holds the password or the token (the mock echoes both as an uploader name).
    $env:OML_SERVER_CACHE = Join-Path $Tmp 'cache'
    $dashOut = Join-Path $Tmp 'dashboard.out'
    $dash = Start-Process -FilePath $Oml -ArgumentList 'dashboard', '--no-open' -RedirectStandardOutput $dashOut -RedirectStandardError "$dashOut.err" -PassThru -WindowStyle Hidden
    try {
        $first = $null
        for ($i = 0; $i -lt 100 -and -not $first; $i++) {
            Start-Sleep -Milliseconds 200
            if (Test-Path $dashOut) { $line = Get-Content $dashOut -TotalCount 1 -ErrorAction SilentlyContinue; if ($line) { $first = $line | ConvertFrom-Json } }
        }
        Assert $first 'the dashboard printed no address'
        $dashUrl = [uri]$first.url
        $dashBase = "http://127.0.0.1:$($dashUrl.Port)"
        $dashHeaders = @{ 'X-Oml-Key' = [Web.HttpUtility]::ParseQueryString($dashUrl.Query)['key'] }
        $secrets = @($env:OML_SERVER_PASSWORD.Substring(0, 35), $env:OML_TEST_SERVER_TOKEN)
        function Dash([string]$Path, $Body) {
            $response = if ($null -eq $Body) { Invoke-WebRequest "$dashBase/api/$Path" -Headers $dashHeaders -UseBasicParsing }
                else { Invoke-WebRequest "$dashBase/api/$Path" -Method Post -Headers $dashHeaders -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 5 -Compress) -UseBasicParsing }
            foreach ($secret in $secrets) { Assert (!$response.Content.Contains($secret)) "dashboard ${Path}: a secret in the answer" }
            return $response.Content | ConvertFrom-Json
        }
        $h = Dash 'server/history'
        Assert ($h.host -eq "127.0.0.1_$port" -and @($h.runs).Count -eq 4 -and $h.runs[3].report -and @($h.runs[3].modules).Count -eq 3 -and $h.server.configured) "dashboard history: $($h | ConvertTo-Json -Depth 3 -Compress)"
        $m = Dash 'server/modules'
        Assert ($m.code -eq 0 -and $m.output.count -eq 10 -and $m.cli -eq 'oml server modules') "dashboard modules: $($m | ConvertTo-Json -Depth 3 -Compress)"
        $v = Dash 'server/versions?module=SalesCore'
        Assert ($v.code -eq 0 -and @($v.output.versions).Count -eq 3 -and $v.cli -eq 'oml server versions SalesCore') "dashboard versions: $($v | ConvertTo-Json -Depth 3 -Compress)"
        $p = Dash 'server/pull' @{ module = 'SalesCore'; version = 2 }
        Assert ($p.code -eq 0 -and (Test-Path -LiteralPath $p.path) -and $p.path.StartsWith($first.workFolder, [StringComparison]::OrdinalIgnoreCase) -and $p.path.EndsWith('SalesCore-v2.oml') -and
            (Get-FileHash $p.path).Hash -eq (Get-FileHash (Join-Path $omls 'k-sales-core-802.oml')).Hash -and $p.cli -like 'oml server pull SalesCore --version 2 --out *') "dashboard pull: $($p | ConvertTo-Json -Depth 3 -Compress)"
        Remove-Item -LiteralPath (Split-Path $p.path) -Recurse -Force
        $rv = Dash 'server/review' @{ module = 'SalesCore'; from = '1'; to = '2' }
        Assert ($rv.code -eq 0 -and $rv.output.server -and $rv.markdown -and $rv.view -and $rv.cli -like 'oml review --server SalesCore 1 2 --md *') "dashboard review: $($rv | ConvertTo-Json -Depth 3 -Compress)"
        Remove-Item -LiteralPath $rv.folder -Recurse -Force
        $c = Dash 'server/check' @{ apps = @('Sales') }
        Assert ($c.code -eq 0 -and $c.output.summary.modules -eq 3 -and $c.cli -eq 'oml server check --app Sales' -and @((Dash 'server/history').runs).Count -eq 5) "dashboard check: $($c | ConvertTo-Json -Depth 3 -Compress)"
        $o = Dash 'overview'
        Assert ($o.score.host -eq "127.0.0.1_$port" -and $o.score.runs -eq 5 -and $null -ne $o.score.change) "dashboard overview: $($o | ConvertTo-Json -Depth 3 -Compress)"
        Dash 'shutdown' @{} | Out-Null
        Assert ($dash.WaitForExit(15000)) 'the dashboard did not stop'
    } finally { if (-not $dash.HasExited) { Stop-Process -Id $dash.Id -Force -ErrorAction SilentlyContinue } }

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
    Write-Host 'ok   server (local mock only): login form, modules, versions, pull, pull --app, review --server, server check (cache, history, scores), no REST route, redirects, settings, redaction'
} finally {
    if ($job) { Stop-Job $job; Remove-Job $job -Force }
    foreach ($name in $names) {
        if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
}
} finally { Remove-TestWorkFolder $Tmp }
