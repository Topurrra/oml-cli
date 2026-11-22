# oml browser run --save: a passing run becomes a browser script that replays without an agent. A local page on 127.0.0.1 (served by
# this script, two screens that change like an OutSystems Reactive app, without a page load) is run, saved and replayed;
# a time limit that is too small fails the replay; a failing run saves nothing; an existing file is never overwritten.
# Needs a browser (Edge or Chrome); whichever engine oml browser picks on this machine is used.
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
    '<button id="b1-SaveButton" onclick="history.pushState({}, '''', ''/App/Next/''); document.getElementById(''screen'').innerHTML=''<h1>Saved</h1><p>Done</p>''; document.title=''Next'';">Save</button></div>' +
    '<script>document.body.className = document.cookie.indexOf(''seen=1'') >= 0 ? ''returning'' : ''first''; document.cookie = ''seen=1; path=/'';</script></body></html>'
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
  $r = Json browser run $flow --save $file --no-session
  Require ($LASTEXITCODE -eq 0 -and $r.saved -eq $file -and $r.replay -like 'oml browser run *') "run --save: $($r | ConvertTo-Json -Compress -Depth 6)"
  $s = Get-Content $file -Raw | ConvertFrom-Json
  Require ($s.steps[0].maxMs -ge 2000 -and $s.steps[1].maxMs -ge 2000 -and $s.steps[1].expect.screen -eq 'Next' -and -not $s.steps[2].maxMs) "saved file: $($s | ConvertTo-Json -Compress -Depth 6)"
  Write-Host 'ok   browser run --save: time limits and screen checks added to a passing run'

  # 2. The saved file replays with no agent.
  $r = Json browser run $file --no-session
  Require ($LASTEXITCODE -eq 0 -and $r.passed -eq 3) "replay: $($r | ConvertTo-Json -Compress -Depth 6)"
  Write-Host 'ok   browser run: the saved file replays'

  # 3. A time limit that is too small fails the step: the app got slower than when the file was saved.
  $s.steps[0].maxMs = 1; $slow = Join-Path $Tmp 'slow.json'; $s | ConvertTo-Json -Depth 6 | Set-Content $slow
  $r = Json browser run $slow --no-session
  Require ($LASTEXITCODE -eq 1 -and $r.steps[0].error -like '*limit is 1 ms*') "maxMs: $($r | ConvertTo-Json -Compress -Depth 6)"
  Write-Host 'ok   browser run: maxMs fails a step that took longer'

  # 4. A failing run saves nothing; an existing file is never overwritten.
  $broken = Join-Path $Tmp 'broken.json'
  "{ `"baseUrl`": `"http://127.0.0.1:$port/App/`", `"steps`": [ { `"open`": `"Home/`" }, { `"expect`": { `"text`": `"Not there`" } } ], `"timeout`": 1500 }" | Set-Content $broken
  $r = Json browser run $broken --save (Join-Path $Tmp 'never.json') --no-session
  Require ($LASTEXITCODE -eq 1 -and $r.saved -like 'Nothing saved*' -and -not (Test-Path (Join-Path $Tmp 'never.json'))) "failing run: $($r.saved)"
  $r = Json browser run $flow --save $file --no-session
  Require ($LASTEXITCODE -eq 2 -and $r.error -like '*exists*') "existing file: $($r | ConvertTo-Json -Compress)"
  Write-Host 'ok   browser run --save: nothing saved from a failing run, no file overwritten'

  # 5. Screenshots an agent reads: --max-width shrinks the PNG (aspect ratio kept, never enlarged) and the output reports
  #    its size and approximate image tokens; OML_SCREENSHOT_MAX_WIDTH sets the default and 0 lifts it.
  function PngSize([string]$file) { $b = [IO.File]::ReadAllBytes($file); @(([int]$b[18] * 256 + $b[19]), ([int]$b[22] * 256 + $b[23])) }   # IHDR width and height (under 65536)
  $homeUrl = "http://127.0.0.1:$port/App/Home/"
  $shot = Join-Path $Tmp 'home.png'
  $r = Json browser page $homeUrl --screenshot $shot --max-width 400 --no-session
  $size = PngSize $shot
  Require ($LASTEXITCODE -eq 0 -and $size[0] -le 400 -and $r.screenshotSize.width -eq $size[0] -and $r.screenshotSize.height -eq $size[1] -and
    $r.screenshotSize.tokens -eq [math]::Round($size[0] * $size[1] / 750) -and $r.screenshotSize.scaledFrom -match '^\d+x\d+$') "page --max-width: $($size -join 'x'), $($r.screenshotSize | ConvertTo-Json -Compress)"
  $from = [int]($r.screenshotSize.scaledFrom -split 'x')[0]
  $env:OML_SCREENSHOT_MAX_WIDTH = '300'
  try {
    $r = Json browser page $homeUrl --screenshot $shot --no-session
    Require ($LASTEXITCODE -eq 0 -and (PngSize $shot)[0] -le 300 -and $r.screenshotSize.width -le 300) "OML_SCREENSHOT_MAX_WIDTH: $($r.screenshotSize | ConvertTo-Json -Compress)"
    $r = Json browser page $homeUrl --screenshot $shot --max-width 0 --no-session
    Require ($LASTEXITCODE -eq 0 -and (PngSize $shot)[0] -eq $from -and -not $r.screenshotSize.scaledFrom) "--max-width 0: $($r.screenshotSize | ConvertTo-Json -Compress)"
    $r = Json browser page $homeUrl --screenshot $shot --max-width 5000 --no-session
    Require ($LASTEXITCODE -eq 0 -and (PngSize $shot)[0] -eq $from) "a limit above the capture enlarged it: $($r.screenshotSize | ConvertTo-Json -Compress)"
  } finally { Remove-Item Env:OML_SCREENSHOT_MAX_WIDTH }
  $r = Json browser page $homeUrl --screenshot $shot --max-width x --no-session
  Require ($LASTEXITCODE -eq 2 -and $r.error -like '*--max-width takes a number*') "bad --max-width: $($r | ConvertTo-Json -Compress)"
  Write-Host "ok   browser page --max-width: the PNG fits and screenshotSize reports its size and tokens ($from px wide without a limit)"

  # 6. browser sweep (DevTools engine) shoots every width in one browser, not one per width: the page's cookie, set at the
  #    first width, is there at the second (a new browser has an empty profile). Each PNG fits --max-width.
  if ((Json browser page $homeUrl --no-session).engine -eq 'cdp') {
    $shots = Join-Path $Tmp 'sweep'
    $r = Json browser sweep $homeUrl --widths '390,1024' --out $shots --max-width 300 --no-session
    Require ($LASTEXITCODE -eq 0 -and $r.widths.Count -eq 2 -and $r.widths[0].body -eq 'first' -and $r.widths[1].body -eq 'returning') "sweep in one browser: $($r | ConvertTo-Json -Compress -Depth 6)"
    foreach ($w in $r.widths) {
      $size = PngSize $w.screenshot
      Require ($size[0] -le 300 -and $w.screenshotSize.width -eq $size[0] -and $w.screenshotSize.scaledFrom -like "$($w.width)x*") "sweep $($w.width): $($size -join 'x'), $($w.screenshotSize | ConvertTo-Json -Compress)"
    }
    Write-Host 'ok   browser sweep: all widths in one browser, each screenshot within --max-width'
  } else { Write-Host 'skip browser sweep: needs the DevTools engine' }

  # 7. With a running session (oml browser start), page --screenshot uses its browser ("session": true) and the caller's
  #    OML_SCREENSHOT_MAX_WIDTH, not the session's. Skipped when a session already runs: the check never stops one it did not start.
  $s = Json browser start
  if ($s.session -eq 'started') {
    try {
      $env:OML_SCREENSHOT_MAX_WIDTH = '250'
      $r = Json browser page $homeUrl --screenshot $shot
      Require ($LASTEXITCODE -eq 0 -and $r.session -eq $true -and (PngSize $shot)[0] -le 250 -and $r.screenshotSize.width -le 250) "session screenshot: $($r | ConvertTo-Json -Compress -Depth 4)"
    } finally { Remove-Item Env:OML_SCREENSHOT_MAX_WIDTH -ErrorAction SilentlyContinue; Json browser stop | Out-Null }
    Write-Host 'ok   browser page --screenshot: uses the running session, within the caller''s OML_SCREENSHOT_MAX_WIDTH'
  } else { Write-Host 'skip browser session screenshot: a session was already running' }
} finally {
  if ($server) { Stop-Job $server -ErrorAction SilentlyContinue; Remove-Job $server -Force -ErrorAction SilentlyContinue }
  $env:OML_LOG = $saved
  Remove-TestWorkFolder $Tmp
}
