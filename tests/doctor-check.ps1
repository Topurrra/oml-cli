# Checks `oml doctor`: the JSON shape, the checks that must pass on a working machine, the failure and warning cases,
# and that LifeTime settings never reach the output. Run from the repo root after a build.
param([string]$Oml = (Join-Path $PSScriptRoot '..\src\bin\Release\net8.0\oml.exe'))
$ErrorActionPreference = 'Stop'
$names = @('OML_SERVICE_STUDIO','OML_STUDIO_PORT','OML_LIFETIME_HOST','OML_LIFETIME_TOKEN')
$saved = @{}; foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$errFile = Join-Path ([IO.Path]::GetTempPath()) ('doctor-err-' + [guid]::NewGuid().ToString('N') + '.txt')
function Assert($Condition, $Message) { if (!$Condition) { throw $Message } }
function Set-Env([hashtable]$Values) {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $null) }
    foreach ($key in $Values.Keys) { [Environment]::SetEnvironmentVariable($key, [string]$Values[$key]) }
}
function Invoke-Doctor([string[]]$Arguments) {
    $stdout = & $Oml doctor @Arguments 2> $errFile
    $code = $LASTEXITCODE
    [pscustomobject]@{
        Code = $code
        Text = ($stdout -join "`n")
        Error = $(if (Test-Path -LiteralPath $errFile) { [IO.File]::ReadAllText($errFile) } else { '' })
    }
}
function Get-Check($Report, [string]$Name) { $Report.checks | Where-Object { $_.name -eq $Name } }
try {
    # 1. Working machine: the JSON shape, the three core checks, and an exit code that agrees with ok.
    Set-Env @{}
    $run = Invoke-Doctor @()
    $report = $run.Text | ConvertFrom-Json
    Assert ($report.checks.Count -eq 8) "expected 8 checks, got $($report.checks.Count)"
    $expected = 'serviceStudio,dotnet,model,selfTest,studioMcp,lifeTime,browser,path'
    Assert ((@($report.checks | ForEach-Object { $_.name }) -join ',') -eq $expected) 'check names or order differ'
    foreach ($check in $report.checks) {
        Assert ($check.status -in 'ok','warn','fail','skip') "$($check.name) has status '$($check.status)'"
        Assert ([bool]$check.detail) "$($check.name) has no detail"
        if ($check.status -in 'warn','fail') { Assert ([bool]$check.fix) "$($check.name) is $($check.status) without a fix" }
    }
    Assert ($report.ok -is [bool]) 'ok is not a boolean'
    Assert ($run.Code -eq $(if ($report.ok) { 0 } else { 1 })) "exit code $($run.Code) does not match ok=$($report.ok)"
    foreach ($name in 'serviceStudio','dotnet','model') { Assert ((Get-Check $report $name).status -eq 'ok') "$name is not ok on this machine" }
    Assert ($report.ok) 'doctor reports a failure on this machine'
    Write-Host 'ok   doctor JSON shape and core checks'

    # 2. --text: one line per check, fix lines indented under them.
    $lines = @((Invoke-Doctor @('--text')).Text -split "\r?\n" | Where-Object { $_.Trim() })
    Assert (@($lines | Where-Object { $_ -match '^\[' }).Count -eq 8) '--text does not print one line per check'
    Assert (@($lines | Where-Object { $_ -notmatch '^\[' -and $_ -notmatch '^ {7}\S' }).Count -eq 0) '--text printed a line that is neither a check nor an indented fix'
    Write-Host 'ok   doctor --text lines'

    # 3. Missing Service Studio folder: serviceStudio fails with the fix, the command still answers, exit 1.
    Set-Env @{ OML_SERVICE_STUDIO = (Join-Path ([IO.Path]::GetTempPath()) ('no-service-studio-' + [guid]::NewGuid().ToString('N'))) }
    $missing = Invoke-Doctor @()
    $r = $missing.Text | ConvertFrom-Json
    $sc = Get-Check $r 'serviceStudio'
    Assert ($missing.Code -eq 1 -and -not $r.ok) "missing Service Studio: exit $($missing.Code), ok $($r.ok)"
    Assert ($sc.status -eq 'fail' -and $sc.fix -match 'OML_SERVICE_STUDIO') 'serviceStudio does not fail with the fix'
    Assert ((Get-Check $r 'model').status -eq 'skip') 'model should be skipped without Service Studio'
    Assert (-not $missing.Error.Contains('Unhandled')) 'doctor crashed'
    $textMissing = (Invoke-Doctor @('--text')).Text
    Assert ($textMissing -match '(?m)^\[fail\] Service Studio not found') 'the failure line is not in --text'
    Write-Host 'ok   missing Service Studio folder fails cleanly'

    # 4. LifeTime: one setting alone is a warning, and the token never appears (stdout or stderr), in either mode.
    Set-Env @{ OML_LIFETIME_HOST = 'lifetime.example.test' }
    $r = (Invoke-Doctor @()).Text | ConvertFrom-Json
    Assert ((Get-Check $r 'lifeTime').status -eq 'warn') 'host without token is not a warning'
    $secret = 'doctor-' + [guid]::NewGuid().ToString('N')
    Set-Env @{ OML_LIFETIME_TOKEN = $secret }
    $tokenOnly = Invoke-Doctor @()
    $r = $tokenOnly.Text | ConvertFrom-Json
    Assert ((Get-Check $r 'lifeTime').status -eq 'warn') 'token without host is not a warning'
    foreach ($mode in @(@(), @('--text'))) {
        $run = Invoke-Doctor $mode
        Assert (-not $run.Text.Contains($secret) -and -not $run.Error.Contains($secret)) "token leaked in doctor $($mode -join ' ')"
    }
    Set-Env @{ OML_LIFETIME_HOST = 'lifetime.example.test'; OML_LIFETIME_TOKEN = $secret }
    $both = Invoke-Doctor @()
    Assert ((Get-Check ($both.Text | ConvertFrom-Json) 'lifeTime').status -eq 'ok') 'both settings set is not ok'
    Assert (-not $both.Text.Contains($secret)) 'token leaked with both settings set'
    Write-Host 'ok   LifeTime settings warn and never print the token'

    # 5. Service Studio's MCP server not answering: a warning with the optional fix, not a failure.
    $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $socket.Start(); $port = $socket.LocalEndpoint.Port; $socket.Stop()
    Set-Env @{ OML_STUDIO_PORT = $port }
    $r = (Invoke-Doctor @()).Text | ConvertFrom-Json
    $mcp = Get-Check $r 'studioMcp'
    Assert ($mcp.status -eq 'warn' -and $mcp.fix -match 'Start MCP server') 'a closed MCP port should warn with the fix'
    Assert ($r.ok) 'a closed MCP port should not fail doctor'
    Write-Host 'ok   Service Studio MCP server not running is a warning'

    # 6. An unknown option is a usage error.
    Set-Env @{}
    $bogus = Invoke-Doctor @('--bogus')
    Assert ($bogus.Code -eq 2) "unknown option: exit $($bogus.Code), expected 2"
    Write-Host 'ok   doctor rejects unknown options'
}
finally {
    foreach ($name in $names) {
        # A $null value reaches .NET as an empty string; that would leave the variable set to '' for later checks.
        if ($null -eq $saved[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue } else { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
    Remove-Item -LiteralPath $errFile -ErrorAction SilentlyContinue
}
