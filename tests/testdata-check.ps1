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
  $m = Json test data module $producer --entity Contact:Create=AddContact,Delete=RemoveContact,Get=GetContactById,Update=ChangeContact --out $spec
  Require ($LASTEXITCODE -eq 0 -and $m.entities[0].get -eq 'GetContactById' -and $m.entities[0].update -eq 'ChangeContact' -and @($m.next).Count -ge 4) "module: $($m | ConvertTo-Json -Compress -Depth 4)"
  & $Oml new (Join-Path $Tmp 'td0.oml') --name ContactsTestData --kind CrossDevice | Out-Null
  $a = Json apply (Join-Path $Tmp 'td0.oml') $spec (Join-Path $Tmp 'ContactsTestData.oml')
  Require ($a.saved -and $a.summary.newErrors -eq 0 -and $a.summary.newWarnings -eq 0 -and $a.summary.problems -eq 0) "test-data module apply: $($a.summary | ConvertTo-Json -Compress)"
  Require (@($a.created) -contains 'SiteProperties TestDataKey' -and @($a.created) -contains 'restService TestDataApi.UpdateContact' -and @($a.created) -contains 'restService TestDataApi.DeleteContact') "test-data module elements: $($a.created -join ', ')"
  Require (-not (@($a.created) -match '^entity ')) 'the test-data module must not create an entity (its table would stay in the database)'
  # Only Create and Delete: reads through an aggregate, no update method; it applies cleanly too.
  $minimal = Join-Path $Tmp 'minimal.spec.json'
  Json test data module $producer --entity Contact:Create=AddContact,Delete=RemoveContact --out $minimal --name ContactsMin | Out-Null
  & $Oml new (Join-Path $Tmp 'min0.oml') --name ContactsMin --kind CrossDevice | Out-Null
  $a2 = Json apply (Join-Path $Tmp 'min0.oml') $minimal (Join-Path $Tmp 'ContactsMin.oml')
  Require ($a2.saved -and $a2.summary.newErrors -eq 0 -and -not (@($a2.created) -contains 'restService TestDataApi.UpdateContact')) "minimal module: $($a2.summary | ConvertTo-Json -Compress)"
  $bad = Json test data module $producer --entity Contact:Create=PrivateAdd,Delete=RemoveContact --out (Join-Path $Tmp 'x.json')
  Require ($LASTEXITCODE -eq 2 -and $bad.error -like '*not public*') 'a private action is refused'
  $bad = Json test data module $producer --out (Join-Path $Tmp 'y.json')
  Require ($LASTEXITCODE -eq 2 -and $bad.error -like '*--entity*') 'no entity'
  $bad = Json test data module $producer --entity Contact:Create=AddContact --out (Join-Path $Tmp 'z.json')
  Require ($LASTEXITCODE -eq 2 -and $bad.error -like '*Delete wrapper*') 'no Delete wrapper'
  Write-Host 'ok   test data module: no entity of its own, wrappers (Get, Update) used, applies cleanly; private or missing wrappers refused'

  # 2. A fake API that answers like the module.
  $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
  $server = Start-Job -ArgumentList $port -ScriptBlock {
    param($port)
    $l = [Net.HttpListener]::new(); $l.Prefixes.Add("http://127.0.0.1:$port/"); $l.Start()
    $records = @{}; $next = 100
    while ($l.IsListening) {
      $c = $l.GetContext(); $q = $c.Request; $path = $q.Url.AbsolutePath -replace '^/api/', ''
      $status = 200; $body = ''
      if ($q.Headers['X-Test-Data-Key'] -ne 'k-123') { $status = 500; $body = '{"Errors":["Wrong or missing X-Test-Data-Key"],"StatusCode":500}' }
      elseif ($q.HttpMethod -eq 'POST' -and $path -match '^(\w+)$') {
        $entity = $Matches[1]; $rec = [IO.StreamReader]::new($q.InputStream).ReadToEnd() | ConvertFrom-Json
        $next++; $rec | Add-Member -NotePropertyName Id -NotePropertyValue $next -Force; $records["$entity/$next"] = $rec; $body = "$next"
      }
      elseif ($q.HttpMethod -eq 'GET' -and $path -match '^(\w+)/(\d+)$') {
        $rec = $records["$($Matches[1])/$($Matches[2])"]; $body = if ($rec) { $rec | ConvertTo-Json -Compress } else { '{"Id":0,"Name":"","Email":""}' }
      }
      elseif ($q.HttpMethod -eq 'PUT' -and $path -match '^(\w+)/(\d+)$') {
        $k = "$($Matches[1])/$($Matches[2])"; $rec = [IO.StreamReader]::new($q.InputStream).ReadToEnd() | ConvertFrom-Json
        if ($records.ContainsKey($k)) { $rec | Add-Member -NotePropertyName Id -NotePropertyValue ([long]$Matches[2]) -Force; $records[$k] = $rec } else { $status = 500; $body = '{"Errors":["not found"]}' }
      }
      elseif ($q.HttpMethod -eq 'DELETE' -and $path -match '^(\w+)/(\d+)$') {
        $k = "$($Matches[1])/$($Matches[2])"; if ($records.ContainsKey($k)) { $records.Remove($k) } else { $status = 500; $body = '{"Errors":["The record does not exist"]}' }
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
    $u = Json test data update Contact $c1.id --values '{"Name":"Anna","Email":"anna@example.com"}'
    Require ($LASTEXITCODE -eq 0 -and $u.updated) "update: $($u | ConvertTo-Json -Compress)"
    Require ((Json test data check Contact $c1.id --expect '{"Name":"Anna"}').ok) 'update then check'
    $other = Json test data update Contact 999 --values '{"Name":"X"}'
    Require ($LASTEXITCODE -eq 2 -and $other.error -like '*not created by an open run*') 'update of a record the run did not create is refused'
    Write-Host 'ok   test data: create, update, check with expected values; wrong key, secrets and foreign records refused'

    # 5. Status shows the open run; cleanup deletes exactly the run's records and checks they are gone.
    $st = Json test data status
    Require (@($st.open).Count -eq 1 -and $st.open[0].records -eq 2) "status: $($st | ConvertTo-Json -Compress -Depth 4)"
    $cl = Json test data cleanup
    Require ($LASTEXITCODE -eq 0 -and $cl.ok -and @($cl.records).Count -eq 2 -and @($cl.records | Where-Object gone).Count -eq 2 -and $cl.records[0].id -eq $c2.id) "cleanup (newest first): $($cl | ConvertTo-Json -Compress -Depth 4)"
    $gone = Json test data check Contact $c1.id --absent
    Require ($LASTEXITCODE -eq 0 -and -not $gone.found) 'absent after cleanup'
    Require (@((Json test data status).open | Where-Object { $_ }).Count -eq 0) 'no open run after cleanup'
    Write-Host 'ok   test data: cleanup deletes the run''s records one by one (newest first) and proves they are gone'

    # 6. The hook: the API only through oml test data.
    function Hook($tool, $toolInput) { $j = @{ tool_name = $tool; tool_input = $toolInput; hook_event_name = 'PreToolUse' } | ConvertTo-Json -Compress; $j | & $Oml hooks check 2>&1 | Out-Null; $LASTEXITCODE }
    Require ((Hook 'Bash' @{ command = "curl -H 'X-Test-Data-Key: k' http://127.0.0.1:$port/api/Contact/1" }) -eq 2) 'hook: curl to the test-data host must be refused'
    Require ((Hook 'PowerShell' @{ command = "Invoke-RestMethod http://127.0.0.1:$port/api/Contact/1 -Method Delete" }) -eq 2) 'hook: Invoke-RestMethod must be refused'
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
