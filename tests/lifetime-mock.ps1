param([int]$Port, [string]$ReadyFile)
$ErrorActionPreference = 'Stop'
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
$plans = @{}; $next = 0
$environments = @(@{Key='dev';Name='Development'}, @{Key='test';Name='Test'})
$apps = @('Shop','Orders') | ForEach-Object {
    @{Key=$_.ToLowerInvariant();Name=$_;AppStatusInEnvs=@(
        @{EnvironmentKey='dev';BaseApplicationVersionKey='v1';IsModified=$true},
        @{EnvironmentKey='test';BaseApplicationVersionKey='v0';IsModified=$false})}
}
Set-Content -LiteralPath $ReadyFile -Value 'ready'
try {
    while ($listener.IsListening) {
        $pending = $listener.GetContextAsync()
        while (!$pending.IsCompleted) { Start-Sleep -Milliseconds 50 }
        $context = $pending.GetAwaiter().GetResult(); $request = $context.Request; $response = $context.Response
        $code = 200; $body = $null; $raw = $false
        try {
            if ($request.Headers['Authorization'] -cne "Bearer $env:OML_LIFETIME_TOKEN") {
                $code = 401; $body = @{error="Rejected $($request.Headers['Authorization'])"}
            } else {
                $path = $request.Url.AbsolutePath
                if (-not $path.StartsWith('/lifetimeapi/rest/v2/')) { throw 'Wrong API base' }
                $path = $path.Substring('/lifetimeapi/rest/v2/'.Length)
                $method = $request.HttpMethod
                $reader = [IO.StreamReader]::new($request.InputStream)
                $inputText = $reader.ReadToEnd(); $reader.Dispose()
                $payload = if ($inputText) { $inputText | ConvertFrom-Json } else { $null }
                switch -Regex ("$method $path") {
                    '^GET teams$' {
                        # Team fields use the casing in the official v2 Team response.
                        $body = @('Commerce','Forbidden','NoDownload','Unsafe','Redirect','Partial','Malformed','Empty') | ForEach-Object { @{key=$_.ToLowerInvariant();name=$_} }; break
                    }
                    '^GET teams/(commerce|forbidden|nodownload|unsafe|redirect|partial|malformed|empty)$' {
                        $team = $Matches[1]
                        if ($request.QueryString['IncludeApplications'] -ne 'true' -or $request.QueryString['IncludeUsers'] -ne 'false') { throw 'Need team applications' }
                        if ($team -eq 'forbidden') { $code = 403; break }
                        $members = switch ($team) {
                            'commerce' { @(@{Key='shop';Name='Shop'},@{Key='orders';Name='Orders'}) }
                            'partial' { @(@{Key='shop';Name='Shop'},@{Key='nodownload';Name='NoDownload'}) }
                            'empty' { @() }
                            default { @(@{Key=$team;Name=$team}) }
                        }
                        $body = @{key=$team;name=$team;applications=@($members)}; break
                    }
                    '^GET applications/(shop|orders|nodownload|unsafe|redirect|malformed)$' {
                        if ($request.QueryString['IncludeModules'] -ne 'true' -or $request.QueryString['IncludeEnvStatus'] -ne 'true') { throw 'Need modules and env status' }
                        $body = @{Key=$Matches[1];Modules=@(@{Key='module';Name='Module'});AppStatusInEnvs=@(@{EnvironmentKey='dev'},@{EnvironmentKey='test'})}; break
                    }
                    '^GET environments/dev/applications/(shop|orders|nodownload|unsafe|redirect|malformed)/content$' {
                        $app = $Matches[1]
                        if ($app -eq 'nodownload') { $code = 404; break }
                        if ($app -eq 'malformed') { $body = '<html>invalid</html>'; $raw = $true; break }
                        $url = "http://127.0.0.1:$Port/lifetimeapi/rest/v2/downloads/$app"
                        if ($app -eq 'unsafe') { $url = 'https://other.invalid/download' }
                        $body = @{url=$url;expires='2099-01-01T00:00:00Z'}; break
                    }
                    '^GET downloads/(shop|orders|redirect)$' {
                        if ($Matches[1] -eq 'redirect') { $code = 302; $response.RedirectLocation = '/lifetimeapi/rest/v2/downloads/shop'; break }
                        # Launcher-only tests inspect downloaded bytes without asking Core to open a model.
                        $body = 'mock-oap-' + $Matches[1]; $raw = $true; break
                    }
                    '^GET environments$' { $body = $environments; break }
                    '^GET applications$' {
                        if ($request.QueryString['IncludeEnvStatus'] -ne 'true') { throw 'Need env status' }
                        $body = $apps; break
                    }
                    '^GET applications/(shop|orders)/versions$' {
                        if ($request.QueryString['MaximumVersionsToReturn'] -ne '0') { throw 'Need all versions' }
                        $body = @(@{Key='v2';Version='2.0.0'}, @{Key='v1';Version='1.0.0'}); break
                    }
                    '^GET applications/(shop|orders)/versions/(v[012])$' {
                        $versionKey = $Matches[2]
                        $body = @{Key=$versionKey;Version=(@{v0='0.9.0';v1='1.0.0';v2='2.0.0'}[$versionKey])}; break
                    }
                    '^POST environments/dev/applications/shop/versions$' {
                        if ($payload.Version -ne '2.0.0' -or $payload.ChangeLog -ne 'release notes' -or $null -eq $payload.MobileVersions -or $payload.MobileVersions.Count -ne 0) { throw 'Wrong tag payload' }
                        $code = 201; $body = 'v2'; break
                    }
                    '^POST deployments$' {
                        if ($payload.SourceEnvironmentKey -ne 'dev' -or $payload.TargetEnvironmentKey -ne 'test' -or !$payload.ApplicationOperations.Count) { throw 'Wrong plan' }
                        foreach ($operation in $payload.ApplicationOperations) {
                            if ($operation.ApplicationVersionKey -notin @('v1','v2') -or $operation.DeploymentZoneKey -cne '') { throw 'Wrong operation' }
                        }
                        if ($payload.Notes -eq 'multi' -and $payload.ApplicationOperations.Count -ne 2) { throw 'Need two applications' }
                        if ($payload.Notes -eq 'pinned' -and $payload.ApplicationOperations[0].ApplicationVersionKey -ne 'v2') { throw 'Wrong pinned version' }
                        if ($payload.Notes -eq 'latest' -and $payload.ApplicationOperations[0].ApplicationVersionKey -ne 'v1') { throw 'Wrong latest version' }
                        $next++; $key = "d$next"
                        $plans[$key] = @{notes=$payload.Notes;status='saved';polls=0}
                        $code = 201; $body = $key; $raw = $true; break
                    }
                    '^POST deployments/(d\d+)/(start|continue|abort)$' {
                        $key = $Matches[1]; $command = $Matches[2]; $plan = $plans[$key]
                        if (!$plan) { $code = 404; break }
                        if ($plan.notes -eq 'start-error') { $code = 400; break }
                        if ($command -eq 'continue' -and $plan.status -ne 'needs_user_intervention') { $code = 400; break }
                        $plan.status = switch ($command) { 'abort' {'aborted'} 'continue' {'finished_successful'} default {'running'} }
                        $code = 202; break
                    }
                    '^GET deployments/(d\d+)/status$' {
                        $key = $Matches[1]; $plan = $plans[$key]
                        if (!$plan) { $code = 404; break }
                        if ($plan.status -eq 'running') {
                            $plan.polls++
                            if ($plan.notes -eq 'waiting') { $plan.status = 'needs_user_intervention' }
                            elseif ($plan.notes -eq 'error') { $plan.status = 'finished_with_errors' }
                            elseif ($plan.polls -gt 1 -or $plan.notes -ne 'latest') { $plan.status = 'finished_successful' }
                        }
                        $body = @{DeploymentStatus=$plan.status;Info='deployment_prepared';Messages=@(
                            @{Message='Publishing modules';Type='Info'}, @{Message="Detail $env:OML_LIFETIME_TOKEN";Type='Info'})}; break
                    }
                    '^GET deployments/forbidden/status$' { $code = 403; break }
                    '^GET deployments/invalid/status$' { $body = '<html>invalid</html>'; $raw = $true; break }
                    '^GET deployments/empty/status$' { $code = 204; break }
                    '^GET deployments/redirect/status$' { $code = 302; $response.RedirectLocation = '/lifetimeapi/rest/v2/environments'; break }
                    default { throw "Unexpected endpoint: $method $path" }
                }
            }
        } catch { $code = 400; $body = @{error=$_.Exception.Message} }
        $response.StatusCode = $code
        $response.ContentType = 'application/json'
        $text = if ($null -eq $body) { '' } elseif ($raw) { [string]$body } else { ConvertTo-Json -InputObject $body -Depth 20 -Compress }
        $bytes = [Text.Encoding]::UTF8.GetBytes($text)
        $response.ContentLength64 = $bytes.Length
        $response.OutputStream.Write($bytes, 0, $bytes.Length)
        $response.Close()
    }
} finally { $listener.Close() }
