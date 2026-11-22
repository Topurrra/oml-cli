param([string]$Oml, [string]$Tmp)
# oml's Service Studio MCP client against a local mock (tests/studio-mock.ps1), never a real Service Studio:
# an early contract 1 build (listApps) and a newer one (listOpenModules, searchApps, openModule, "MCP contract 2").
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Tmp = New-TestFolder $Tmp 'studio'
function Free-Port {
  $l = [System.Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); $p
}
function Assert($ok, $what) { if (-not $ok) { throw $what } }
$savedPort = $env:OML_STUDIO_PORT
$job = $null; $tokens = @()
try {
  # Two free ports in a row.
  do { $port = Free-Port; $next = $null; try { $l = [System.Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port + 1); $l.Start(); $l.Stop(); $next = $port + 1 } catch { } } while (-not $next)
  $ready = Join-Path $Tmp 'ready.txt'; $problems = Join-Path $Tmp 'problems.txt'; $calls = Join-Path $Tmp 'calls.txt'
  Set-Content $calls ''
  $job = Start-Job -FilePath (Join-Path $PSScriptRoot 'studio-mock.ps1') -ArgumentList $port, $ready, $problems, $calls
  for ($i = 0; $i -lt 100 -and -not (Test-Path $ready); $i++) { Start-Sleep -Milliseconds 100 }
  if (-not (Test-Path $ready)) { throw "the Studio mock did not start: $(Receive-Job $job 2>&1 | Out-String)" }
  $tokenDir = Join-Path ([IO.Path]::GetTempPath()) 'oml-cli'
  $tokens = @((Join-Path $tokenDir "studio-token-$port"), (Join-Path $tokenDir "studio-token-$($port + 1)"))
  $tokens | ForEach-Object { Remove-Item $_ -ErrorAction SilentlyContinue }

  function Run([int]$offset, [string[]]$a) {
    $env:OML_STUDIO_PORT = "$($port + $offset)"
    $out = & $Oml @a 2>&1 | Out-String
    $json = $null; try { $json = ($out -split "`n" | Where-Object { $_.TrimStart().StartsWith('{') } | Select-Object -Last 1) | ConvertFrom-Json } catch { }
    [pscustomobject]@{ Exit = $LASTEXITCODE; Out = $out; Json = $json }
  }
  function Calls([int]$offset) { @(Get-Content $calls | Where-Object { $_ -like "$offset *" } | ForEach-Object { $_.Split(' ')[1] }) }

  # 1. Early build: listApps, no contract line, cannot open.
  $r = Run 0 @('studio')
  Assert ($r.Exit -eq 0 -and $r.Json.apps.Count -eq 2 -and $r.Json.apps[0].name -eq 'Orders' -and $r.Json.canOpen -eq $false -and $null -eq $r.Json.server) "studio on an early build: $($r.Out)"
  Assert ((Calls 0) -contains 'listApps') "early build: listApps was not called: $(Calls 0)"
  Write-Host "ok   studio: an early build lists open modules with listApps"
  $r = Run 0 @('studio', 'open', 'Shop')
  Assert ($r.Exit -eq 2 -and $r.Out -match 'openModule' -and $r.Out -match 'server pull') "studio open on an early build: $($r.Out)"
  Write-Host "ok   studio open: an early build refuses with the manual step"
  $r = Run 0 @('query', 'Orders', 'Root { Name }', '--live')
  Assert ($r.Exit -eq 0 -and $r.Out -match 'Orders') "query --live on an early build: $($r.Out)"
  Write-Host "ok   query --live: works on an early build"

  # 2. Newer build: listOpenModules only; listApps is an unknown tool there.
  $r = Run 1 @('studio')
  Assert ($r.Exit -eq 0 -and $r.Json.apps.Count -eq 2 -and $r.Json.apps[0].tabId -eq 't-1' -and $r.Json.canOpen -eq $true -and $r.Json.server -match 'MCP contract 2') "studio on a newer build: $($r.Out)"
  Assert ((Calls 1) -notcontains 'listApps' -and (Calls 1) -contains 'listOpenModules') "newer build: wrong list tool: $(Calls 1)"
  Write-Host "ok   studio: a newer build lists open modules with listOpenModules and shows its contract line"
  $r = Run 1 @('query', 'Orders', 'Root { Name }', '--live')
  Assert ($r.Exit -eq 0 -and $r.Out -match 'Orders') "query --live on a newer build: $($r.Out)"
  $r = Run 1 @('query', 'Shop', 'Root { Name }', '--live')
  Assert ($r.Exit -eq 2 -and $r.Out -match 'not open') "query --live of a closed module: $($r.Out)"
  Write-Host "ok   query --live: checks the open modules through listOpenModules"

  # 3. studio open through searchApps + openModule.
  $r = Run 1 @('studio', 'open', 'Orders')
  Assert ($r.Exit -eq 0 -and $r.Json.status -eq 'alreadyOpen' -and $r.Json.tabIds[0] -eq 't-1') "studio open of an open module: $($r.Out)"
  $r = Run 1 @('studio', 'open', 'Nope')
  Assert ($r.Exit -eq 2 -and $r.Out -match "No module named 'Nope'" -and $r.Out -match 'Close names') "studio open of an unknown module: $($r.Out)"
  $r = Run 1 @('studio', 'open', 'Twin')
  Assert ($r.Exit -eq 2 -and $r.Out -match 'Several modules') "studio open of an ambiguous name: $($r.Out)"
  $r = Run 1 @('studio', 'open', 'Shop', '--environment', 'other.example.com')
  Assert ($r.Exit -eq 2 -and $r.Out -match 'NOT_LOGGED_IN') "studio open on another environment: $($r.Out)"
  Assert (@(Calls 1 | Where-Object { $_ -eq 'openModule' }).Count -eq 0) "openModule was called before a single match: $(Calls 1)"
  $r = Run 1 @('studio', 'open', 'shop')
  Assert ($r.Exit -eq 0 -and $r.Json.status -eq 'opened' -and $r.Json.tabId -eq 't-2' -and $r.Json.module -eq 'Shop' -and $r.Json.application -eq 'Shop App') "studio open: $($r.Out)"
  Assert (@(Calls 1 | Where-Object { $_ -eq 'openModule' }).Count -eq 2) "studio open did not call openModule again while it was opening: $(Calls 1)"
  $r = Run 1 @('studio')
  Assert (@($r.Json.apps | Where-Object { $_.name -eq 'Shop' -and $_.isOpen }).Count -eq 1) "the opened module is not listed: $($r.Out)"
  Write-Host "ok   studio open: finds the module, waits while Studio opens it, refuses unknown and ambiguous names"

  if (Test-Path $problems) { throw "the Studio mock saw a bad call: $(Get-Content -Raw $problems)" }
} finally {
  $env:OML_STUDIO_PORT = $savedPort
  if ($job) { Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue }
  $tokens | ForEach-Object { Remove-Item $_ -ErrorAction SilentlyContinue }
  Remove-TestWorkFolder $Tmp
}
