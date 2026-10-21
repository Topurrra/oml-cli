# Checks the first model command on a Service Studio build that is not recorded as checked yet: the automatic
# self-test runs once and ends (1.2.0 to 1.3.0 started it again inside itself, without end), and a self-test
# that does not finish in time stops with exit 2 and a clear error (oml dashboard reports it and keeps serving).
# It uses its own self-test record (OML_SELFTEST_RECORD), so other oml processes that read the machine-wide record in
# %LOCALAPPDATA%\oml-cli meanwhile are not affected; it runs in the alone lane because the self-test is long and CPU heavy.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Assert($Condition, $Message) { if (!$Condition) { throw $Message } }
$machine = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'oml-cli\selftest-passed.txt'
$build = (& $Oml version | ConvertFrom-Json).serviceStudio
$machineHad = (Test-Path -LiteralPath $machine) -and ([IO.File]::ReadAllLines($machine) -contains $build)
# A relative OML_SELFTEST_RECORD is a path from the oml process's working folder: every oml below starts in $Tmp.
$recordName = 'selftest-record-' + [guid]::NewGuid().ToString('N') + '.txt'
$record = Join-Path $Tmp $recordName
$env:OML_SELFTEST_RECORD = $recordName

function Forget-Build {
    if (Test-Path -LiteralPath $record) { [IO.File]::WriteAllLines($record, [string[]]@([IO.File]::ReadAllLines($record) | Where-Object { $_ -ne $build })) }
}
function Recorded { (Test-Path -LiteralPath $record) -and ([IO.File]::ReadAllLines($record) -contains $build) }
# Runs oml with a time limit; a process still running at the limit is killed and counts as a failure.
function Invoke-Limited([string[]]$Arguments, [int]$Seconds) {
    $out = Join-Path $Tmp ('selftest-out-' + [guid]::NewGuid().ToString('N') + '.txt')
    $p = Start-Process $Oml -ArgumentList ($Arguments | ForEach-Object { '"' + $_ + '"' }) -RedirectStandardOutput $out -RedirectStandardError "$out.err" -PassThru -NoNewWindow -WorkingDirectory $Tmp
    if (!$p.WaitForExit($Seconds * 1000)) { Stop-Process -Id $p.Id -Force; throw "oml $($Arguments -join ' ') did not end in $Seconds s" }
    [pscustomobject]@{ Code = $p.ExitCode; Text = [IO.File]::ReadAllText($out) }
}

try {
    # 1. An unrecorded build: the self-test runs once, passes, records the build, and the command goes on.
    Forget-Build
    $first = Join-Path $Tmp 'selftest-first.oml'
    $run = Invoke-Limited @('new', $first, '--name', 'SelfTestFirst') 300
    Assert ($run.Code -eq 0) "oml new on an unrecorded build: exit $($run.Code): $($run.Text)"
    Assert (Test-Path -LiteralPath $first) 'oml new on an unrecorded build did not write the module'
    Assert (Recorded) "the self-test did not record Service Studio $build in the relative OML_SELFTEST_RECORD ($record)"
    Write-Host 'ok   first model command on an unrecorded build runs the self-test once'

    # 2. A self-test over its time limit stops the process with exit 2 and an error, not a hang.
    Forget-Build
    $env:OML_SELFTEST_LIMIT_SECONDS = '1'
    $late = Join-Path $Tmp 'selftest-late.oml'
    $run = Invoke-Limited @('new', $late, '--name', 'SelfTestLate') 120
    Assert ($run.Code -eq 2) "self-test over its limit: exit $($run.Code), expected 2: $($run.Text)"
    Assert ((($run.Text | ConvertFrom-Json).error) -match 'did not finish in 1 s') "self-test over its limit: no clear error: $($run.Text)"
    Assert (!(Test-Path -LiteralPath $late)) 'self-test over its limit: the command still wrote its module'
    Assert (!(Recorded)) 'self-test over its limit: the build was recorded as checked'
    Write-Host 'ok   a self-test over its time limit stops with exit 2'
    # 3. In oml dashboard the same limit must not end the server: the command fails with the error, later commands are
    #    refused with it, and the dashboard keeps serving.
    Forget-Build
    $out = Join-Path $Tmp 'selftest-dashboard.txt'
    $dash = Start-Process -FilePath $Oml -ArgumentList 'dashboard', '--no-open' -WorkingDirectory $Tmp -RedirectStandardOutput $out -RedirectStandardError "$out.err" -PassThru -WindowStyle Hidden
    try {
        $address = $null
        for ($i = 0; $i -lt 100 -and -not $address; $i++) {
            Start-Sleep -Milliseconds 200
            if (Test-Path $out) { $line = Get-Content $out -TotalCount 1 -ErrorAction SilentlyContinue; if ($line) { $address = [uri]($line | ConvertFrom-Json).url } }
            Assert (-not $dash.HasExited) "oml dashboard ended early: $(Get-Content "$out.err" -Raw -ErrorAction SilentlyContinue)"
        }
        Assert $address 'oml dashboard printed no address'
        $api = "http://127.0.0.1:$($address.Port)/api"
        $headers = @{ 'X-Oml-Key' = [Web.HttpUtility]::ParseQueryString($address.Query)['key'] }
        $body = @{ path = $first } | ConvertTo-Json -Compress
        $inspect = Invoke-RestMethod "$api/inspect" -Method Post -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 120
        Assert ($inspect.code -eq 2 -and $inspect.output.error -match 'did not finish in 1 s' -and $inspect.output.error -match 'restart oml dashboard') "dashboard inspect past the limit: $($inspect | ConvertTo-Json -Depth 5 -Compress)"
        Assert (-not $dash.HasExited) 'the self-test limit ended oml dashboard'
        $again = Invoke-RestMethod "$api/inspect" -Method Post -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 60
        Assert ($again.code -eq 2 -and $again.output.error -match 'did not finish in 1 s') "a later dashboard command was not refused: $($again | ConvertTo-Json -Depth 5 -Compress)"
        $doctor = Invoke-RestMethod "$api/doctor" -Headers $headers -TimeoutSec 60
        Assert ($doctor.code -eq 2 -and $doctor.output.error -match 'did not finish in 1 s') "a launcher command (doctor) was not refused: $($doctor | ConvertTo-Json -Depth 5 -Compress)"
        Assert ((Invoke-WebRequest "$api/info" -Headers $headers -UseBasicParsing -TimeoutSec 30).StatusCode -eq 200) 'the dashboard stopped serving'
        Write-Host 'ok   a self-test over its time limit in oml dashboard fails the command and the dashboard keeps serving'
    }
    finally { if (-not $dash.HasExited) { Stop-Process -Id $dash.Id -Force } }
}
finally {
    $env:OML_SELFTEST_LIMIT_SECONDS = $null
    $env:OML_SELFTEST_RECORD = $null
    if (Test-Path -LiteralPath $record) { Remove-Item -LiteralPath $record }
}
# Without OML_SELFTEST_RECORD the machine-wide record is used, and this check never removed the build from it.
Assert (!$machineHad -or ([IO.File]::ReadAllLines($machine) -contains $build)) "the machine-wide self-test record lost Service Studio $build"
function SelfTestStatus { ((& $Oml doctor | ConvertFrom-Json).checks | Where-Object { $_.name -eq 'selfTest' }).status }
$env:OML_SELFTEST_RECORD = Join-Path $Tmp ('selftest-none-' + [guid]::NewGuid().ToString('N') + '.txt')
try { $own = SelfTestStatus } finally { $env:OML_SELFTEST_RECORD = $null }
Assert ($own -eq 'warn') "oml doctor with an empty OML_SELFTEST_RECORD: selfTest $own, expected warn"
if ($machineHad) { $normal = SelfTestStatus; Assert ($normal -eq 'ok') "oml doctor without OML_SELFTEST_RECORD: selfTest $normal, expected ok" }
Write-Host 'ok   the check used its own self-test record; without OML_SELFTEST_RECORD the machine-wide record is read and kept'

