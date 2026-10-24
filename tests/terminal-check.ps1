# Terminal view regression. Redirected stdout must stay byte-identical JSON (compared with 1.8.0 goldens),
# --json and OML_OUTPUT=json force JSON, OML_OUTPUT=human renders the human view, and NO_COLOR gives no escape
# sequences. The renderer's unit-style self-tests (`oml terminal-test`) need no console.
param($Oml, $Tmp)
$ErrorActionPreference = 'Stop'
$tests = $PSScriptRoot
$golden = Join-Path $tests 'terminal-golden'

# --- 0. The renderer's own self-tests (no console, no Service Studio). ---
$st = & $Oml terminal-test
if ($LASTEXITCODE -ne 0 -or $st -ne 'ok') { throw "terminal renderer self-test failed:`n$st" }
Write-Host 'ok   terminal renderer self-test'

$reactive = Join-Path $Tmp 'r0.oml'

# --- 1. Redirected output is byte-identical JSON, as in 1.8.0. ---
$cases = @(
  @{ Name = 'inspect-entities'; Args = @('inspect', $reactive, '--only', 'entities') },
  @{ Name = 'expr-add'; Args = @('expr', '1 + 2') },
  @{ Name = 'expr-invalid'; Args = @('expr', '1 +') },
  @{ Name = 'validate-r0'; Args = @('validate', $reactive) },
  @{ Name = 'describe-missing'; Args = @('describe', $reactive, 'NoSuchElement') },
  @{ Name = 'find-id'; Args = @('find', 'Id', $reactive) },
  @{ Name = 'tree-missing'; Args = @('tree', $reactive, 'Fake') },
  @{ Name = 'init-rules'; Args = @('check', '--init-rules', '--out', (Join-Path $Tmp 'terminal-rules.json')) },
  @{ Name = 'spec-check-bad'; Args = @('spec', 'check', (Join-Path $Tmp 'terminal-bad.json')) },
  @{ Name = 'version'; Args = @('version') },
  @{ Name = 'spec-schema'; Args = @('spec', 'schema') }
)
foreach ($c in $cases) {
  if ($c.Name -eq 'spec-check-bad') { '{"badKey":1}' | Set-Content (Join-Path $Tmp 'terminal-bad.json') -NoNewline }
  $actual = (& $Oml @($c.Args) 2>$null) -join "`n"
  if ($LASTEXITCODE -gt 2) { throw "command failed badly ($($c.Name)): exit $LASTEXITCODE" }
  # Line endings depend on the checkout (text=auto); PowerShell already split the output into lines.
  $expected = [IO.File]::ReadAllText((Join-Path $golden ($c.Name + '.json'))).Replace("`r`n", "`n").TrimEnd("`n")
  if ($c.Name -eq 'init-rules') {
    # The written path is a full path in a temp folder: compare it by its file name.
    $pat = '"([^"]*\\terminal-rules\.json)"'
    $actual = [regex]::Replace($actual, $pat, '"<TMP>\terminal-rules.json"')
    $expected = [regex]::Replace($expected, $pat, '"<TMP>\terminal-rules.json"')
  }
  if ($actual -ne $expected) { throw "output changed vs 1.8.0: $($c.Name)`n--- got ---`n$actual`n--- want ---`n$expected" }
  Write-Host "ok   golden $($c.Name)"
}

# --- 2. OML_OUTPUT=human renders a human view even when stdout is redirected (no ANSI in a pipe). ---
$env:OML_OUTPUT = 'human'
$human = (& $Oml meta $reactive) -join "`n"
Remove-Item Env:OML_OUTPUT
if ($human -match '^\s*\{') { throw 'OML_OUTPUT=human did not render a human view' }
if ($human -match '\x1b\[') { throw 'human view contains escape sequences' }
if ($human -notmatch 'Name') { throw 'human view is missing the module fields' }
Write-Host 'ok   OML_OUTPUT=human renders a human view'

# --- 3. --json beats OML_OUTPUT=human. ---
$env:OML_OUTPUT = 'human'
$forced = (& $Oml meta $reactive --json) -join "`n"
Remove-Item Env:OML_OUTPUT
$null = $forced | ConvertFrom-Json # throws when the output is not JSON
Write-Host 'ok   --json forces JSON'

# --- 4. OML_OUTPUT=json forces JSON. ---
$env:OML_OUTPUT = 'json'
$j = (& $Oml meta $reactive) -join "`n"
Remove-Item Env:OML_OUTPUT
$null = $j | ConvertFrom-Json
Write-Host 'ok   OML_OUTPUT=json forces JSON'

# --- 5. Default redirected output equals the forced-JSON output, byte for byte. ---
$def = (& $Oml inspect $reactive --own) -join "`n"
$json2 = (& $Oml inspect $reactive --own --json) -join "`n"
if ($def -ne $json2) { throw 'redirected output differs from --json' }
Write-Host 'ok   redirected output equals --json output'

# --- 6. NO_COLOR gives no escape sequences. ---
$env:OML_OUTPUT = 'human'
$env:NO_COLOR = '1'
$nc = (& $Oml meta $reactive) -join "`n"
Remove-Item Env:OML_OUTPUT, Env:NO_COLOR
if ($nc -match '\x1b\[') { throw 'NO_COLOR gave escape sequences' }
Write-Host 'ok   NO_COLOR gives no escape sequences'
