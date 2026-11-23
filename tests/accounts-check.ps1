# Test accounts by role (.oml/test-accounts.local.json): oml browser accounts lists roles without passwords; a browser
# script logs in on a local login page with {"login": "<role>"} and with "$account:<role>.password"; the password never
# appears in any output; a missing or unfilled role is refused with what to do; the hook refuses an agent that reads the
# file. The login page is served by this script on 127.0.0.1. Needs a browser (Edge or Chrome).
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Tmp = New-TestFolder $Tmp 'accounts'
$saved = $env:OML_LOG
$server = $null
try {
  function Require($condition, $message) { if (-not $condition) { throw "accounts: $message" } }
  function Call([string[]]$a) {
    $out = & $Oml @a 2>&1
    $text = ($out | ForEach-Object { "$_" }) -join "`n"
    $json = $null; try { $json = $text | ConvertFrom-Json } catch { }
    [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text; Json = $json }
  }
  $env:OML_LOG = '0'
  $secret = 'pw-' + [guid]::NewGuid().ToString('N').Substring(0, 12)

  # A project with two roles, one not filled in yet.
  $project = Join-Path $Tmp 'proj'; New-Item -ItemType Directory -Force (Join-Path $project '.oml') | Out-Null
  '{}' | Set-Content (Join-Path $project '.oml\oml-config.json')
  $file = Join-Path $project '.oml\test-accounts.local.json'
  "{ `"accounts`": [ { `"role`": `"Manager`", `"username`": `"manager.test`", `"password`": `"$secret`" }, { `"role`": `"Clerk`", `"username`": `"clerk.test`", `"password`": `"`" } ] }" | Set-Content $file
  Push-Location $project
  try {
    $r = Call 'browser', 'accounts'
    Require ($r.Exit -eq 0 -and @($r.Json.accounts).Count -eq 2 -and ($r.Json.accounts | Where-Object role -eq 'Manager').ready -and -not ($r.Json.accounts | Where-Object role -eq 'Clerk').ready -and $r.Text -notmatch [regex]::Escape($secret)) "browser accounts: $($r.Text)"
    $r = Call 'config'
    Require ($r.Text -match 'Test accounts of the app, by role: Manager' -and $r.Text -notmatch 'Clerk' -and $r.Text -notmatch [regex]::Escape($secret)) "profile line: $($r.Text)"
    Write-Host 'ok   accounts: oml browser accounts and the profile name the roles, never a password'

    # The hook refuses an agent that reads or prints the file.
    $read = @{ tool_name = 'Read'; tool_input = @{ file_path = $file }; cwd = $project } | ConvertTo-Json -Compress
    $out = $read | & $Oml hooks check 2>&1 | Out-String
    Require ($LASTEXITCODE -eq 2 -and $out -match 'test passwords') "hook on Read: $out"
    $shell = @{ tool_name = 'Bash'; tool_input = @{ command = 'cat .oml/test-accounts.local.json' }; cwd = $project } | ConvertTo-Json -Compress
    $out = $shell | & $Oml hooks check 2>&1 | Out-String
    Require ($LASTEXITCODE -eq 2) "hook on cat: $out"
    Write-Host 'ok   accounts: the hook refuses reading the file with a tool or a shell'

    # A local login page: the form checks the account and goes to Home, which greets the user.
    $login = '<!doctype html><html><head><meta charset="utf-8"><title>Login</title></head><body><form id="f" onsubmit="event.preventDefault(); var u=document.getElementById(''b1-Input_UsernameVal'').value, p=document.getElementById(''b1-Input_PasswordVal'').value; if (p===''' + $secret + ''') { location.href=''/App/Home/?u=''+encodeURIComponent(u); } else { document.getElementById(''err'').textContent=''Invalid login''; }">' +
      '<input id="b1-Input_UsernameVal" type="text"><input id="b1-Input_PasswordVal" type="password"><button type="submit">Login</button><span id="err"></span></form></body></html>'
    $homePage = '<!doctype html><html><head><meta charset="utf-8"><title>Home</title></head><body><h1 id="hi"></h1><script>document.getElementById(''hi'').textContent=''Welcome ''+new URLSearchParams(location.search).get(''u'');</script></body></html>'
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
    $server = Start-Job -ArgumentList $port, $login, $homePage -ScriptBlock {
      param($port, $login, $homePage)
      $l = [Net.HttpListener]::new(); $l.Prefixes.Add("http://127.0.0.1:$port/"); $l.Start()
      while ($l.IsListening) {
        $c = $l.GetContext()
        $page = if ($c.Request.Url.AbsolutePath -like '/App/Home*') { $homePage } else { $login }
        $bytes = [Text.Encoding]::UTF8.GetBytes($page)
        $c.Response.ContentType = 'text/html; charset=utf-8'; $c.Response.OutputStream.Write($bytes, 0, $bytes.Length); $c.Response.Close()
      }
    }
    Start-Sleep -Milliseconds 700
    $flow = Join-Path $project 'login.json'
    "{ `"baseUrl`": `"http://127.0.0.1:$port/App/`", `"steps`": [ { `"open`": `"Login/`" }, { `"login`": `"manager`" }, { `"expect`": { `"text`": `"Welcome manager.test`" } } ] }" | Set-Content $flow
    $r = Call 'browser', 'run', $flow, '--no-session', '--out', (Join-Path $project 'out1')
    Require ($r.Exit -eq 0 -and $r.Text -notmatch [regex]::Escape($secret)) "login step: $($r.Text)"
    $flow2 = Join-Path $project 'fill.json'
    "{ `"baseUrl`": `"http://127.0.0.1:$port/App/`", `"steps`": [ { `"open`": `"Login/`" }, { `"fill`": { `"Input_UsernameVal`": `"`$account:Manager.username`", `"Input_PasswordVal`": `"`$account:Manager.password`" } }, { `"press`": `"Enter`" }, { `"expect`": { `"text`": `"Welcome manager.test`" } } ] }" | Set-Content $flow2
    $r = Call 'browser', 'run', $flow2, '--no-session', '--out', (Join-Path $project 'out2')
    Require ($r.Exit -eq 0 -and $r.Text -notmatch [regex]::Escape($secret) -and $r.Text -match '\$account:Manager\.password') "fill with `$account: $($r.Text)"
    $leak = Get-ChildItem $project -Recurse -File -Include *.json, *.md, *.txt, *.jsonc | Where-Object { $_.FullName -ne $file -and (Get-Content -Raw $_.FullName) -match [regex]::Escape($secret) }
    Require (-not $leak) "the password reached a file: $($leak.FullName -join ', ')"
    Write-Host 'ok   accounts: a browser script logs in by role (login step and $account: fill); the password is in no output or file'

    "{ `"baseUrl`": `"http://127.0.0.1:$port/App/`", `"steps`": [ { `"open`": `"Login/`" }, { `"login`": `"Clerk`" } ] }" | Set-Content $flow
    $r = Call 'browser', 'run', $flow, '--no-session', '--out', (Join-Path $project 'out3')
    Require ($r.Exit -ne 0 -and $r.Text -match 'no password yet') "unfilled role: $($r.Text)"
    "{ `"baseUrl`": `"http://127.0.0.1:$port/App/`", `"steps`": [ { `"open`": `"Login/`" }, { `"login`": `"Admin`" } ] }" | Set-Content $flow
    $r = Call 'browser', 'run', $flow, '--no-session', '--out', (Join-Path $project 'out4')
    Require ($r.Exit -ne 0 -and $r.Text -match 'Roles: Manager, Clerk') "unknown role: $($r.Text)"
    Write-Host 'ok   accounts: an unknown or unfilled role is refused with the roles there are'
  } finally { Pop-Location }
} finally {
  if ($server) { Stop-Job $server -ErrorAction SilentlyContinue; Remove-Job $server -Force -ErrorAction SilentlyContinue }
  $env:OML_LOG = $saved
  Remove-TestWorkFolder $Tmp
}
