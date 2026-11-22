# oml code: the code-mode runtime in codemode/ (Python 3.9+). Checks the missing-Python message (OML_PYTHON at a file
# that does not exist), then, with Python: doctor, a program from a file (parallel oml.read calls, an oml.write apply,
# a backslash that survives), a program on stdin, a gated call that pauses (exit 3) and is approved together with its
# neighbour, get, approve refused without a terminal (a pipe and NUL), reject, a missing program file, the merge of
# .oml/codemode.json (rules added, a bad file refused), prompt and instructions, and the vendored runtime's own unit
# cases. The oml calls go to this oml's `oml mcp`, as the default config says. No Service Studio window, nothing is
# published, installed or pulled: the gated calls are only paused and rejected. Skips the Python part with a message when no Python 3.9+ is found.
param([string]$Oml = (Join-Path $PSScriptRoot '..\src\bin\Release\net10.0\oml.exe'), [string]$Tmp = (Join-Path $PSScriptRoot '..\tmp'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$repo = Split-Path $PSScriptRoot -Parent
$Oml = [IO.Path]::GetFullPath($Oml)
$work = New-TestFolder $Tmp 'code'
$savedPython = $env:OML_PYTHON
function Assert($Condition, $Message) { if (!$Condition) { throw "code: $Message" } }

# Runs oml with stdin as a pipe (closed at once, or given $InputText); returns exit code, stdout and stderr.
function Invoke-Code([string[]]$Arguments, [string]$InputText = $null) {
  $info = [Diagnostics.ProcessStartInfo]::new($Oml)
  foreach ($a in @('code') + $Arguments) { $info.ArgumentList.Add($a) }
  $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true; $info.RedirectStandardInput = $true
  $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WorkingDirectory = $work
  $process = [Diagnostics.Process]::Start($info)
  if ($InputText) { $process.StandardInput.Write($InputText) }
  $process.StandardInput.Close()
  $out = $process.StandardOutput.ReadToEndAsync(); $err = $process.StandardError.ReadToEndAsync()
  if (-not $process.WaitForExit(240000)) { try { $process.Kill($true) } catch { }; throw "code: oml code $($Arguments -join ' ') did not end in 240 s" }
  $process.WaitForExit()
  $json = $null
  try { $json = $out.Result | ConvertFrom-Json -Depth 30 } catch { }
  return [pscustomobject]@{ Exit = $process.ExitCode; Out = $out.Result; Err = $err.Result; Json = $json }
}

try {
  # 1. No Python: OML_PYTHON names a file that does not exist. The message says what to install; no Python is started.
  $env:OML_PYTHON = Join-Path $work 'no-such-python.exe'
  $r = Invoke-Code @('doctor', '--json')
  Assert ($r.Exit -eq 2 -and $r.Json.error.type -eq 'PythonNotFound' -and $r.Json.error.message -match 'OML_PYTHON' -and $r.Json.error.hint -match 'Install Python 3\.9') "missing Python: exit $($r.Exit) $($r.Out)"
  $env:OML_PYTHON = $savedPython

  # Python 3.9+ the way oml looks for it (OML_PYTHON, py -3, python, python3), for the unit cases.
  $python = $null
  $candidates = if ($savedPython) { , @($savedPython) } else { @(@('py', '-3'), @('python'), @('python3')) }
  foreach ($c in $candidates) {
    try {
      $exe = $c[0]; $rest = @($c | Select-Object -Skip 1)
      $answer = & $exe @rest -I -c "import sys; print(sys.version_info >= (3, 9)); print(sys.executable)" 2>$null
      if ($LASTEXITCODE -eq 0 -and $answer[0] -eq 'True') { $python = $answer[1].Trim(); break }
    } catch { }
  }
  if (-not $python) { Write-Host 'skip code: no Python 3.9 or newer (OML_PYTHON, py -3, python, python3); only the missing-Python message was checked'; return }

  New-Item -ItemType Directory -Force (Join-Path $work '.oml\code') | Out-Null
  & $Oml new (Join-Path $work 'M.oml') --kind CrossDevice --name M | Out-Null
  Assert ($LASTEXITCODE -eq 0) 'oml new failed'
  $own = Join-Path $work '.oml\codemode.json'

  # 2. doctor with oml's default config only: Python, the config, the sandbox, this oml's `oml mcp` with its three
  #    tools, the helpers.
  $r = Invoke-Code @('doctor', '--json')
  Assert ($r.Exit -eq 0 -and $r.Json.ok) "doctor: exit $($r.Exit) $($r.Out) $($r.Err)"
  Assert (@($r.Json.checks | Where-Object { $_.check -eq 'connector oml' -and $_.detail -match '3 tools' }).Count -eq 1) "doctor: connector oml $($r.Out)"
  $r = Invoke-Code @('config', '--json')
  Assert (($r.Json.connectors.oml.command -join ' ') -eq (($Oml -replace '\\', '/') + ' mcp')) "default connector: $($r.Out)"

  # 4. A program from a file: an apply (oml.write), five reads in parallel (helpers.oml.many and parallel itself), a
  #    backslash in a string. -f reads the file as it is, so no shell changes the text.
  $spec = (Join-Path $PSScriptRoot 'datamodel.json') -replace '\\', '/'
  Set-Content -LiteralPath (Join-Path $work '.oml\code\reads.py') -Encoding utf8NoBOM -Value @'
oml = helpers.oml
made = oml.write("apply", "M.oml", args["spec"], "S.oml")
info, stats, header = oml.many([("read", ["inspect", "S.oml", "--only", "entities", "--own"]), ("read", ["stats", "S.oml"]), ("read", ["header", "S.oml"])])
raw = parallel([("oml.read", {"args": ["validate", "S.oml"]}), ("oml.read", {"args": ["find", "Customer", "S.oml"]})])
{"saved": made["saved"], "entities": sorted(e["name"] for e in info["entities"]), "name": header["info"]["Name"],
 "exits": [r["exit"] for r in raw], "found": oml.J(raw[1]["output"]) is not None, "text": "a\\b"}
'@
  $r = Invoke-Code @('run', '--json', '-f', '.oml/code/reads.py', '--arg', "spec=$spec")
  Assert ($r.Exit -eq 0 -and $r.Json.status -eq 'completed') "run reads.py: exit $($r.Exit) $($r.Out) $($r.Err)"
  $x = $r.Json.result
  Assert ($x.saved -and ($x.entities -join ',') -eq 'Customer,Order,OrderLine' -and $x.name -eq 'M' -and ($x.exits -join ',') -eq '0,0' -and $x.found) "run reads.py result: $($r.Out)"
  Assert ($x.text -eq 'a\b' -and $r.Json.stats.toolCalls -eq 6) "run reads.py text or calls: $($r.Out)"

  # 5. A program on stdin (run -), with --json before the subcommand.
  $r = Invoke-Code @('--json', 'run', '-') 'len(parallel([("oml.read", {"args": ["header", "M.oml"]}), ("oml.read", {"args": ["header", "S.oml"]})]))'
  Assert ($r.Exit -eq 0 -and $r.Json.result -eq 2) "run - : exit $($r.Exit) $($r.Out) $($r.Err)"

  # 6. Gated calls pause before they run: two in one many() wait together, so one approve would cover both.
  Set-Content -LiteralPath (Join-Path $work '.oml\code\gated.py') -Encoding utf8NoBOM -Value @'
before = helpers.oml.read("header", "M.oml")["info"]["Name"]
helpers.oml.many([("gated", ["references", "--help"]), ("gated", ["pull", "--help"])])
before
'@
  $r = Invoke-Code @('run', '--json', '-f', '.oml/code/gated.py')
  Assert ($r.Exit -eq 3 -and $r.Json.status -eq 'paused' -and @($r.Json.pending).Count -eq 2 -and $r.Json.pending[0].tool -eq 'oml.gated') "gated: exit $($r.Exit) $($r.Out)"
  Assert ($r.Json.hint -match 'code approve') "gated hint: $($r.Json.hint)"
  $id = $r.Json.executionId
  $r = Invoke-Code @('get', $id, '--json')
  Assert ($r.Exit -eq 3 -and $r.Json.status -eq 'paused') "get: exit $($r.Exit) $($r.Out)"
  # approve needs a person at a console: a pipe and the NUL device (isatty() is true for NUL on Windows) are refused.
  $r = Invoke-Code @('approve', $id, '--json')
  Assert ($r.Exit -eq 2 -and $r.Json.error.type -eq 'TerminalRequired' -and $r.Json.error.hint -match '\.oml/codemode\.json') "approve from a pipe: exit $($r.Exit) $($r.Out)"
  Push-Location $work
  try { $nul = & cmd /c "`"$Oml`" code approve $id --json < NUL" | ConvertFrom-Json } finally { Pop-Location }
  Assert ($nul.error.type -eq 'TerminalRequired') "approve with NUL as stdin: $($nul | ConvertTo-Json -Compress)"
  $r = Invoke-Code @('reject', $id, '--reason', 'code-check', '--json')
  Assert ($r.Exit -eq 1 -and $r.Json.status -eq 'rejected') "reject: exit $($r.Exit) $($r.Out)"

  # 7. A missing program file is a usage error.
  $r = Invoke-Code @('run', '--json', '-f', '.oml/code/no-such-program.py')
  Assert ($r.Exit -eq 2 -and $r.Json.error.type -eq 'UsageError') "missing program: exit $($r.Exit) $($r.Out)"

  # 8. The project's .oml/codemode.json adds to oml's rules; oml's come first. A file that is not JSON is refused.
  $merged = if (Test-Path $own) { Get-Content -Raw $own | ConvertFrom-Json -AsHashtable } else { @{} }
  $merged.policy = @{ deny = @('oml.write'); allow = @('oml.read') }
  $merged.limits = @{ max_calls = 50 }
  $merged | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $own -Encoding utf8NoBOM
  # oml's own settings reach the oml server (the runtime starts servers with a short list of host variables), as
  # ${NAME} references: a password is never written into the generated config.
  $savedPassword = $env:OML_SERVER_PASSWORD
  $env:OML_SERVER_PASSWORD = 'code-check-' + [guid]::NewGuid().ToString('N')
  try { $r = Invoke-Code @('config', '--json') } finally { $written = Get-Content -Raw (Join-Path $work '.oml\codemode\config.json'); $secret = $env:OML_SERVER_PASSWORD; $env:OML_SERVER_PASSWORD = $savedPassword }
  Assert ($r.Json.connectors.oml.env.OML_SERVER_PASSWORD -eq '${OML_SERVER_PASSWORD}' -and $r.Json.connectors.oml.env.OML_EXE -eq '${OML_EXE}' -and @($r.Json.connectors.oml.secrets) -contains 'OML_SERVER_PASSWORD') "config env: $($r.Out)"
  Assert (!$written.Contains($secret) -and !$r.Out.Contains($secret) -and !$r.Err.Contains($secret)) 'the password reached the generated config or the output'
  $p = $r.Json.policy
  Assert ($r.Exit -eq 0 -and ($p.allow -join ',') -eq 'oml.read,oml.write' -and ($p.ask -join ',') -eq 'oml.gated' -and ($p.deny -join ',') -eq 'oml.write' -and $r.Json.limits.max_calls -eq 50 -and $r.Json.limits.timeout_s -eq 1800) "config merge: $($r.Out)"
  Assert ($r.Json.connectors.oml.cwd -eq ($work -replace '\\', '/') -and $r.Json.approval.require_terminal -eq $true) "config: cwd or approval $($r.Out)"
  $r = Invoke-Code @('run', '--json', 'helpers.oml.write("new", "W.oml", "--name", "W")')
  Assert ($r.Exit -eq 1 -and $r.Json.error.type -eq 'PolicyDenied') "deny rule from .oml/codemode.json: exit $($r.Exit) $($r.Out)"
  Copy-Item $own "$own.good"
  Set-Content -LiteralPath $own -Value '{"connectors": ' -Encoding utf8NoBOM
  $r = Invoke-Code @('doctor', '--json')
  Assert ($r.Exit -eq 2 -and $r.Json.error.type -eq 'ConfigError' -and $r.Json.error.message -match 'codemode\.json') "bad .oml/codemode.json: exit $($r.Exit) $($r.Out)"
  Move-Item -Force "$own.good" $own

  # 9. What agents read: the execute tool's description and the instructions block name the oml tools and helpers.
  $r = Invoke-Code @('prompt')
  Assert ($r.Exit -eq 0 -and $r.Out -match 'oml\.read' -and $r.Out -match 'oml\.gated' -and $r.Out -match 'helpers\.oml' -and $r.Out -match 'pauses until the user approves') "prompt: $($r.Out)"
  $r = Invoke-Code @('instructions')
  Assert ($r.Exit -eq 0 -and $r.Out -match '\.oml/code/' -and $r.Out -match 'code run --json -f' -and $r.Out -match 'Never approve it yourself') "instructions: $($r.Out)"

  # 10. The vendored runtime's own unit cases (pure logic, isolated states): a few seconds.
  Push-Location (Join-Path $repo 'codemode\tests')
  try { $unit = & $python -m unittest test_pure_logic test_isolated 2>&1 | Out-String; $unitExit = $LASTEXITCODE } finally { Pop-Location }
  Assert ($unitExit -eq 0 -and $unit -match '(?m)^OK') "codemode unit cases: $unit"
  Write-Host "ok   code: missing Python, doctor, parallel reads and a write, stdin, gated pause and reject, approve refused (pipe, NUL), merge, prompt, instructions, $([regex]::Match($unit, 'Ran \d+ tests').Value) of the runtime"
}
finally {
  $env:OML_PYTHON = $savedPython
  Remove-TestWorkFolder $work
}
