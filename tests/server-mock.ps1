# A local stand-in for the platform server's REST API, for tests/server-check.ps1. Routes, login form and response
# shapes follow Service Studio 11.55.95's client code (see src/ServerApi.cs); no real server was recorded.
# Four servers on four ports: $Port full, $Port+1 no server API (every route 404), $Port+2 the API without the module
# routes (and SoapWSRestMigration without Modules), $Port+3 redirects. A request of the wrong shape answers 418 and
# is written to $ProblemFile, so the check fails with the reason. A version's source code is the file <key>-<versionId>.oml
# in $OmlDir when there is one (real modules for review and check), else a short text; each download is a line "<key> <id>"
# in $DownloadLog. POST /mock/publish?key=<key>&id=<versionId> (no login) sets a module's published version.
param([int]$Port, [string]$ReadyFile, [string]$ProblemFile, [string]$OmlDir, [string]$DownloadLog)
$ErrorActionPreference = 'Stop'
$listener = [System.Net.HttpListener]::new()
foreach ($p in 0..3) { $listener.Prefixes.Add("http://127.0.0.1:$($Port + $p)/") }
$listener.Start()
$token = $env:OML_TEST_SERVER_TOKEN
function Module($key, $name, $app, $published, $versions) {
    @{ key = $key; name = $name; publishedVersionId = $published; versions = $versions
       _embedded = @{ application = @{ key = "app-$($app.ToLowerInvariant())"; name = $app }
                      lastVersion = $versions[-1] } }
}
function Version($id, $number, $user) { @{ versionId = $id; versionNumber = $number; uploaderUsername = $user; uploadedDate = "2026-11-0$($number)T10:00:00Z" } }
$modules = @(
    (Module 'k-orders-core' 'OrdersCore' 'Orders' 102 @((Version 101 1 'ana'), (Version 102 2 'ana'), (Version 103 3 'rui')))
    (Module 'k-orders-ui' 'OrdersUI' 'Orders' 201 @((Version 201 1 'rui')))
    (Module 'k-shop-core' 'ShopCore' 'Shop' 301 @((Version 301 1 "echo $env:OML_SERVER_PASSWORD $token")))
    (Module 'k-twin-1' 'Twin' 'Shop' 401 @((Version 401 1 'ana')))
    (Module 'k-twin-2' 'Twin' 'Orders' 501 @((Version 501 1 'ana')))
    (Module 'k-forbidden' 'Forbidden' 'Shop' 601 @((Version 601 1 'ana')))
    (Module 'k-empty' 'Empty' 'Shop' 701 @((Version 701 1 'ana')))
    (Module 'k-sales-core' 'SalesCore' 'Sales' 802 @((Version 801 1 'ana'), (Version 802 2 'rui'), (Version 803 3 'ana')))
    (Module 'k-sales-ui' 'SalesUI' 'Sales' 902 @((Version 901 1 'ana'), (Version 902 2 'ana')))
    (Module 'k-sales-data' 'SalesData' 'Sales' 1001 @((Version 1001 1 'rui')))
)
Set-Content -LiteralPath $ReadyFile -Value 'ready'
try {
    while ($listener.IsListening) {
        $pending = $listener.GetContextAsync()
        while (!$pending.IsCompleted) { Start-Sleep -Milliseconds 20 }
        $context = $pending.GetAwaiter().GetResult(); $request = $context.Request; $response = $context.Response
        $mode = $request.Url.Port - $Port
        $code = 200; $body = $null; $binary = $null
        $path = $request.Url.AbsolutePath
        $query = $request.Url.Query
        try {
            if ($mode -eq 1) { $code = 404 }
            elseif ($mode -eq 3) { $code = 302; $response.RedirectLocation = "http://127.0.0.1:$Port$path" }
            elseif ($request.HttpMethod -eq 'GET' -and $path -eq '/server.api/v1/environment/handshake-properties') {
                if ($request.Headers['Authorization']) { throw 'handshake-properties takes no login' }
                $migration = if ($mode -eq 2) { 'ListReferenceable' } else { 'Modules|ModulesVersion|Download|ListReferenceable' }
                $body = @(@{ id = 'SoapWSRestMigration'; value = $migration }, @{ id = 'IdentityService_ServiceURL'; value = '' })
            }
            elseif ($request.HttpMethod -eq 'POST' -and $path -eq '/server.identity/connect/token') {
                if ($request.ContentType -notlike 'application/x-www-form-urlencoded*') { throw "token request content type $($request.ContentType)" }
                $reader = [IO.StreamReader]::new($request.InputStream); $text = $reader.ReadToEnd(); $reader.Dispose()
                $form = @{}
                foreach ($pair in $text.Split('&')) { $kv = $pair.Split('=', 2); $form[[Uri]::UnescapeDataString($kv[0].Replace('+', ' '))] = [Uri]::UnescapeDataString($kv[1].Replace('+', ' ')) }
                if ($form.grant_type -cne 'password' -or $form.client_id -cne 'service_studio' -or $form.scope -cne 'offline_access server_api allow_system_users') {
                    throw "token request form: grant_type=$($form.grant_type) client_id=$($form.client_id) scope=$($form.scope)"
                }
                if ($form.Count -ne 5) { throw "token request has $($form.Count) fields" }
                if ($form.username -cne $env:OML_SERVER_USER -or $form.password -cne $env:OML_SERVER_PASSWORD) {
                    $code = 400; $body = @{ error = 'invalid_grant'; error_description = "invalid_username_or_password $($form.password)" }
                } else {
                    $body = @{ access_token = $token; expires_in = 3600; token_type = 'Bearer'; refresh_token = "refresh-$token"; scope = $form.scope }
                }
            }
            elseif ($mode -eq 0 -and $request.HttpMethod -eq 'POST' -and $path -eq '/mock/publish') {
                $q = [Web.HttpUtility]::ParseQueryString($query)
                $m = $modules | Where-Object key -eq $q['key']
                if (!$m -or !($m.versions | Where-Object versionId -eq ([int]$q['id']))) { throw "mock publish of an unknown version: $query" }
                $m.publishedVersionId = [int]$q['id']
            }
            elseif (-not $path.StartsWith('/server.api/v1/')) { throw "unexpected route $($request.HttpMethod) $path" }
            elseif ($request.Headers['Authorization'] -cne "Bearer $token") { $code = 401 }
            elseif ($mode -eq 2) { $code = 404 }
            elseif ($request.HttpMethod -eq 'GET' -and $path -eq '/server.api/v1/modules') {
                if ([Uri]::UnescapeDataString($query) -cne '?embed=application,lastVersion') { throw "modules query $query" }
                $body = @($modules | ForEach-Object { $m = $_.Clone(); $m.Remove('versions'); $m })
            }
            elseif ($request.HttpMethod -eq 'GET' -and $path -match '^/server\.api/v1/modules/([^/]+)/versions$') {
                if ($query -cne '?limit=9999') { throw "versions query $query" }
                $m = $modules | Where-Object key -eq $Matches[1]
                if (!$m) { $code = 404 } elseif ($m.name -eq 'Forbidden') { $code = 403 } else { $body = @($m.versions) }
            }
            elseif ($request.HttpMethod -eq 'GET' -and $path -match '^/server\.api/v1/modules/([^/]+)/versions/(\d+)/source-code$') {
                if ($query) { throw "source-code query $query" }
                $m = $modules | Where-Object key -eq $Matches[1]
                $id = [int]$Matches[2]
                if (!$m -or !($m.versions | Where-Object versionId -eq $id)) { $code = 404 }
                elseif ($m.name -eq 'Empty') { $binary = [byte[]]@() }
                else {
                    $real = if ($OmlDir) { Join-Path $OmlDir "$($m.key)-$id.oml" }
                    $binary = if ($real -and (Test-Path -LiteralPath $real)) { [IO.File]::ReadAllBytes($real) } else { [Text.Encoding]::UTF8.GetBytes("mock-oml $($m.key) $id") }
                    if ($DownloadLog) { Add-Content -LiteralPath $DownloadLog -Value "$($m.key) $id" }
                }
            }
            else { throw "unexpected route $($request.HttpMethod) $path$query" }
        } catch {
            Add-Content -LiteralPath $ProblemFile -Value $_.Exception.Message
            $code = 418; $body = @{ error = $_.Exception.Message }
        }
        $response.StatusCode = $code
        [byte[]]$bytes = $binary
        if ($null -eq $binary) {
            $response.ContentType = 'application/json'
            $bytes = [Text.Encoding]::UTF8.GetBytes($(if ($null -eq $body) { '' } else { ConvertTo-Json -InputObject $body -Depth 10 -Compress }))
        } else { $response.ContentType = 'application/octet-stream' }
        $response.ContentLength64 = $bytes.Length
        if ($bytes.Length) { $response.OutputStream.Write($bytes, 0, $bytes.Length) }
        $response.Close()
    }
} finally { $listener.Close() }
