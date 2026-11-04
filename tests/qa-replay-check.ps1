# oml qa run --save: a passing run becomes a QA file that replays without an agent. A local page on 127.0.0.1 (served by
# this script, two screens that change like an OutSystems Reactive app, without a page load) is run, saved and replayed;
# a time limit that is too small fails the replay; a failing run saves nothing; an existing file is never overwritten.
# Needs a browser (Edge or Chrome); whichever engine oml qa picks on this machine is used.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'qa-replay'
$saved = $env:OML_LOG
$server = $null
try {
  function Require($condition, $message) { if (-not $condition) { throw "qa-replay: $message" } }
  function Json { param([Parameter(ValueFromRemainingArguments)]$a) $out = & $Oml @a; ($out -join "`n") | ConvertFrom-Json }
  $env:OML_LOG = '0'
  $homePage = '<!doctype html><html><head><meta charset="utf-8"><title>Home</title></head><body><div id="screen"><h1>Home</h1>' +
    '<button id="b1-SaveButton" onclick="history.pushState({}, '''', ''/App/Next/''); document.getElementById(''screen'').innerHTML=''<h1>Saved</h1><p>Done</p>''; document.title=''Next'';">Save</button></div></body></html>'
  $next = '<!doctype html><html><head><meta charset="utf-8"><title>Next</title></head><body><h1>Saved</h1><p>Done</p></body></html>'
  # A free port, then a small server in a background job.
  $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
  $server = Start-Job -ArgumentList $port, $homePage, $next -ScriptBlock {
    param($port, $homePage, $next)
    $l = [Net.HttpListener]::new(); $l.Prefixes.Add("http://127.0.0.1:$port/"); $l.Start()
    while ($l.IsListening) {
      $c = $l.GetContext()
      $page = if ($c.Request.Url.AbsolutePath -like '/App/Next*') { $next } else { $homePage }
      $bytes = [Text.Encoding]::UTF8.GetBytes($page)
      $c.Response.ContentType = 'text/html; charset=utf-8'; $c.Response.OutputStream.Write($bytes, 0, $bytes.Length); $c.Response.Close()
    }
  }
  Start-Sleep -Milliseconds 700
  $flow = Join-Path $Tmp 'flow.json'
  "{ `"baseUrl`": `"http://127.0.0.1:$port/App/`", `"steps`": [ { `"open`": `"Home/`" }, { `"click`": `"SaveButton`" }, { `"expect`": { `"text`": `"Done`" } } ] }" | Set-Content $flow
  $file = Join-Path $Tmp 'saved.json'

  # 1. A passing run is saved: time limits on the actions, the screen each ended on.
  $r = Json qa run $flow --save $file --no-session
  Require ($LASTEXITCODE -eq 0 -and $r.saved -eq $file -and $r.replay -like 'oml qa run *') "run --save: $($r | ConvertTo-Json -Compress -Depth 6)"
  $s = Get-Content $file -Raw | ConvertFrom-Json
  Require ($s.steps[0].maxMs -ge 2000 -and $s.steps[1].maxMs -ge 2000 -and $s.steps[1].expect.screen -eq 'Next' -and -not $s.steps[2].maxMs) "saved file: $($s | ConvertTo-Json -Compress -Depth 6)"
  Write-Host 'ok   qa run --save: time limits and screen checks added to a passing run'

  # 2. The saved file replays with no agent.
  $r = Json qa run $file --no-session
  Require ($LASTEXITCODE -eq 0 -and $r.passed -eq 3) "replay: $($r | ConvertTo-Json -Compress -Depth 6)"
  Write-Host 'ok   qa run: the saved file replays'

  # 3. A time limit that is too small fails the step: the app got slower than when the file was saved.
  $s.steps[0].maxMs = 1; $slow = Join-Path $Tmp 'slow.json'; $s | ConvertTo-Json -Depth 6 | Set-Content $slow
  $r = Json qa run $slow --no-session
  Require ($LASTEXITCODE -eq 1 -and $r.steps[0].error -like '*limit is 1 ms*') "maxMs: $($r | ConvertTo-Json -Compress -Depth 6)"
  Write-Host 'ok   qa run: maxMs fails a step that took longer'

  # 4. A failing run saves nothing; an existing file is never overwritten.
  $broken = Join-Path $Tmp 'broken.json'
  "{ `"baseUrl`": `"http://127.0.0.1:$port/App/`", `"steps`": [ { `"open`": `"Home/`" }, { `"expect`": { `"text`": `"Not there`" } } ], `"timeout`": 1500 }" | Set-Content $broken
  $r = Json qa run $broken --save (Join-Path $Tmp 'never.json') --no-session
  Require ($LASTEXITCODE -eq 1 -and $r.saved -like 'Nothing saved*' -and -not (Test-Path (Join-Path $Tmp 'never.json'))) "failing run: $($r.saved)"
  $r = Json qa run $flow --save $file --no-session
  Require ($LASTEXITCODE -eq 2 -and $r.error -like '*exists*') "existing file: $($r | ConvertTo-Json -Compress)"
  Write-Host 'ok   qa run --save: nothing saved from a failing run, no file overwritten'
} finally {
  if ($server) { Stop-Job $server -ErrorAction SilentlyContinue; Remove-Job $server -Force -ErrorAction SilentlyContinue }
  $env:OML_LOG = $saved
  Remove-TestWorkFolder $Tmp
}
