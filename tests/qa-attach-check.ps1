# oml browser --attach, the handoff step and browser raw, against a browser this script starts itself (a throwaway
# profile, remote debugging on a free port, the window off-screen) and a local page on 127.0.0.1 served by this script.
# The login page "logs in" with a button that its own timer clicks, standing in for the person, so a handoff with
# "until" passes without anyone. OML_BROWSER_SESSION moves the session state file into the test folder, so a session
# the user runs on this machine is never touched. Needs Edge or Chrome.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'qa-attach'
$savedLog = $env:OML_LOG
$savedSession = $env:OML_BROWSER_SESSION
$server = $null
$browser = $null
try {
  function Require($condition, $message) { if (-not $condition) { throw "qa-attach: $message" } }
  # stdin is not a terminal (as for an agent); stderr is kept apart, where the attention line goes.
  function Call {
    param([Parameter(ValueFromRemainingArguments)]$a)
    $err = Join-Path $Tmp ('err-' + [guid]::NewGuid().ToString('N') + '.txt')
    $out = '' | & $Oml @a 2>$err
    $text = ($out | ForEach-Object { "$_" }) -join "`n"
    $json = $null; try { $json = $text | ConvertFrom-Json } catch { }
    [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text; Json = $json; Err = "$(Get-Content $err -Raw -ErrorAction SilentlyContinue)" }
  }
  function Show($r) { "exit $($r.Exit): $($r.Text) $($r.Err)" }
  function FreePort { $p = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $p.Start(); $n = $p.LocalEndpoint.Port; $p.Stop(); $n }
  function File([string]$name, [string]$text) { $p = Join-Path $Tmp $name; Set-Content $p $text; $p }
  $env:OML_LOG = '0'
  $env:OML_BROWSER_SESSION = Join-Path $Tmp 'browser-session.json'

  # The app: a login page (with ?stay it waits for a person who never comes), a home page, and the user's own tab.
  $login = '<!doctype html><html><head><meta charset="utf-8"><title>Login</title></head><body><h1>Log in</h1>' +
    '<input id="Input_Password" type="password" aria-label="Password"><button id="b1-LoginButton" onclick="location.href=''/App/Home/''">Log in</button>' +
    '<script>if (!location.search.includes(''stay'')) setTimeout(() => document.getElementById(''b1-LoginButton'').click(), 1500)</script></body></html>'
  $homePage = '<!doctype html><html><head><meta charset="utf-8"><title>Home</title></head><body><h1>Home</h1><p id="b1-Welcome">Welcome</p></body></html>'
  $mine = '<!doctype html><html><head><meta charset="utf-8"><title>Mine</title></head><body><h1>The user''s own tab</h1></body></html>'
  $web = FreePort
  $server = Start-Job -ArgumentList $web, $login, $homePage, $mine -ScriptBlock {
    param($port, $login, $homePage, $mine)
    $l = [Net.HttpListener]::new(); $l.Prefixes.Add("http://127.0.0.1:$port/"); $l.Start()
    while ($l.IsListening) {
      $c = $l.GetContext()
      $path = $c.Request.Url.AbsolutePath
      $page = if ($path -like '/App/Login*') { $login } elseif ($path -like '/Mine*') { $mine } else { $homePage }
      $bytes = [Text.Encoding]::UTF8.GetBytes($page)
      $c.Response.ContentType = 'text/html; charset=utf-8'; $c.Response.OutputStream.Write($bytes, 0, $bytes.Length); $c.Response.Close()
    }
  }

  # The user's browser: remote debugging on a free port, a profile of its own, the window off-screen, one tab of theirs.
  # A browser whose policy turns remote debugging off (RemoteDebuggingAllowed = 0) cannot be attached to: the other one
  # is used, and with neither the check is skipped.
  function Blocked([string]$exe) {
    $key = if ($exe -match 'Edge') { 'SOFTWARE\Policies\Microsoft\Edge' } else { 'SOFTWARE\Policies\Google\Chrome' }
    foreach ($hive in 'HKLM:', 'HKCU:') { if ((Get-ItemProperty "$hive\$key" -ErrorAction SilentlyContinue).RemoteDebuggingAllowed -eq 0) { return $true } }
    $false
  }
  $exe = @($env:OML_BROWSER, "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe") |
    Where-Object { $_ -and (Test-Path $_) -and -not (Blocked $_) } | Select-Object -First 1
  if (-not $exe) { Write-Host 'skip browser --attach: no Edge or Chrome that allows remote debugging'; return }
  $debug = FreePort
  $profileDir = Join-Path $Tmp 'profile'
  $browser = Start-Process $exe -PassThru -ArgumentList "--remote-debugging-port=$debug", "--user-data-dir=`"$profileDir`"", '--no-first-run',
    '--no-default-browser-check', '--disable-sync', '--window-position=-32000,-32000', '--window-size=1000,800', "http://127.0.0.1:$web/Mine"
  # Invoke-RestMethod writes a JSON array as one object: through a variable, it is enumerated.
  function Tabs { $all = Invoke-RestMethod "http://127.0.0.1:$debug/json/list" -TimeoutSec 5; @($all | Where-Object type -eq 'page') }
  $ready = $false
  for ($clock = [Diagnostics.Stopwatch]::StartNew(); $clock.Elapsed.TotalSeconds -lt 30 -and -not $ready; ) {
    try { $ready = (Tabs | Where-Object url -like '*/Mine*').Count -eq 1 } catch { $why = "$_" }
    if (-not $ready) { Start-Sleep -Milliseconds 200 }
  }
  Require $ready "the browser did not open its DevTools port $debug with the user's tab ($why)"
  $before = (Tabs).Count

  # 1. No browser on the port: refused, with how to start one.
  $r = Call browser start --attach (FreePort)
  Require ($r.Exit -eq 2 -and $r.Json.error -like '*No browser with remote debugging*') "attach to an empty port: $(Show $r)"
  Write-Host 'ok   browser start --attach: an empty port is refused with how to start Chrome'

  # 2. Attach: a new tab of oml's own; the user's tab stays.
  $r = Call browser start --attach $debug
  Require ($r.Exit -eq 0 -and $r.Json.session -eq 'started' -and $r.Json.attached -eq "127.0.0.1:$debug") "start --attach: $(Show $r)"
  $tabs = Tabs
  Require ($tabs.Count -eq $before + 1 -and ($tabs | Where-Object url -like '*/Mine*')) "after attach: $($tabs.url -join ', ')"
  Write-Host 'ok   browser start --attach: a new tab in the user''s browser, their tab untouched'

  # 3. A handoff with "until": the attention line on stderr, the run goes on when the URL is the home page.
  $flow = File 'login.json' "{ `"baseUrl`": `"http://127.0.0.1:$web/App/`", `"steps`": [ { `"open`": `"Login/`" }, { `"handoff`": `"log in`", `"until`": { `"url`": `"/App/Home`" }, `"timeout`": 20000 }, { `"expect`": { `"text`": `"Welcome`" } } ] }"
  $r = Call browser run $flow
  Require ($r.Exit -eq 0 -and $r.Json.passed -eq 3 -and $r.Json.session -and $r.Json.steps[1].handoff -eq 'until') "handoff run: $(Show $r)"
  Require ($r.Err -match '"attention"' -and $r.Err -match 'log in') "no attention line on stderr: $(Show $r)"
  Write-Host 'ok   browser run: a handoff step prints attention and goes on when "until" holds'

  # 4. raw: a DevTools call on oml's tab, with the parameters in a file and with none; the result as JSON.
  $params = File 'title.json' '{ "expression": "document.title", "returnByValue": true }'
  $r = Call browser raw Runtime.evaluate $params
  Require ($r.Exit -eq 0 -and $r.Json.result.result.value -eq 'Home') "raw Runtime.evaluate: $(Show $r)"
  $r = Call browser raw Page.getNavigationHistory
  Require ($r.Exit -eq 0 -and @($r.Json.result.entries).Count -ge 1) "raw without parameters: $(Show $r)"
  Write-Host 'ok   browser raw: any DevTools method on the session''s page'

  # 5. raw refusals while attached: the user's own tab, their storage, the whole browser.
  $mineId = (Tabs | Where-Object url -like '*/Mine*').id
  $close = File 'close.json' "{ `"targetId`": `"$mineId`" }"
  $r = Call browser raw Target.closeTarget $close
  Require ($r.Exit -eq 2 -and $r.Json.error -like "*not oml's*") "raw Target.closeTarget of the user's tab: $(Show $r)"
  $clear = File 'clear.json' "{ `"origin`": `"http://127.0.0.1:$web`", `"storageTypes`": `"all`" }"
  $r = Call browser raw Storage.clearDataForOrigin $clear
  Require ($r.Exit -eq 2 -and $r.Json.error -like '*your own browser*') "raw Storage.clearDataForOrigin: $(Show $r)"
  $r = Call browser raw Browser.close
  Require ($r.Exit -eq 2 -and $r.Json.error -like '*whole browser*') "raw Browser.close: $(Show $r)"
  Require ((Tabs | Where-Object url -like '*/Mine*').Count -eq 1) 'the user''s tab is gone after the refusals'
  Write-Host 'ok   browser raw: refuses closing the user''s tab, clearing their storage and closing the browser'

  # 6. The raw step in a script, and the login hint on a step that fails on a login page.
  $rawFlow = File 'raw.json' "{ `"steps`": [ { `"raw`": `"Runtime.evaluate`", `"params`": { `"expression`": `"1 + 1`", `"returnByValue`": true } } ] }"
  $r = Call browser run $rawFlow
  Require ($r.Exit -eq 0 -and $r.Json.steps[0].raw.result.value -eq 2) "raw step: $(Show $r)"
  $stuck = File 'stuck.json' "{ `"baseUrl`": `"http://127.0.0.1:$web/App/`", `"timeout`": 1500, `"steps`": [ { `"open`": `"Login/?stay=1`" }, { `"expect`": { `"text`": `"Welcome`" } } ] }"
  $r = Call browser run $stuck
  Require ($r.Exit -eq 1 -and $r.Json.steps[1].hint -like '*asks to log in*') "login hint: $(Show $r)"
  Write-Host 'ok   browser run: a raw step; a step that fails on a login page says how to get past it'

  # 7. stop detaches: oml's tab is closed, the user's tab and browser stay.
  $r = Call browser stop
  Require ($r.Exit -eq 0 -and $r.Json.session -eq 'detached') "stop: $(Show $r)"
  $tabs = Tabs
  Require ($tabs.Count -eq $before -and ($tabs | Where-Object url -like '*/Mine*') -and -not $browser.HasExited) "after stop: $($tabs.url -join ', ')"
  Write-Host 'ok   browser stop: detaches, closes only oml''s tab'

  # 8. --attach on one run: a handoff without "until" and no terminal is refused, and oml's tab is closed again.
  $noUntil = File 'nountil.json' "{ `"baseUrl`": `"http://127.0.0.1:$web/App/`", `"steps`": [ { `"open`": `"Login/?stay=1`" }, { `"handoff`": `"log in`" } ] }"
  $r = Call browser run $noUntil --attach $debug
  Require ($r.Exit -eq 2 -and $r.Json.error -like '*no terminal*') "handoff without until: $(Show $r)"
  Require ((Tabs).Count -eq $before) "after run --attach: $((Tabs).url -join ', ')"
  Write-Host 'ok   browser run --attach: a handoff needs "until" without a terminal; the tab is closed after'

  # 9. A handoff in a browser nobody can see (oml's own, headless) is refused.
  $r = Call browser run $flow --no-session
  Require ($r.Exit -eq 2 -and $r.Json.error -like '*nobody can see*') "handoff headless: $(Show $r)"
  Write-Host 'ok   browser run: a handoff in a headless browser is refused'
} finally {
  try { & $Oml browser stop *> $null } catch { }
  if ($browser -and -not $browser.HasExited) { & taskkill /PID $browser.Id /T /F *> $null }
  if ($server) { Stop-Job $server -ErrorAction SilentlyContinue; Remove-Job $server -Force -ErrorAction SilentlyContinue }
  $env:OML_LOG = $savedLog
  $env:OML_BROWSER_SESSION = $savedSession
  Start-Sleep -Milliseconds 500
  Remove-TestWorkFolder $Tmp
}
