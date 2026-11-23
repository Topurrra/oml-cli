# Questions for a person: hooks install, config set and team-rules add ask for what is missing when a person runs them
# (OML_ASK=1 lets the pipe answer), print the command line they stand for, and change nothing when the answer is empty.
# Without a person (an agent, a script) they behave as before. oml create's questions are in create-check.ps1.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Tmp = New-TestFolder $Tmp 'prompt'
try {
  function Require($condition, $message) { if (-not $condition) { throw "prompt: $message" } }
  function Asked([string]$answers, [string[]]$a) {
    $env:OML_ASK = '1'
    try { $out = $answers | & $Oml @a 2>&1 | Out-String } finally { $env:OML_ASK = $null }
    [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $out }
  }
  $project = Join-Path $Tmp 'p'; New-Item -ItemType Directory -Force (Join-Path $project '.oml') | Out-Null
  '{}' | Set-Content (Join-Path $project '.oml\oml-config.json')
  Push-Location $project
  try {
    $r = Asked "2`n" @('hooks', 'install')
    Require ($r.Exit -eq 0 -and $r.Text -match 'The same as: oml hooks install --agent copilot' -and (Test-Path '.github\hooks\oml.json') -and -not (Test-Path '.claude')) "hooks install asked: $($r.Text)"
    $r = Asked "ui.targets`ndesktop,phone`n" @('config', 'set')
    $targets = (Get-Content '.oml\oml-config.json' -Raw | ConvertFrom-Json).ui.targets
    Require ($r.Exit -eq 0 -and $r.Text -match 'The same as: oml config set ui.targets desktop,phone' -and ($targets -join ',') -eq 'desktop,phone') "config set asked: $($r.Text)"
    $r = Asked "off`n" @('config', 'set', 'checks.browser')
    Require ($r.Exit -eq 0 -and (Get-Content '.oml\oml-config.json' -Raw | ConvertFrom-Json).checks.browser -eq $false) "config set <key> asked: $($r.Text)"
    $r = Asked "`n" @('team-rules', 'add')
    Require ($r.Exit -eq 2 -and $r.Text -match 'Nothing was added') "team-rules add, empty answer: $($r.Text)"
    Write-Host 'ok   prompt: hooks install, config set and team-rules add ask a person for what is missing and show the command'

    # Without a person: the usage as before, nothing asked, nothing changed.
    $out = & $Oml config set 2>&1 | Out-String
    Require ($LASTEXITCODE -eq 2 -and $out -match 'Usage: oml config' -and $out -notmatch '\? Setting') "config set for an agent: $out"
    Write-Host 'ok   prompt: an agent or a script is never asked'
  } finally { Pop-Location }
} finally { Remove-TestWorkFolder $Tmp }
