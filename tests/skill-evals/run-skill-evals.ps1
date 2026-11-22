#requires -Version 7.0
# Measures the oml skill (skill/) the way Anthropic's skill-creator does (github.com/anthropics/skills, skills/skill-creator),
# with `claude -p` and a cheap model. Not part of tests/regress.ps1: it calls a model and costs usage. Run it by hand.
#
# Trigger evals (trigger-evals.json, the skill-creator eval-set format): for each query, a temporary project gets a command
# file with the SKILL.md description (as skill-creator's scripts/run_eval.py does), and `claude -p <query>` runs. The query
# counts as triggered when the model's first tool call is Skill or Read of that command. skill-creator's run_eval.py reads
# the stream with select() on a pipe, which does not work on Windows; this script does the same detection with a timed read.
# Task evals (evals.json, the skill-creator evals format plus `checks`): `claude -p <prompt>` runs in a temporary project
# that holds a copy of skill/ as .claude/skills/outsystems-oml, with only Skill, Read, Glob and Grep allowed, and the answer
# must match every regex in `checks`. Prompts say "do not run anything"; nothing touches a module or a server.
#
#   pwsh -NoProfile -File tests/skill-evals/run-skill-evals.ps1                       # both, claude-haiku-5-5, one run each
#   pwsh -NoProfile -File tests/skill-evals/run-skill-evals.ps1 -Only trigger -Description "<new description>"
#
# Needs the Claude Code CLI (`claude`, logged in; set OML_CLAUDE to claude.exe if it is not on PATH). Sets no API key.
# Remove an installed copy of the skill (%USERPROFILE%\.claude\skills\outsystems-oml) first, or the model may pick that one,
# which counts as not triggered. Results: tmp/skill-evals/<time>.json (tmp/ is ignored).
param(
  [string]$Model = 'claude-haiku-5-5',
  [ValidateSet('all', 'trigger', 'task')][string]$Only = 'all',
  [string]$Description,
  [int]$Timeout = 60,
  [int]$TaskTimeout = 240
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$skill = Join-Path $repo 'skill'
$claude = $env:OML_CLAUDE
if (-not $claude) { $claude = (Get-Command claude -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)?.Source }
if (-not $claude) { Write-Host 'run-skill-evals: the claude CLI is not on PATH. Install Claude Code and log in, or set OML_CLAUDE to claude.exe.'; exit 2 }

$front = [regex]::Match((Get-Content -Raw (Join-Path $skill 'SKILL.md')), '(?s)^---\r?\n(.*?)\r?\n---')
$name = [regex]::Match($front.Groups[1].Value, '(?m)^name:\s*(.+)$').Groups[1].Value.Trim()
if (-not $Description) { $Description = [regex]::Match($front.Groups[1].Value, '(?m)^description:\s*(.+)$').Groups[1].Value.Trim() }
if (Test-Path (Join-Path $HOME ".claude/skills/$name")) { Write-Warning "An installed copy of the skill is in $HOME\.claude\skills\$name; the model may pick it instead of the tested one." }

$work = Join-Path ([IO.Path]::GetTempPath()) ('oml-skill-evals-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$outDir = Join-Path $repo 'tmp/skill-evals'
New-Item -ItemType Directory -Force $work, $outDir | Out-Null

# Starts claude with the arguments in a project folder; returns the process with stdout redirected (UTF-8).
function Start-Claude([string]$Folder, [string[]]$Arguments) {
  $psi = [Diagnostics.ProcessStartInfo]::new($claude)
  foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
  $psi.WorkingDirectory = $Folder; $psi.UseShellExecute = $false
  $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.RedirectStandardInput = $true
  $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
  [void]$psi.Environment.Remove('CLAUDECODE')
  $p = [Diagnostics.Process]::Start($psi)
  $p.StandardInput.Close()
  [void]$p.StandardError.ReadToEndAsync()
  $p
}
# Reads stdout events until $OnEvent returns a value other than $null, the stream ends or the time is up.
function Read-Events($Process, [int]$Seconds, [scriptblock]$OnEvent) {
  $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
  try {
    while ($true) {
      $left = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
      if ($left -le 0) { return 'timeout' }
      $task = $Process.StandardOutput.ReadLineAsync()
      if (-not $task.Wait($left)) { return 'timeout' }
      $line = $task.Result
      if ($null -eq $line) { return 'end' }
      if (-not $line.Trim()) { continue }
      try { $e = $line | ConvertFrom-Json -Depth 64 } catch { continue }
      $r = & $OnEvent $e
      if ($null -ne $r) { return $r }
    }
  } finally { if (-not $Process.HasExited) { try { $Process.Kill($true) } catch { } } }
}

$report = [ordered]@{ model = $Model; skill = $name; description = $Description; at = (Get-Date).ToString('o') }
try {
  if ($Only -in 'all', 'trigger') {
    $project = Join-Path $work 'trigger'; $commands = Join-Path $project '.claude/commands'
    New-Item -ItemType Directory -Force $commands | Out-Null
    $rows = foreach ($item in (Get-Content -Raw (Join-Path $PSScriptRoot 'trigger-evals.json') | ConvertFrom-Json)) {
      $clean = "$name-skill-" + [guid]::NewGuid().ToString('N').Substring(0, 8)
      $file = Join-Path $commands "$clean.md"
      $indented = ($Description -split "`n") -join "`n  "
      Set-Content -LiteralPath $file -Encoding utf8NoBOM -Value "---`ndescription: |`n  $indented`n---`n`n# $name`n`nThis skill handles: $Description`n"
      $state = @{ pending = $null; json = ''; seen = $false }
      try {
        $p = Start-Claude $project @('-p', $item.query, '--output-format', 'stream-json', '--verbose', '--include-partial-messages', '--model', $Model)
        $r = Read-Events $p $Timeout {
          param($e)
          if ($e.type -eq 'stream_event') {
            $se = $e.event
            if ($se.type -eq 'content_block_start' -and $se.content_block.type -eq 'tool_use') {
              if ($se.content_block.name -in 'Skill', 'Read') { $state.pending = $se.content_block.name; $state.json = '' } else { return 'no' }
            } elseif ($se.type -eq 'content_block_delta' -and $state.pending -and $se.delta.type -eq 'input_json_delta') {
              $state.json += $se.delta.partial_json
              if ($state.json.Contains($clean)) { return 'yes' }
            } elseif ($se.type -in 'content_block_stop', 'message_stop') {
              if ($state.pending) { return ($state.json.Contains($clean) ? 'yes' : 'no') }
              if ($se.type -eq 'message_stop') { return 'no' }
            }
          } elseif ($e.type -eq 'assistant') {
            foreach ($c in @($e.message.content | Where-Object { $_.type -eq 'tool_use' })) {
              return ((($c.input | ConvertTo-Json -Compress -Depth 8) -match [regex]::Escape($clean)) -and $c.name -in 'Skill', 'Read') ? 'yes' : 'no'
            }
          } elseif ($e.type -eq 'result') { return 'no' }
          $null
        }
      } finally { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
      $triggered = $r -eq 'yes'
      [pscustomobject]@{ query = $item.query; should_trigger = [bool]$item.should_trigger; triggered = $triggered; outcome = $r; pass = ($triggered -eq [bool]$item.should_trigger) }
      Write-Host ("{0} trigger={1,-5} expected={2,-5} {3}" -f ($triggered -eq [bool]$item.should_trigger ? 'PASS' : 'FAIL'), $triggered, [bool]$item.should_trigger, $item.query.Substring(0, [Math]::Min(80, $item.query.Length)))
    }
    $rows = @($rows)
    $pos = @($rows | Where-Object should_trigger); $neg = @($rows | Where-Object { -not $_.should_trigger })
    $report.trigger = [ordered]@{
      passed = @($rows | Where-Object pass).Count; total = $rows.Count
      shouldTrigger = "$(@($pos | Where-Object triggered).Count)/$($pos.Count) triggered"
      shouldNotTrigger = "$(@($neg | Where-Object { -not $_.triggered }).Count)/$($neg.Count) stayed quiet"
      timeouts = @($rows | Where-Object outcome -eq 'timeout').Count
      results = $rows
    }
    Write-Host "trigger accuracy: $($report.trigger.passed)/$($report.trigger.total) ($($report.trigger.shouldTrigger); $($report.trigger.shouldNotTrigger))"
  }

  if ($Only -in 'all', 'task') {
    $project = Join-Path $work 'task'
    New-Item -ItemType Directory -Force (Join-Path $project '.claude/skills') | Out-Null
    Copy-Item -Recurse -LiteralPath $skill -Destination (Join-Path $project ".claude/skills/$name")
    $evals = (Get-Content -Raw (Join-Path $PSScriptRoot 'evals.json') | ConvertFrom-Json).evals
    $cost = 0.0
    $rows = foreach ($ev in $evals) {
      $state = @{ text = ''; cost = $null; skill = $false; turns = $null }
      $p = Start-Claude $project @('-p', $ev.prompt, '--output-format', 'stream-json', '--verbose', '--model', $Model, '--max-turns', '12',
        '--allowedTools', 'Skill Read Glob Grep', '--disallowedTools', 'Bash PowerShell Write Edit NotebookEdit WebFetch WebSearch')
      $r = Read-Events $p $TaskTimeout {
        param($e)
        if ($e.type -eq 'assistant') {
          foreach ($c in @($e.message.content | Where-Object { $_.type -eq 'tool_use' })) {
            if (($c.input | ConvertTo-Json -Compress -Depth 8) -match [regex]::Escape($name)) { $state.skill = $true }
          }
        } elseif ($e.type -eq 'result') { $state.text = [string]$e.result; $state.cost = $e.total_cost_usd; $state.turns = $e.num_turns; return 'done' }
        $null
      }
      if ($state.cost) { $cost += [double]$state.cost }
      $checks = foreach ($c in $ev.checks) { [pscustomobject]@{ text = $c.text; pass = [regex]::IsMatch($state.text, $c.regex, 'IgnoreCase') } }
      $ok = $r -eq 'done' -and -not @($checks | Where-Object { -not $_.pass }).Count
      Write-Host ("{0} task {1}: skill loaded={2}, {3} of {4} checks, cost {5}" -f ($ok ? 'PASS' : 'FAIL'), $ev.id, $state.skill, @($checks | Where-Object pass).Count, @($checks).Count, $state.cost)
      [pscustomobject]@{ id = $ev.id; pass = $ok; outcome = $r; skillLoaded = $state.skill; turns = $state.turns; costUsd = $state.cost; checks = @($checks); answer = $state.text }
    }
    $rows = @($rows)
    $report.task = [ordered]@{ passed = @($rows | Where-Object pass).Count; total = $rows.Count; costUsd = [Math]::Round($cost, 4); results = $rows }
    Write-Host "task evals: $($report.task.passed)/$($report.task.total) passed; cost $($report.task.costUsd) USD (from claude's result events)"
  }
} finally {
  Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction SilentlyContinue
}
$file = Join-Path $outDir ((Get-Date).ToString('yyyyMMdd-HHmmss') + '.json')
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $file -Encoding utf8NoBOM
Write-Host "results: $file"
