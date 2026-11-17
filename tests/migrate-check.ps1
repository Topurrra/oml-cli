# oml migrate: the pure decisions (selftest: plan, specs, wrappers, 2-source.json repoint edits, screens export and translation wiring, session
# variables, layout, css, other content, status on a built-in sample) and the refusals that need no module (missing file, missing --name, bad name,
# existing output folder, a --template that is not an .oml file, a missing file or plan folder for status). Nothing here needs Service Studio.
# Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Tmp = New-TestFolder $Tmp 'migrate'
try {
$work = Join-Path $Tmp ('migrate-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Force $work | Out-Null
function Fail($m) { throw "migrate: $m" }
function Require($condition, $message) { if (-not $condition) { Fail $message } }
function Call([string[]]$a) {
  $out = & $Oml @a 2>&1
  $text = ($out | ForEach-Object { "$_" }) -join "`n"
  $json = $null; try { $json = $text | ConvertFrom-Json } catch { }
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text; Json = $json }
}
$env:OML_LOG = '0'; $env:OML_OUTPUT = 'json'

# 1. the built-in sample: ok and exit 0
$r = Call @('migrate', 'selftest')
Require ($r.Exit -eq 0 -and $r.Text.Trim() -eq 'ok') "selftest: exit $($r.Exit), $($r.Text)"
$r = Call @('migrate', 'selftest', '--show')
Require ($r.Exit -eq 0 -and $r.Text -match '"op": "repoint"' -and $r.Text -match 'Order_CreateOrUpdate' -and $r.Text -match '2-source.json' -and $r.Text -match '3-new-module.json' -and $r.Text -match 'migrate-source' -and $r.Text -match '4-screens.json' -and $r.Text -match '4-screens.md' -and $r.Text -match 'clientVariables' -and $r.Text -match 'coverage|screens: ' -and $r.Text -match 'css/README.md') "selftest --show: $($r.Text)"

# 2. refusals: one JSON error and exit 2, before any child process (Service Studio is not needed)
$old = Join-Path $work 'Old.oml'; Set-Content -LiteralPath $old -Value 'x'
$full = Join-Path $work 'full'; New-Item -ItemType Directory -Force $full | Out-Null; Set-Content -LiteralPath (Join-Path $full 'f.txt') -Value 'x'
$cases = @(
  @{ args = @('migrate', 'plan', (Join-Path $work 'Missing.oml'), '--name', 'New'); match = 'does not exist' },
  @{ args = @('migrate', 'plan', $old); match = '--name' },
  @{ args = @('migrate', 'plan', $old, '--name', '1bad'); match = 'letters' },
  @{ args = @('migrate', 'plan', $old, '--name', 'New', '--out', $full); match = 'not empty' },
  @{ args = @('migrate', 'plan', $old, '--name', 'New', '--in', (Join-Path $work 'nothing')); match = '--in' },
  @{ args = @('migrate', 'plan', $old, '--name', 'New', '--template', (Join-Path $work 'nothing.oml')); match = '--template' },
  @{ args = @('migrate', 'plan', $old, '--name', 'New', '--template', $old.Replace('.oml', '.txt')); match = '--template' },
  @{ args = @('migrate', 'status', (Join-Path $work 'Missing.oml'), $old); match = 'does not exist' },
  @{ args = @('migrate', 'status', $old, $old, '--plan', (Join-Path $work 'nothing')); match = 'plan.json' },
  @{ args = @('migrate', 'bogus'); match = 'Unknown migrate command' }
)
foreach ($c in $cases) {
  $r = Call $c.args
  Require ($r.Exit -eq 2 -and $r.Json -and $r.Json.error -and $r.Text -match [regex]::Escape($c.match)) "refusal '$($c.args -join ' ')': exit $($r.Exit), $($r.Text)"
}
Require ((Get-ChildItem $full).Count -eq 1) 'the existing output folder was changed'
Require ((Get-Content -Raw $old).Trim() -eq 'x') 'the old module file was changed'
Write-Host 'ok   migrate checks'
} finally { Remove-TestFolder $Tmp }
