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
  @('allow', 'apply_patch', @{ command = "*** Begin Patch`n*** Update File: specs/change.json`n@@`n-a`n+b`n*** End Patch" })
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
Require ($s.hooks.PreToolUse[1].matcher -match 'Read' -and $s.hooks.PreToolUse[1].matcher -match 'StrReplace' -and $s.hooks.PreToolUse[1].hooks[0].command -match 'oml\.exe" hooks check$' -and (Get-Content $settings -Raw) -notmatch '\\u0022') "hook entry: $(Get-Content $settings -Raw)"
$codex = Get-Content (Join-Path $project '.codex\hooks.json') -Raw | ConvertFrom-Json
Require ($codex.hooks.PreToolUse[0].matcher -match 'apply_patch' -and $codex.hooks.PreToolUse[0].hooks[0].command -match 'hooks check$') "codex hooks.json: $(Get-Content (Join-Path $project '.codex\hooks.json') -Raw)"
$gemini = Get-Content (Join-Path $project '.gemini\settings.json') -Raw | ConvertFrom-Json
Require ($gemini.hooks.BeforeTool[0].matcher -match 'run_shell_command' -and $gemini.hooks.BeforeTool[0].hooks[0].timeout -eq 10000) "gemini settings.json: $(Get-Content (Join-Path $project '.gemini\settings.json') -Raw)"
$r = Call 'hooks', '--dir', $project
Require ($r.Exit -eq 0 -and @($r.Json.agents | Where-Object { $_.installed -and $_.exeFound }).Count -eq 3 -and -not $r.Json.next) "status after install: $($r.Text)"
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

Write-Host "ok   hooks: $($cases.Count) decisions (large references, .oml writes by tool, patch and shell; Claude, Copilot, Cursor, Codex, Gemini payloads), unreadable input allowed, install/status/remove for 3 agents keep the user's settings, refusals"
} finally { Remove-TestWorkFolder $Tmp }
