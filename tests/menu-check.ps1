# oml menu: the interactive menu is for people at a terminal and changes nothing for agents. `oml menu-test` (the model, the
# keys, the settings editor and the bare-oml decision, all without a terminal) passes; a bare oml with redirected output
# prints the overview as before; oml menu with redirected input exits 2 with one error line and never waits for a key.
# Needs no Service Studio and no network. Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
function Require($condition, $message) { if (-not $condition) { throw "menu: $message" } }
function Call([string[]]$a, [string]$in = '') {
  $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try { $out = $in | & $Oml @a 2>&1 } finally { $ErrorActionPreference = $prev }
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = (($out | ForEach-Object { "$_" }) -join "`n") }
}
$saved = @{}; foreach ($name in 'OML_LOG', 'OML_MENU', 'OML_OUTPUT') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
  $env:OML_LOG = '0'; $env:OML_MENU = $null; $env:OML_OUTPUT = $null

  # 1. The self-test: tasks against the usage text, filter, keys, settings editor, file picker, the bare-oml decision.
  $t = Call @('menu-test')
  Require ($t.Exit -eq 0 -and $t.Text.Trim() -eq 'ok') "menu-test failed (exit $($t.Exit)):`n$($t.Text)"
  Write-Host 'ok   menu self-test'

  # 2. A bare oml with redirected output is the overview, exactly as before (the menu needs a person at a terminal).
  $bare = Call @()
  Require ($bare.Exit -eq 0 -and $bare.Text -match 'Commands by area') "a bare oml with redirected output did not print the overview (exit $($bare.Exit)): $($bare.Text)"
  $help = Call @('--help')
  Require ($help.Text -ceq $bare.Text) 'a bare oml and oml --help differ'
  Write-Host 'ok   bare oml prints the overview when redirected'

  # 3. oml menu with redirected input refuses cleanly: exit 2, one line, no hang.
  $menu = Call @('menu') ''
  Require ($menu.Exit -eq 2) "oml menu with redirected input exited $($menu.Exit), not 2: $($menu.Text)"
  Require (@($menu.Text -split "`n" | Where-Object { $_.Trim() }).Count -eq 1 -and $menu.Text -match 'terminal') "oml menu did not say in one line that it needs a terminal: $($menu.Text)"
  Write-Host 'ok   oml menu refuses redirected input'

  # 4. oml menu --help prints its usage line; the command is in the command table and the usage text.
  $h = Call @('menu', '--help')
  Require ($h.Exit -eq 0 -and $h.Text -match 'oml menu') "oml menu --help: $($h.Text)"
  $c = Call @('commands', '--check')
  Require ($c.Exit -eq 0) "oml commands --check failed: $($c.Text)"
  Write-Host 'ok   oml menu --help and the command table'
}
finally {
  foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}
