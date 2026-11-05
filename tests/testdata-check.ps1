# oml test data: the module spec (applies cleanly; refuses private actions and missing actions), and the client against a
# fake test-data API on 127.0.0.1 that answers like the generated module (key check, ledger per run, cleanup by ledger):
# allowed hosts only, create, check with expected values, cleanup with counts and absence, status, secrets refused, and
# the hook that refuses calling the API without oml. Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'testdata'
$saved = @{}; foreach ($n in 'OML_LOG', 'OML_TESTDATA_URL', 'OML_TESTDATA_KEY') { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
$server = $null
try {
  function Require($condition, $message) { if (-not $condition) { throw "testdata: $message" } }
  function Json { param([Parameter(ValueFromRemainingArguments)]$a) $out = & $Oml @a; ($out -join "`n") | ConvertFrom-Json }
  $env:OML_LOG = '0'

  # 1. The module spec: applies with no error and no new warning.
  $producer = Join-Path $Tmp 'Contacts.oml'
  & $Oml new (Join-Path $Tmp 'p0.oml') --name Contacts --kind CrossDevice | Out-Null
  $r = Json apply (Join-Path $Tmp 'p0.oml') (Join-Path $PSScriptRoot 'testdata.json') $producer
  Require $r.saved 'producer fixture'
  $spec = Join-Path $Tmp 'ContactsTestData.spec.json'
  $m = Json test data module $producer --entity Contact:AddContact:RemoveContact --out $spec
  Require ($LASTEXITCODE -eq 0 -and $m.writes -contains 'Contact' -and @($m.next).Count -ge 4) "module: $($m | ConvertTo-Json -Compress)"
  & $Oml new (Join-Path $Tmp 'td0.oml') --name ContactsTestData --kind CrossDevice | Out-Null
  $a = Json apply (Join-Path $Tmp 'td0.oml') $spec (Join-Path $Tmp 'ContactsTestData.oml')
  Require ($a.saved -and $a.summary.newErrors -eq 0 -and $a.summary.newWarnings -eq 0 -and $a.summary.problems -eq 0) "test-data module apply: $($a.summary | ConvertTo-Json -Compress)"
  Require (@($a.created) -contains 'entity TestDataLedger' -and @($a.created) -contains 'SiteProperties TestDataKey' -and @($a.created) -contains 'restService TestDataApi.CleanupContact') 'test-data module elements'
  $bad = Json test data module $producer --entity Contact:PrivateAdd:RemoveContact --out (Join-Path $Tmp 'x.json')
  Require ($LASTEXITCODE -eq 2 -and $bad.error -like '*not public*') 'a private action is refused'
  $bad = Json test data module $producer --out (Join-Path $Tmp 'y.json')
  Require ($LASTEXITCODE -eq 2 -and $bad.error -like '*--entity*') 'no entity'
  Write-Host 'ok   test data module: the spec applies cleanly; private or missing actions are refused'

  # 2. A fake API that answers like the module.
  $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
  $server = Start-Job -ArgumentList $port -ScriptBlock {
    param($port)
    $l = [Net.HttpListener]::new(); $l.Prefixes.Add("http://127.0.0.1:$port/"); $l.Start()
    $records = @{}; $ledger = [System.Collections.Generic.List[object]]::new(); $next = 100
    while ($l.IsListening) {
      $c = $l.GetContext(); $q = $c.Request; $path = $q.Url.AbsolutePath -replace '^/api/', ''
      $status = 200; $body = ''
      if ($q.Headers['X-Test-Data-Key'] -ne 'k-123') { $status = 500; $body = '{"Errors":["Wrong or missing X-Test-Data-Key"],"StatusCode":500}' }
      elseif ($q.HttpMethod -eq 'POST' -and $path -match '^runs/([^/]+)/(\w+)$') {
        $run = $Matches[1]; $entity = $Matches[2]; $text = [IO.StreamReader]::new($q.InputStream).ReadToEnd(); $rec = $text | ConvertFrom-Json
        $next++; $rec | Add-Member -NotePropertyName Id -NotePropertyValue $next -Force; $records["$entity/$next"] = $rec
        $ledger.Add([pscustomobject]@{ Run = $run; Entity = $entity; Id = $next }); $body = "$next"
      }
      elseif ($q.HttpMethod -eq 'GET' -and $path -match '^(\w+)/(\d+)$') {
        $rec = $records["$($Matches[1])/$($Matches[2])"]; $body = if ($rec) { $rec | ConvertTo-Json -Compress } else { '{"Id":0,"Name":"","Email":""}' }
      }
      elseif ($q.HttpMethod -eq 'DELETE' -and $path -match '^runs/([^/]+)/(\w+)$') {
        $run = $Matches[1]; $entity = $Matches[2]; $rows = @($ledger | Where-Object { $_.Run -eq $run -and $_.Entity -eq $entity })
        foreach ($row in $rows) { $records.Remove("$entity/$($row.Id)"); [void]$ledger.Remove($row) }
        $body = "$($rows.Count)"
      }
      else { $status = 404; $body = '{"Errors":["no such method"]}' }
      $bytes = [Text.Encoding]::UTF8.GetBytes($body); $c.Response.StatusCode = $status; $c.Response.ContentType = 'application/json'
      $c.Response.OutputStream.Write($bytes, 0, $bytes.Length); $c.Response.Close()
    }
  }
  Start-Sleep -Milliseconds 700
  $work = Join-Path $Tmp 'project'; New-Item -ItemType Directory $work | Out-Null
  Push-Location $work
  try {
    $env:OML_TESTDATA_URL = "http://127.0.0.1:$port/api"; $env:OML_TESTDATA_KEY = 'k-123'
    # 3. Only an allowed host.
    $x = Json test data create Contact --values '{"Name":"Ana"}'
    Require ($LASTEXITCODE -eq 2 -and $x.error -like '*not an allowed*' -and $x.hint -like '*oml test data allow 127.0.0.1*') "not allowed: $($x | ConvertTo-Json -Compress)"
    Json test data allow 127.0.0.1 | Out-Null
    Write-Host 'ok   test data: refused until the user allows the host'

    # 4. Create, check, a wrong value, a wrong key, a secret.
    $c1 = Json test data create Contact --values '{"Name":"Ana","Email":"ana@example.com"}'
    $c2 = Json test data create Contact --values '{"Name":"Ben"}'
    Require ($c1.id -gt 0 -and $c2.id -gt $c1.id -and $c1.run -eq $c2.run) "create: $($c1 | ConvertTo-Json -Compress) $($c2 | ConvertTo-Json -Compress)"
    $ok = Json test data check Contact $c1.id --expect '{"Name":"Ana"}'
    Require ($LASTEXITCODE -eq 0 -and $ok.ok -and $ok.values.Name -eq 'Ana') "check: $($ok | ConvertTo-Json -Compress)"
    $no = Json test data check Contact $c1.id --expect '{"Name":"Bob"}'
    Require ($LASTEXITCODE -eq 1 -and $no.mismatches[0].field -eq 'Name') "mismatch: $($no | ConvertTo-Json -Compress -Depth 4)"
    $env:OML_TESTDATA_KEY = 'wrong'
    $w = Json test data create Contact --values '{"Name":"Cy"}'
    Require ($LASTEXITCODE -eq 1 -and $w.error -like '*X-Test-Data-Key*') "wrong key: $($w | ConvertTo-Json -Compress)"
    $env:OML_TESTDATA_KEY = 'k-123'
    $s = Json test data create Contact --values '{"Name":"ghp_1234567890abcdefghijklmnopqrstuvwxyzAB"}'
    Require ($LASTEXITCODE -eq 2 -and ($s.error -like '*secret*' -or $s.error -like '*token*')) 'secret refused'
    Write-Host 'ok   test data: create, check with expected values, wrong key and secrets refused'

    # 5. Status shows the open run; cleanup deletes exactly the run's records and checks they are gone.
    $st = Json test data status
    Require (@($st.open).Count -eq 1 -and $st.open[0].records -eq 2) "status: $($st | ConvertTo-Json -Compress -Depth 4)"
    $cl = Json test data cleanup
    Require ($LASTEXITCODE -eq 0 -and $cl.ok -and $cl.entities[0].deleted -eq 2 -and $cl.entities[0].created -eq 2) "cleanup: $($cl | ConvertTo-Json -Compress -Depth 4)"
    $gone = Json test data check Contact $c1.id --absent
    Require ($LASTEXITCODE -eq 0 -and -not $gone.found) 'absent after cleanup'
    Require (@((Json test data status).open | Where-Object { $_ }).Count -eq 0) 'no open run after cleanup'
    Write-Host 'ok   test data: cleanup deletes the run''s records by the ledger and proves they are gone'

    # 6. The hook: the API only through oml test data.
    function Hook($tool, $toolInput) { $j = @{ tool_name = $tool; tool_input = $toolInput; hook_event_name = 'PreToolUse' } | ConvertTo-Json -Compress; $j | & $Oml hooks check 2>&1 | Out-Null; $LASTEXITCODE }
    Require ((Hook 'Bash' @{ command = "curl -H 'X-Test-Data-Key: k' http://127.0.0.1:$port/api/Contact/1" }) -eq 2) 'hook: curl to the test-data host must be refused'
    Require ((Hook 'PowerShell' @{ command = "Invoke-RestMethod http://127.0.0.1:$port/api/runs/x/Contact -Method Delete" }) -eq 2) 'hook: Invoke-RestMethod must be refused'
    Require ((Hook 'WebFetch' @{ url = "http://127.0.0.1:$port/api/Contact/1"; prompt = 'x' }) -eq 2) 'hook: WebFetch must be refused'
    Require ((Hook 'Bash' @{ command = "oml test data check Contact 1" }) -eq 0) 'hook: oml test data is allowed'
    Require ((Hook 'Bash' @{ command = "curl https://example.com" }) -eq 0) 'hook: other hosts are not refused'
    Write-Host 'ok   test data: the hook refuses calling the API without oml'
  } finally { Pop-Location }
} finally {
  if ($server) { Stop-Job $server -ErrorAction SilentlyContinue; Remove-Job $server -Force -ErrorAction SilentlyContinue }
  foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
  Remove-TestWorkFolder $Tmp
}
