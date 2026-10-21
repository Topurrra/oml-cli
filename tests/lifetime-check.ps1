param([string]$Oml = (Join-Path $PSScriptRoot '..\src\bin\Release\net8.0\oml.exe'), [string]$Tmp = (Join-Path $PSScriptRoot '..\tmp'))
$ErrorActionPreference = 'Stop'
$names = @('OML_LIFETIME_HOST','OML_LIFETIME_TOKEN','OML_LIFETIME_INSECURE_LOCALHOST','OML_SERVICE_STUDIO','OML_DEBUG_ARGS','TEMP','TMP')
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
    Assert ($r.files.Count -eq 2 -and $r.files[0].bytes -eq 13 -and $r.files[1].bytes -eq 15) 'Both current OAP files downloaded'
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
    Remove-Item -LiteralPath $ready,$progress -ErrorAction SilentlyContinue
    foreach ($name in $names) {
        if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
}
