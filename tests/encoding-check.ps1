# Output encoding: JSON must stay valid and lossless in every shell and code page, text output must be UTF-8 when
# redirected, and a console window must show the real characters without its code page being changed.
# Each shell runs in its own hidden console (Start-Process), so `chcp` and [Console]::OutputEncoding change only that
# console, never the one the suite runs in. This file stays ASCII: the test texts are \u escapes in JSON.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($condition, $message) { if (!$condition) { throw $message } }

# A folder with non-ASCII in its name, as in C:\Users\<name>: the paths in the output must round-trip too.
$dir = Join-Path $Tmp ('enc-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + ('" Fun\u00E7\u00E3o \u10E5"' | ConvertFrom-Json))
New-Item -ItemType Directory -Force $dir | Out-Null
$blank = Join-Path $dir 'blank.oml'; $module = Join-Path $dir 'Enc.oml'; $spec = Join-Path $dir 'spec.json'
# Curly quotes (the OutSystemsUI Licenses case), Portuguese, Spanish, Georgian, an emoji and backslashes.
Set-Content $spec -Encoding ascii -Value @'
{ "module": { "description": "M\u00F3dulo \u201CLicenses\u201D \u2014 \u10E5\u10D0\u10E0\u10D7\u10E3\u10DA\u10D8 \\ \uD83D\uDE00",
              "javaScript": "var t = 'Fun\u00E7\u00E3o \u201Cq\u201D \\\\';" },
  "screens": [ { "name": "Home", "widgets": [ { "type": "Text", "text": "Ol\u00E1 \u201Cmundo\u201D \u10E5 \uD83D\uDE00 \\" },
                                              { "type": "Expression", "value": "\"A\u00F1o \u201Cx\u201D \u10E5\"" } ] } ] }
'@
$want = (Get-Content $spec -Raw | ConvertFrom-Json).module
$wantText = (Get-Content $spec -Raw | ConvertFrom-Json).screens[0].widgets[0].text
& $Oml new $blank --name EncCheck --kind CrossDevice | Out-Null
$applied = & $Oml apply $blank $spec $module | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $applied.saved -and $applied.staged -ceq $module) "encoding: the test module did not apply ($($applied | ConvertTo-Json -Depth 5 -Compress))"

$env:ENC_OML = $Oml; $env:ENC_MOD = $module; $env:ENC_DIR = $dir
$utf8 = [Text.UTF8Encoding]::new($false)
# Runs one command line in its own hidden console. The command writes what it captured to $env:ENC_DIR\<name>.out.
function In-Console([string]$exe, [string[]]$arguments, [string]$name) {
  $file = Join-Path $dir "$name.out"; Remove-Item $file -ErrorAction SilentlyContinue
  $p = Start-Process $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
  Require (Test-Path $file) "encoding: $name wrote no output (exit $($p.ExitCode))"
  return [IO.File]::ReadAllBytes($file)
}
function Run-Ps([string]$exe, [string]$script, [string]$name) {
  $file = Join-Path $dir "$name.ps1"
  Set-Content $file -Encoding ascii -Value $script
  In-Console $exe @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$file`"") $name
}
function Check-Json([byte[]]$bytes, [string]$label, [switch]$Pretty) {
  Require (@($bytes | Where-Object { $_ -gt 0x7F }).Count -eq 0) "encoding: $label printed non-ASCII bytes"
  $text = $utf8.GetString($bytes)
  try { $j = $text | ConvertFrom-Json } catch { throw "encoding: $label printed invalid JSON: $($text.Substring(0, [Math]::Min(200, $text.Length)))" }
  Require ($j.name -ceq 'EncCheck' -and $j.module.description -ceq $want.description -and $j.module.javaScript -ceq $want.javaScript) "encoding: $label changed the texts: $($j.module.description) / $($j.module.javaScript)"
  if ($Pretty) { Require ($text.Contains("`n  ")) "encoding: $label is not indented" }
}
# What a captured command wrote, as the shell decoded it, saved as UTF-8.
$capture = @'
[Console]::OutputEncoding = [Text.Encoding]::GetEncoding(__CP__)
$x = & $env:ENC_OML __ARGS__
[IO.File]::WriteAllText((Join-Path $env:ENC_DIR '__NAME__.out'), ($x -join "`n"), (New-Object Text.UTF8Encoding $false))
'@
function Capture([string]$exe, [int]$cp, [string]$arguments, [string]$name) {
  Run-Ps $exe $capture.Replace('__CP__', $cp).Replace('__ARGS__', $arguments).Replace('__NAME__', $name) $name
}

# JSON: PowerShell 7 and 5.1 capture ($x = oml ...), in code pages 437 and 1252, compact and --pretty.
foreach ($shell in 'pwsh', 'powershell.exe') {
  foreach ($cp in 437, 1252) {
    $label = "$shell `$x = (code page $cp)"
    Check-Json (Capture $shell $cp 'inspect $env:ENC_MOD --own' "cap-$shell-$cp") $label
    Check-Json (Capture $shell $cp 'inspect $env:ENC_MOD --own --pretty' "pretty-$shell-$cp") "$label --pretty" -Pretty
  }
  # describe: the screen text, with the module path in "element"... and as the shell decoded it.
  $d = $utf8.GetString((Capture $shell 437 'describe $env:ENC_MOD Home' "describe-$shell")) | ConvertFrom-Json
  Require ($d.text.Contains($wantText)) "encoding: $shell describe lost the screen text: $($d.text)"
  Write-Host "ok   encoding: $shell captures valid, lossless JSON (code pages 437 and 1252, --pretty, describe)"
}
# PowerShell 5.1 > file: PowerShell decodes the pipe and writes UTF-16; ASCII JSON survives that too.
$b = Run-Ps 'powershell.exe' "[Console]::OutputEncoding = [Text.Encoding]::GetEncoding(437); & `$env:ENC_OML inspect `$env:ENC_MOD --own > (Join-Path `$env:ENC_DIR 'ps5-file.tmp'); [IO.File]::WriteAllText((Join-Path `$env:ENC_DIR 'ps5-file.out'), (Get-Content (Join-Path `$env:ENC_DIR 'ps5-file.tmp') -Raw), (New-Object Text.UTF8Encoding `$false))" 'ps5-file'
Check-Json $b 'PowerShell 5.1 > file'

# cmd.exe at code page 437 and 1252: the raw bytes, redirected to a file and through a pipe.
foreach ($cp in 437, 1252) {
  Check-Json (In-Console 'cmd.exe' @('/c', "chcp $cp >nul & `"%ENC_OML%`" inspect `"%ENC_MOD%`" --own > `"%ENC_DIR%\cmd-$cp.out`"") "cmd-$cp") "cmd > file (chcp $cp)"
  Check-Json (In-Console 'cmd.exe' @('/c', "chcp $cp >nul & `"%ENC_OML%`" inspect `"%ENC_MOD%`" --own | findstr /r . > `"%ENC_DIR%\cmdpipe-$cp.out`"") "cmdpipe-$cp") "cmd | findstr (chcp $cp)"
}
# oml spec check (run by the launcher, not Core) repeats an unknown key from the spec in its message.
$badKey = '"descri\u00E7\u00E3o\u201C"' | ConvertFrom-Json
Set-Content (Join-Path $dir 'bad.json') -Encoding ascii -Value '{ "entities": [ { "name": "X", "descri\u00E7\u00E3o\u201C": "y" } ] }'
$c = $utf8.GetString((In-Console 'cmd.exe' @('/c', "chcp 437 >nul & `"%ENC_OML%`" spec check `"%ENC_DIR%\bad.json`" > `"%ENC_DIR%\speccheck.out`"") 'speccheck')) | ConvertFrom-Json
Require ($c.problems[0].message -ceq "unknown key '$badKey'") "encoding: spec check changed the key: $($c.problems[0].message)"
Write-Host 'ok   encoding: cmd.exe redirect and pipe give valid, lossless JSON (code pages 437 and 1252, also spec check)'

# Git Bash, when installed: it reads the raw bytes.
$bash = @("$env:ProgramFiles\Git\bin\bash.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($bash) {
  Check-Json (In-Console $bash @('-c', '"\"$ENC_OML\" inspect \"$ENC_MOD\" --own > \"$ENC_DIR/bash.out\""') 'bash') 'Git Bash > file'
  Write-Host 'ok   encoding: Git Bash gets valid, lossless JSON'
} else { Write-Host 'ok   encoding: Git Bash not installed, skipped' }

# oml run: the envelope is ASCII JSON too, with each step's output intact; steps from a UTF-8 pipe keep their paths.
$steps = Join-Path $dir 'steps.json'
@{ steps = @(@('inspect', $module, '--own'), @('describe', $module, 'Home')) } | ConvertTo-Json -Depth 5 | Set-Content $steps -Encoding utf8
$env:ENC_STEPS = $steps
$r = $utf8.GetString((Capture 'powershell.exe' 437 'run $env:ENC_STEPS' 'run-ps5')) | ConvertFrom-Json
Require ($r.results.Count -eq 2 -and $r.results[0].exitCode -eq 0 -and $r.results[0].output.module.description -ceq $want.description -and $r.results[1].output.text.Contains($wantText)) 'encoding: oml run in PowerShell 5.1 lost text'
$piped = Run-Ps 'pwsh' "[Console]::OutputEncoding = [Text.Encoding]::GetEncoding(437); `$x = Get-Content `$env:ENC_STEPS -Raw | & `$env:ENC_OML run -; [IO.File]::WriteAllText((Join-Path `$env:ENC_DIR 'run-stdin.out'), (`$x -join ""``n""), (New-Object Text.UTF8Encoding `$false))" 'run-stdin'
$r = $utf8.GetString($piped) | ConvertFrom-Json
Require ($r.results.Count -eq 2 -and $r.results[0].exitCode -eq 0 -and $r.results[0].output.module.description -ceq $want.description) "encoding: oml run - (steps on stdin, non-ASCII path) failed: $($r.results[0].output.error)"
Write-Host 'ok   encoding: oml run (PowerShell 5.1 capture, steps from a UTF-8 pipe)'

# oml's own text is pure ASCII, so it reads the same in every code page: the embedded docs (oml doc, oml spec,
# oml warn, rules, recipes) and the usage text.
$src = Join-Path $PSScriptRoot '..\src'
$embedded = @(foreach ($proj in (Join-Path $src 'OmlCli.csproj'), (Join-Path $src 'Core\OmlCli.Core.csproj')) {
  foreach ($m in [regex]::Matches((Get-Content $proj -Raw), '<EmbeddedResource Include="([^"]+)"')) {
    foreach ($item in $m.Groups[1].Value.Split(';')) { Get-ChildItem (Join-Path (Split-Path $proj) $item) | Where-Object Extension -in '.md', '.jsonc' }
  }
})
$bad = @($embedded | Where-Object { @([IO.File]::ReadAllBytes($_.FullName) | Where-Object { $_ -gt 0x7F }).Count -gt 0 } | ForEach-Object Name)
Require ($embedded.Count -ge 15 -and $bad.Count -eq 0) "encoding: embedded docs with non-ASCII text: $($bad -join ', ') ($($embedded.Count) checked)"
$usage = Join-Path $dir 'usage.out'
foreach ($c in '--help', 'lt --help', 'spec --help', 'doc --help', 'doctor --help', 'dashboard --help', 'font --help', 'image --help', 'spec', 'doc', 'api') {
  cmd /c "`"%ENC_OML%`" $c > `"$usage`" 2>&1"
  Require (@([IO.File]::ReadAllBytes($usage) | Where-Object { $_ -gt 0x7F }).Count -eq 0) "encoding: 'oml $c' printed non-ASCII text"
}
Write-Host "ok   encoding: oml's own text is ASCII ($($embedded.Count) embedded docs, usage, doc, spec and api lists)"

# Other text, such as a message on stderr with a non-ASCII path, is UTF-8 when redirected.
$env:ENC_MISSING = Join-Path $dir 'missing.oml'
$message = "--module needs an existing .oml file: $env:ENC_MISSING"
$t = $utf8.GetString((In-Console 'cmd.exe' @('/c', "chcp 437 >nul & `"%ENC_OML%`" dashboard --no-open --module `"%ENC_MISSING%`" 2> `"%ENC_DIR%\text-cmd.out`"") 'text-cmd'))
Require ($t.Contains($message)) "encoding: redirected stderr is not UTF-8: $t"
$t = $utf8.GetString((Run-Ps 'powershell.exe' "[Console]::OutputEncoding = New-Object Text.UTF8Encoding `$false; `$x = & `$env:ENC_OML dashboard --no-open --module `$env:ENC_MISSING 2>&1 | ForEach-Object { ""`$_"" }; [IO.File]::WriteAllText((Join-Path `$env:ENC_DIR 'text-ps5.out'), (`$x -join ""``n""), (New-Object Text.UTF8Encoding `$false))" 'text-ps5'))
Require ($t.Contains($message)) "encoding: PowerShell 5.1 with UTF-8 output encoding did not decode stderr: $t"
Write-Host 'ok   encoding: redirected stderr with a non-ASCII path is UTF-8 (cmd 2> file, PowerShell 5.1 with UTF-8 set)'

# A lone surrogate escape in a spec is a clear problem in apply and spec check, not a crash.
Set-Content (Join-Path $dir 'lone.json') -Encoding ascii -Value '{ "module": { "styleSheet": ".a{}\uD800" }, "entities": [ { "name": "A" } ] }'
$lone = & $Oml apply $blank (Join-Path $dir 'lone.json') (Join-Path $dir 'lone.oml') | ConvertFrom-Json
Require ($LASTEXITCODE -eq 1 -and -not $lone.saved -and $lone.problems[0] -like 'module.styleSheet: text with an unpaired surrogate*') "encoding: apply with a lone surrogate: exit $LASTEXITCODE $($lone | ConvertTo-Json -Compress)"
$lone = & $Oml spec check (Join-Path $dir 'lone.json') | ConvertFrom-Json
Require ($LASTEXITCODE -eq 1 -and $lone.problems[0].path -eq 'module.styleSheet') "encoding: spec check with a lone surrogate: exit $LASTEXITCODE"
Write-Host 'ok   encoding: a lone surrogate in a spec is a problem in apply and spec check'

# A console window: --pretty shows the real characters, compact JSON stays ASCII, and the code page stays 437.
$dump = Join-Path $dir 'dump.ps1'
Set-Content $dump -Encoding ascii -Value @'
$raw = $Host.UI.RawUI
$cells = $raw.GetBufferContents((New-Object System.Management.Automation.Host.Rectangle 0, 0, ($raw.BufferSize.Width - 1), $raw.CursorPosition.Y))
$sb = [Text.StringBuilder]::new()
for ($y = 0; $y -le $cells.GetUpperBound(0); $y++) {
  for ($x = 0; $x -le $cells.GetUpperBound(1); $x++) { if ($cells[$y, $x].BufferCellType -ne 'Trailing') { [void]$sb.Append($cells[$y, $x].Character) } }
  [void]$sb.Append("`n")
}
[IO.File]::WriteAllText((Join-Path $env:ENC_DIR 'console.out'), $sb.ToString(), [Text.UTF8Encoding]::new($false))
'@
$screen = $utf8.GetString((In-Console 'cmd.exe' @('/c', "chcp 437 >nul & `"%ENC_OML%`" inspect `"%ENC_MOD%`" --own --pretty & `"%ENC_OML%`" inspect `"%ENC_MOD%`" --own & chcp & pwsh -NoProfile -File `"$dump`"") 'console'))
# The screen shows the BMP characters; System.Text.Json itself writes characters outside the BMP (the emoji) as \u escapes.
$screen = $screen.Replace("`n", '') # a long line wraps onto the next row of the buffer
$shown = $want.description.Replace('\', '\\').Replace([char]::ConvertFromUtf32(0x1F600), '\uD83D\uDE00')
Require ($screen.Contains('"description": "' + $shown + '"')) 'encoding: --pretty in a console window did not show the real characters'
Require ($screen.Contains('"description":"M\u00F3dulo \u201CLicenses\u201D')) 'encoding: compact JSON in a console window is not ASCII'
Require ($screen.Contains('Active code page: 437')) 'encoding: oml changed the console code page'
Write-Host 'ok   encoding: console window shows real characters with --pretty, keeps code page 437'
Remove-Item Env:ENC_OML, Env:ENC_MOD, Env:ENC_DIR, Env:ENC_STEPS, Env:ENC_MISSING -ErrorAction SilentlyContinue
