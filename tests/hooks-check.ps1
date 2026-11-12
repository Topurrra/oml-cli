# oml hooks: the pre-tool decisions (large skill references read whole, .oml files changed by a tool, a patch or a shell)
# for the payloads of Claude Code, Copilot, Cursor, Codex and Gemini CLI; input it cannot read is allowed; install /
# status / remove for every agent on a throwaway project folder keep the user's own settings and hooks. No agent is started. Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'hooks'
try {
$work = Join-Path $Tmp ('hooks-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Force $work | Out-Null
function Fail($m) { throw "hooks: $m" }
function Require($condition, $message) { if (-not $condition) { Fail $message } }
function Call([string[]]$a) {
  $out = & $Oml @a 2>&1
  $text = ($out | ForEach-Object { "$_" }) -join "`n"
  $json = $null; try { $json = $text | ConvertFrom-Json } catch { }
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text; Json = $json }
}

# 1. decisions: exit 2 refuses (the reason on stderr), exit 0 allows
$cases = @(
  @('block', 'Read', @{ file_path = 'C:\p\skill\reference\commands.md' }),
  @('allow', 'Read', @{ file_path = 'C:\p\skill\reference\commands.md'; offset = 100; limit = 200 }),
  @('block', 'Read', @{ file_path = 'skill/reference/spec.md'; limit = 2000 }),
  @('allow', 'Read', @{ file_path = 'C:\other\docs\spec.md' }),
  @('allow', 'Read', @{ file_path = 'skill/SKILL.md' }),
  @('block', 'Grep', @{ pattern = 'x'; path = 'skill/reference/model-api.md'; output_mode = 'content' }),
  @('allow', 'Grep', @{ pattern = 'x'; path = 'skill/reference/model-api.md'; output_mode = 'content'; head_limit = 50 }),
  @('allow', 'Grep', @{ pattern = 'x'; path = 'skill/reference/commands.md' }),
  @('block', 'Edit', @{ file_path = 'C:\p\Orders.oml'; old_string = 'a'; new_string = 'b' }),
  @('block', 'Write', @{ file_path = 'new.OML'; content = 'x' }),
  @('allow', 'Write', @{ file_path = 'spec.json'; content = 'x' }),
  @('block', 'Bash', @{ command = 'cat skill/reference/commands.md' }),
  @('allow', 'Bash', @{ command = 'cat skill/reference/commands.md | head -50' }),
  @('block', 'PowerShell', @{ command = 'Get-Content C:\p\skill\reference\spec.md' }),
  @('allow', 'PowerShell', @{ command = 'Get-Content C:\p\skill\reference\spec.md -TotalCount 40' }),
  @('block', 'Bash', @{ command = 'python fix.py > Orders.oml' }),
  @('block', 'PowerShell', @{ command = '$b | Set-Content -Path Orders.oml -AsByteStream' }),
  @('block', 'Bash', @{ command = 'sed -i s/a/b/ Orders.oml' }),
  @('block', 'PowerShell', @{ command = '[IO.File]::WriteAllBytes("Orders.oml", $b)' }),
  @('allow', 'Bash', @{ command = 'oml apply Orders.oml spec.json Orders.v2.oml > out.json 2>&1' }),
  @('allow', 'PowerShell', @{ command = 'oml install staged.oml Orders.oml; oml inspect Orders.oml --own' }),
  @('allow', 'Bash', @{ command = 'ls *.oml && cp a.oml b.oml' }),
  @('allow', 'Glob', @{ pattern = '**/*.oml' }),
  # Cursor and Copilot names, Gemini CLI, Codex
  @('block', 'StrReplace', @{ path = 'C:\p\Orders.oml'; old_string = 'a'; new_string = 'b' }),
  @('block', 'Shell', @{ command = 'cat skill/reference/commands.md' }),
  @('block', 'write_file', @{ file_path = 'Orders.oml'; content = 'x' }),
  @('block', 'replace', @{ file_path = 'Orders.oml'; old_string = 'a'; new_string = 'b' }),
  @('block', 'read_file', @{ absolute_path = 'C:\p\skill\reference\spec.md' }),
  @('allow', 'read_file', @{ absolute_path = 'C:\p\skill\SKILL.md' }),
  @('block', 'run_shell_command', @{ command = 'sed -i s/a/b/ Orders.oml' }),
  @('allow', 'run_shell_command', @{ command = 'oml inspect Orders.oml' }),
  @('block', 'apply_patch', @{ command = "*** Begin Patch`n*** Update File: modules/Orders.oml`n@@`n-a`n+b`n*** End Patch" }),
  @('allow', 'apply_patch', @{ command = "*** Begin Patch`n*** Update File: specs/change.json`n@@`n-a`n+b`n*** End Patch" }),
  # A part of a large reference, as each agent names the range: Copilot's view, VS Code's read_file
  @('allow', 'Read', @{ path = 'C:\p\skill\reference\spec.md'; view_range = @(100, 300) }),
  @('block', 'Read', @{ path = 'C:\p\skill\reference\spec.md'; view_range = @(1, -1) }),
  @('block', 'Read', @{ path = 'C:\p\skill\reference\spec.md'; view_range = @(1, 900) }),
  @('allow', 'read_file', @{ filePath = 'skill/reference/commands.md'; startLine = 10; endLine = 120 }),
  @('block', 'read_file', @{ filePath = 'skill/reference/commands.md'; startLine = 1; endLine = 5000 }),
  # The edit tools of VS Code's Local harness
  @('block', 'replace_string_in_file', @{ filePath = 'modules/Orders.oml'; oldString = 'a'; newString = 'b' }),
  @('block', 'create_file', @{ filePath = 'Orders.oml'; content = 'x' }),
  # Without a project profile nothing stops a browser
  @('allow', 'Bash', @{ command = 'Start-Process msedge https://dev.example.com' })
)
foreach ($c in $cases) {
  $json = @{ tool_name = $c[1]; tool_input = $c[2]; hook_event_name = 'PreToolUse' } | ConvertTo-Json -Compress
  $out = $json | & $Oml hooks check 2>&1
  $err = @($out | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] }) -join ' '
  $got = if ($LASTEXITCODE -eq 2) { 'block' } elseif ($LASTEXITCODE -eq 0) { 'allow' } else { "exit $LASTEXITCODE" }
  Require ($got -eq $c[0]) "$($c[1]) $($c[2] | ConvertTo-Json -Compress): $got, expected $($c[0]) $err"
  if ($got -eq 'block') {
    Require ("$err" -match '^oml hook: ') "refusal without a reason: $err"
    $decision = (@($out | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) -join '') | ConvertFrom-Json
    Require ($decision.permissionDecision -eq 'deny' -and $decision.hookSpecificOutput.permissionDecision -eq 'deny' -and $decision.decision -eq 'deny') "deny decision on stdout: $out"
  }
}
# Copilot's own preToolUse shape: toolName and toolArgs as a JSON string
$copilot = @{ toolName = 'edit'; toolArgs = (@{ path = 'Orders.oml' } | ConvertTo-Json -Compress) } | ConvertTo-Json -Compress
$copilot | & $Oml hooks check 2>&1 | Out-Null
Require ($LASTEXITCODE -eq 2) "Copilot toolName/toolArgs edit of an .oml file was not refused (exit $LASTEXITCODE)"
$copilot = @{ toolName = 'bash'; toolArgs = (@{ command = 'ls' } | ConvertTo-Json -Compress) } | ConvertTo-Json -Compress
$copilot | & $Oml hooks check 2>&1 | Out-Null
Require ($LASTEXITCODE -eq 0) "Copilot toolName/toolArgs ls was refused (exit $LASTEXITCODE)"
foreach ($junk in 'not json', '{}', '{"tool_name":"Read"}', '[1,2]', '') {
  $junk | & $Oml hooks check 2>&1 | Out-Null
  Require ($LASTEXITCODE -eq 0) "unreadable input '$junk' was not allowed (exit $LASTEXITCODE)"
}
$r = '{"tool_name":"Read","tool_input":{"file_path":"skill/reference/commands.md"}}' | & $Oml hooks check 2>&1
Require ("$r" -match 'oml doc commands#') "the commands.md refusal names oml doc commands: $r"

# 1b. the hook command line: no leading quote for a plain path (PowerShell, which Copilot uses on Windows, reads a
# line that starts with a quote as a string and the hook fails); the project profile: guards on and off, the browser
# guard, the session-start text
$prof = Join-Path $work 'profile'; New-Item -ItemType Directory -Force $prof | Out-Null
$r = Call 'hooks', 'install', '--dir', $prof, '--agent', 'claude'
$ps = Get-Content (Join-Path $prof '.claude\settings.local.json') -Raw | ConvertFrom-Json
if ($Oml -match '^[A-Za-z0-9_./:~+\\-]+$') {
  Require ($ps.hooks.PreToolUse[0].hooks[0].command -match '^[^"]\S* hooks check$' -and $ps.hooks.PreToolUse[0].hooks[0].command -notmatch '\\') "plain path is written without quotes and with forward slashes: $($ps.hooks.PreToolUse[0].hooks[0].command)"
} else {
  Require ($ps.hooks.PreToolUse[0].hooks[0].powershell -match '^& "') "a path that needs quotes gets a powershell line: $($ps.hooks.PreToolUse[0].hooks[0] | ConvertTo-Json -Compress)"
}
Require ($ps.hooks.SessionStart[0].hooks[0].command -match 'hooks session$') "session-start hook: $($ps | ConvertTo-Json -Compress -Depth 6)"
function Hook($verb, $payload) { $out = (($payload | ConvertTo-Json -Compress) | & $Oml hooks $verb 2>$null) -join ''; @{ Exit = $LASTEXITCODE; Out = $out } }
Require ((Hook 'session' @{ hook_event_name = 'SessionStart'; cwd = $prof }).Out -eq '') 'session hook printed a profile without a config file'
Push-Location $prof
try {
  & $Oml config set ui.targets desktop | Out-Null; & $Oml config set browser agent | Out-Null; & $Oml config set ask.testing off | Out-Null
  Require ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $prof '.oml\config.json'))) 'oml config set writes .oml/config.json'
  $x = Hook 'session' @{ hook_event_name = 'SessionStart'; cwd = $prof }
  $ctx = ($x.Out | ConvertFrom-Json)
  Require ($x.Exit -eq 0 -and $ctx.additionalContext -match 'desktop only' -and $ctx.additionalContext -match 'Do not ask whether to test' -and $ctx.hookSpecificOutput.additionalContext -eq $ctx.additionalContext) "session profile: $($x.Out)"
  Require ((Hook 'check' @{ tool_name = 'Bash'; cwd = $prof; tool_input = @{ command = 'Start-Process msedge https://dev.example.com' } }).Exit -eq 2) 'browser = agent did not refuse starting Edge'
  Require ((Hook 'check' @{ tool_name = 'Bash'; cwd = $prof; tool_input = @{ command = 'oml qa page https://dev.example.com --screenshot a.png' } }).Exit -eq 0) 'browser = agent refused oml qa'
  & $Oml config set hooks.bigDocs off | Out-Null
  Require ((Hook 'check' @{ tool_name = 'Read'; cwd = $prof; tool_input = @{ file_path = 'skill/reference/commands.md' } }).Exit -eq 0) 'hooks.bigDocs off still refused the read'
  Require ((Hook 'check' @{ tool_name = 'Edit'; cwd = $prof; tool_input = @{ file_path = 'Orders.oml' } }).Exit -eq 2) 'the .oml write guard must stay on'
  & $Oml config set hooks.omlWrites off 2>&1 | Out-Null
  Require ($LASTEXITCODE -eq 2) 'a locked setting was turned off'
  & $Oml config set ui.targets watch 2>&1 | Out-Null
  Require ($LASTEXITCODE -eq 2) 'an unknown value was accepted'
  # preview and qa follow the sizes and themes; the MCP address stays on this computer
  $x = (& $Oml preview x.oml Home --width 390 2>&1) -join ''
  Require ($LASTEXITCODE -eq 2 -and $x -match 'phone size') "preview --width 390 in a desktop-only project: $x"
  $x = (& $Oml qa page http://127.0.0.1:9 --width 390 2>&1) -join ''
  Require ($LASTEXITCODE -eq 2 -and $x -match 'phone size') "qa --width 390 in a desktop-only project: $x"
  & $Oml config set outsystems.mcpServer http://example.com/mcp 2>&1 | Out-Null
  Require ($LASTEXITCODE -eq 2) 'a Service Studio MCP address on another machine was accepted'
  & $Oml config set outsystems.mcpServer http://127.0.0.1:41999/mcp | Out-Null
  Require ($LASTEXITCODE -eq 0 -and ((& $Oml config) -join '') -match '41999') 'outsystems.mcpServer is not in the profile'
  # oml style replace on a CSS file: one class only, a longer value and a comment stay
  $cssIn = Join-Path $prof 'a.css'; $cssOut = Join-Path $prof 'b.css'
  Set-Content $cssIn "/* 45px */`n.card { height: 45px; margin: 145px; }`n.card-title { height: 45px; }`n@media (max-width: 600px) { .card { top: 45px } }"
  $x = ((& $Oml style replace $cssIn --from 45px --to 75px --class card --out $cssOut) -join '') | ConvertFrom-Json
  $got = Get-Content $cssOut -Raw
  Require ($LASTEXITCODE -eq 0 -and $x.lines -eq 2 -and $got -match '\.card \{ height: 75px; margin: 145px; \}' -and $got -match '\.card-title \{ height: 45px' -and $got -match 'top: 75px' -and $got -match '/\* 45px \*/') "style replace: $got"
  Set-Content (Join-Path $prof '.oml\config.json') '{ broken'
  Require ((Hook 'check' @{ tool_name = 'Bash'; cwd = $prof; tool_input = @{ command = 'ls' } }).Exit -eq 0) 'a broken config file blocked the agent'
} finally { Pop-Location }
Write-Host 'ok   hooks: command line for bash and PowerShell, session profile, config: guards on and off, browser guard, locked settings, a broken file never blocks'

# 2. install / status / remove keep the user's settings and their own hooks
$project = Join-Path $work 'project'; New-Item -ItemType Directory -Force (Join-Path $project '.claude') | Out-Null
$settings = Join-Path $project '.claude\settings.local.json'
Set-Content $settings '{ "permissions": { "allow": ["Bash(git status)"] }, "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo mine" } ] } ] } }'
$r = Call 'hooks', 'status', '--dir', $project, '--agent', 'claude'
Require ($r.Exit -eq 0 -and $r.Json.agents[0].installed -eq $false -and $r.Json.next -match 'install') "status before install: $($r.Text)"
$r = Call 'hooks', 'install', '--dir', $project, '--agent', 'claude'
Require ($r.Exit -eq 0 -and @($r.Json.agents).Count -eq 1 -and $r.Json.agents[0].installed -and -not $r.Json.agents[0].replaced -and -not (Test-Path (Join-Path $project '.codex'))) "install claude only: $($r.Text)"
$r = Call 'hooks', 'install', '--dir', $project
Require ($r.Exit -eq 0 -and @($r.Json.agents).Count -eq 3 -and ($r.Json.agents | Where-Object agent -eq 'claude').replaced) "install all: $($r.Text)"
$s = Get-Content $settings -Raw | ConvertFrom-Json
Require (@($s.hooks.PreToolUse).Count -eq 2 -and $s.permissions.allow[0] -eq 'Bash(git status)' -and $s.hooks.PreToolUse[0].hooks[0].command -eq 'echo mine') "install changed the user's settings: $(Get-Content $settings -Raw)"
Require ($s.hooks.PreToolUse[1].matcher -match 'Read' -and $s.hooks.PreToolUse[1].matcher -match 'StrReplace' -and $s.hooks.PreToolUse[1].hooks[0].command -match 'oml\.exe"? hooks check$' -and (Get-Content $settings -Raw) -notmatch '\\u0022') "hook entry: $(Get-Content $settings -Raw)"
$codex = Get-Content (Join-Path $project '.codex\hooks.json') -Raw | ConvertFrom-Json
Require ($codex.hooks.PreToolUse[0].matcher -match 'apply_patch' -and $codex.hooks.PreToolUse[0].hooks[0].command -match 'hooks check$') "codex hooks.json: $(Get-Content (Join-Path $project '.codex\hooks.json') -Raw)"
$gemini = Get-Content (Join-Path $project '.gemini\settings.json') -Raw | ConvertFrom-Json
Require ($gemini.hooks.BeforeTool[0].matcher -match 'run_shell_command' -and $gemini.hooks.BeforeTool[0].hooks[0].timeout -eq 10000) "gemini settings.json: $(Get-Content (Join-Path $project '.gemini\settings.json') -Raw)"
foreach ($x in @(@($s, 'Stop'), @($codex, 'Stop'), @($gemini, 'AfterAgent'))) {
  $entry = @($x[0].hooks.($x[1]))
  Require ($entry.Count -eq 1 -and $entry[0].matcher -eq '' -and $entry[0].hooks[0].command -match 'oml\.exe"? hooks stop$') "end-of-turn hook $($x[1]): $($entry | ConvertTo-Json -Compress -Depth 5)"
}
$r = Call 'hooks', '--dir', $project
Require ($r.Exit -eq 0 -and @($r.Json.agents | Where-Object { $_.installed -and $_.exeFound -and $_.memoryReminder }).Count -eq 3 -and -not $r.Json.next) "status after install: $($r.Text)"
$r = Call 'hooks', 'remove', '--dir', $project
Require ($r.Exit -eq 0 -and @($r.Json.agents | Where-Object removed).Count -eq 3) "remove: $($r.Text)"
$s = Get-Content $settings -Raw | ConvertFrom-Json
Require (@($s.hooks.PreToolUse).Count -eq 1 -and $s.hooks.PreToolUse[0].hooks[0].command -eq 'echo mine') "remove took more than oml's entry: $(Get-Content $settings -Raw)"
Require (-not (Test-Path (Join-Path $project '.codex\hooks.json')) -and -not (Test-Path (Join-Path $project '.gemini\settings.json'))) "remove left files that held only oml's hook"

# a Gemini settings file with the user's own settings keeps them
$gem = Join-Path $project '.gemini\settings.json'
Set-Content $gem '{ "theme": "Dark" }'
Call 'hooks', 'install', '--dir', $project, '--agent', 'gemini' | Out-Null
Call 'hooks', 'remove', '--dir', $project, '--agent', 'gemini' | Out-Null
Require ((Get-Content $gem -Raw | ConvertFrom-Json).theme -eq 'Dark' -and (Get-Content $gem -Raw) -notmatch 'hooks') "gemini remove: $(Get-Content $gem -Raw)"

# a project without .claude: install creates the files; remove of the only hooks drops them
$fresh = Join-Path $work 'fresh'; New-Item -ItemType Directory -Force $fresh | Out-Null
$r = Call 'hooks', 'install', '--dir', $fresh
Require ($r.Exit -eq 0 -and (Test-Path (Join-Path $fresh '.claude\settings.local.json'))) "install in a fresh folder: $($r.Text)"
$r = Call 'hooks', 'remove', '--dir', $fresh
Require ($r.Exit -eq 0 -and -not (Test-Path (Join-Path $fresh '.claude\settings.local.json'))) "remove in a fresh folder left the file"
# 3. refusals: a broken settings file is never overwritten; bad options and folders
Set-Content $settings '{ broken'
$r = Call 'hooks', 'install', '--dir', $project
Require ($r.Exit -eq 2 -and $r.Json.error -match 'not valid JSON' -and (Get-Content $settings -Raw).Trim() -eq '{ broken') "install over a broken file: $($r.Text)"
$r = Call 'hooks', 'install', '--dir', (Join-Path $work 'missing')
Require ($r.Exit -eq 2 -and $r.Json.error -match 'Folder not found') "missing folder: $($r.Text)"
$r = Call 'hooks', 'frobnicate'
Require ($r.Exit -eq 2 -and $r.Json.error -match 'Unknown hooks command') "unknown subcommand: $($r.Text)"
$r = Call 'hooks', 'install', '--agent', 'windsurf'
Require ($r.Exit -eq 2 -and $r.Json.error -match 'Unknown agent') "unknown agent: $($r.Text)"
$r = Call 'hooks', 'install', '--bogus'
Require ($r.Exit -eq 2 -and $r.Json.error -match 'Unknown or incomplete option') "unknown option: $($r.Text)"
$r = Call 'hooks', '--help'
Require ($r.Exit -eq 0 -and $r.Text -match 'oml hooks') "help: $($r.Text)"

# 4. the end-of-turn memory reminder: once, only after a change or qa with nothing saved, only with a memory store
$saved = @{}; foreach ($n in 'OML_LOG', 'OML_MEMORY_REMINDER') { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
try {
  $mem = Join-Path $work 'memproject'; New-Item -ItemType Directory -Force $mem | Out-Null
  $env:OML_LOG = '0'
  Push-Location $mem; try { & $Oml memory init --force | Out-Null } finally { Pop-Location }
  Require (Test-Path (Join-Path $mem '.oml\memory\memory.log')) 'memory init for the reminder test'
  $log = Join-Path $work 'usage.jsonl'; $env:OML_LOG = $log; $env:OML_MEMORY_REMINDER = $null
  function Line($minutesAgo, $cmd, $exit = 0) { '{"ts":"' + [DateTime]::UtcNow.AddMinutes(-$minutesAgo).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") + '","cmd":"' + $cmd + '","exit":' + $exit + ',"ms":10,"bytes":1}' }
  function Stop-Hook($payload) {
    $out = (($payload | ConvertTo-Json -Compress) | & $Oml hooks stop 2>&1) -join ''
    Require ($LASTEXITCODE -eq 0) "hooks stop exit $LASTEXITCODE`: $out"
    if ($out.Trim()) { $out | ConvertFrom-Json } else { $null }
  }
  Set-Content $log @((Line 300 'apply'), (Line 20 'inspect'), (Line 10 'apply'), (Line 9 'install'), (Line 8 'apply' 1))
  $d = Stop-Hook @{ hook_event_name = 'Stop'; cwd = $mem; stop_hook_active = $false; session_id = 'x' }
  Require ($d.decision -eq 'block' -and $d.reason -match 'apply 1x, install 1x' -and $d.reason -match 'oml memory add' -and $d.reason -match 'nothing is worth keeping') "reminder after apply and install: $($d | ConvertTo-Json -Compress)"
  $d = Stop-Hook @{ hook_event_name = 'AfterAgent'; cwd = $mem; stop_hook_active = $false }
  Require ($d.decision -eq 'deny' -and $d.reason) "Gemini CLI AfterAgent gets deny: $($d | ConvertTo-Json -Compress)"
  $d = Stop-Hook @{ sessionId = 'x'; cwd = $mem; stopReason = 'end_turn' }
  Require ($d.decision -eq 'block') "Copilot agentStop payload: $($d | ConvertTo-Json -Compress)"
  Require ($null -eq (Stop-Hook @{ hook_event_name = 'Stop'; cwd = $mem; stop_hook_active = $true })) 'reminded again while already continuing (stop_hook_active)'
  Require ($null -eq (Stop-Hook @{ hook_event_name = 'Stop'; cwd = $work })) 'reminded in a folder without a memory store'
  $transcript = Join-Path $work 'transcript.jsonl'; Set-Content $transcript '{}'
  Require ($null -eq (Stop-Hook @{ hook_event_name = 'Stop'; cwd = $mem; transcript_path = $transcript })) 'reminded for changes made before this session started'
  $env:OML_MEMORY_REMINDER = '0'
  Require ($null -eq (Stop-Hook @{ hook_event_name = 'Stop'; cwd = $mem })) 'reminded with OML_MEMORY_REMINDER=0'
  $env:OML_MEMORY_REMINDER = $null
  Add-Content $log (Line 1 'memory add')
  Require ($null -eq (Stop-Hook @{ hook_event_name = 'Stop'; cwd = $mem })) 'reminded after a memory add'
  Set-Content $log @((Line 10 'inspect'), (Line 5 'describe'), (Line 4 'apply' 1))
  Require ($null -eq (Stop-Hook @{ hook_event_name = 'Stop'; cwd = $mem })) 'reminded after a session that only read (and a failed apply)'
  Set-Content $log @((Line 3 'qa'))
  Require ((Stop-Hook @{ hook_event_name = 'Stop'; cwd = $mem }).reason -match 'qa 1x') 'no reminder after a qa run'
  foreach ($junk in 'not json', '', '[1]') {
    $out = $junk | & $Oml hooks stop 2>&1
    Require ($LASTEXITCODE -eq 0 -and -not "$out".Trim()) "unreadable stop input '$junk': exit $LASTEXITCODE $out"
  }
  Write-Host 'ok   hooks: memory reminder once after a change or qa with nothing saved; quiet when continuing, without a store, before the session, after memory add, for reading only, when off'
} finally { foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) } }

Write-Host "ok   hooks: $($cases.Count) decisions (large references, .oml writes by tool, patch and shell; Claude, Copilot, Cursor, Codex, Gemini payloads), unreadable input allowed, install/status/remove for 3 agents keep the user's settings, refusals"
} finally { Remove-TestWorkFolder $Tmp }
