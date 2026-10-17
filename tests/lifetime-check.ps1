param([string]$Oml = (Join-Path $PSScriptRoot '..\src\bin\Release\net8.0\oml.exe'), [string]$Tmp = (Join-Path $PSScriptRoot '..\tmp'))
$ErrorActionPreference = 'Stop'
$names = @('OML_LIFETIME_HOST','OML_LIFETIME_TOKEN','OML_LIFETIME_INSECURE_LOCALHOST','OML_SERVICE_STUDIO','TEMP','TMP')
$saved = @{}; foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$job = $null
New-Item -ItemType Directory -Force $Tmp | Out-Null
$ready = Join-Path $Tmp ('lt-ready-' + [guid]::NewGuid().ToString('N'))
$progress = Join-Path $Tmp ('lt-progress-' + [guid]::NewGuid().ToString('N'))
function Assert($Condition, $Message) { if (!$Condition) { throw $Message } }
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
try {
    $env:TEMP = [IO.Path]::GetFullPath($Tmp); $env:TMP = $env:TEMP
    # Use a random credential held only in process environments and memory.
    $env:OML_LIFETIME_TOKEN = 'mock-' + [guid]::NewGuid().ToString('N') + '"\'
    $env:OML_LIFETIME_HOST = ''
    $r = Invoke-Lt @('environments') 2
    Assert ($r.error -match 'OML_LIFETIME_HOST') 'Missing host error'
    $env:OML_LIFETIME_HOST = '127.0.0.1:1'
    $credential = $env:OML_LIFETIME_TOKEN; $env:OML_LIFETIME_TOKEN = ''
    # Missing token needs a direct check because the leak assertion requires a nonempty token.
    $missing = & $Oml lt environments | ConvertFrom-Json
    Assert ($LASTEXITCODE -eq 2 -and $missing.error -match 'OML_LIFETIME_TOKEN') 'Missing token error'
    $env:OML_LIFETIME_TOKEN = $credential
    $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $socket.Start(); $port = $socket.LocalEndpoint.Port; $socket.Stop()
    $env:OML_LIFETIME_HOST = "127.0.0.1:$port"; $env:OML_LIFETIME_INSECURE_LOCALHOST = '1'
    $job = Start-Job -FilePath (Join-Path $PSScriptRoot 'lifetime-mock.ps1') -ArgumentList $port,$ready
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (!(Test-Path -LiteralPath $ready)) {
        if ($job.State -eq 'Failed' -or [DateTime]::UtcNow -gt $deadline) { throw 'LifeTime mock did not start' }
        Start-Sleep -Milliseconds 100
    }
    $env:OML_SERVICE_STUDIO = Join-Path $Tmp 'no-service-studio'
    $compact = & $Oml lt environments
    Assert ($LASTEXITCODE -eq 0 -and @($compact).Count -eq 1) 'LifeTime compact output'
    $pretty = & $Oml --pretty lt environments
    Assert ($LASTEXITCODE -eq 0 -and @($pretty).Count -gt 1) 'LifeTime global pretty output'
    $pretty = & $Oml lt environments --pretty
    Assert ($LASTEXITCODE -eq 0 -and @($pretty).Count -gt 1) 'LifeTime trailing pretty output'
    $r = @(Invoke-Lt @('environments'))
    Assert ($r.Count -eq 2 -and $r[0].Key -eq 'dev') 'Environment list'
    $r = @(Invoke-Lt @('apps'))
    Assert ($r.Count -eq 2 -and $r[0].environments.Count -eq 2 -and $r[0].environments[0].version -eq '1.0.0') 'App versions'
    $r = @(Invoke-Lt @('apps','--env','development'))
    Assert ($r[0].environments.Count -eq 1 -and $r[0].environments[0].isModified) 'App env filter'
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
    $r = Invoke-Lt @('environments') 2
    Assert ($r.error -match '401' -and $r.error -match 'authentication') 'Clear 401 message'
    $env:OML_LIFETIME_TOKEN = $credential
    $null = Invoke-Lt @('apps','--token',$credential) 2
    $null = Invoke-Lt @('deploy','Shop','--from','dev') 2
    $null = Invoke-Lt @('deploy','Shop','--from','dev','--to','dev') 2
    $null = Invoke-Lt @('deploy','Shop,Shop','--from','dev','--to','test') 2
    $env:OML_LIFETIME_HOST = 'http://127.0.0.1:1'
    $null = Invoke-Lt @('environments') 2
    $env:OML_LIFETIME_HOST = 'nonlocal.invalid'
    $r = Invoke-Lt @('environments') 2
    Assert ($r.error -match 'only for') 'HTTP must be loopback only'
    Write-Host 'ok   LifeTime (local mock only)'
} finally {
    if ($job) { Stop-Job $job; Remove-Job $job -Force }
    Remove-Item -LiteralPath $ready,$progress -ErrorAction SilentlyContinue
    foreach ($name in $names) {
        if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
}
