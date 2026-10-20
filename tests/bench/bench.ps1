<#
.SYNOPSIS
    Runs one benchmark task on the OutSystems MCP side, the oml side, or both, and writes the results.

.DESCRIPTION
    Each agent is one headless Claude Code run (claude -p). Each side gets its own run folder under -Out with
    the skill it uses installed the way its official setup does it, the same task text, and a short side
    preamble. Writes results.json and results.md to -Out.

    Real runs use Claude API usage and cost money. Use -DryRun first. See tests/bench/README.md.

.PARAMETER Task
    Task file, for example tests/bench/tasks/showcase-24.md. A line that starts with "Expect:" is not sent to
    the agents; verify.ps1 checks those rules after an oml run.

.PARAMETER Side
    mcp   = OutSystems MCP server (Service Studio, module open) + the servicestudio-mcp-oml skill.
    oml   = the oml CLI + the skill in this repository's skill/ folder. Needs -Module.
    both  = mcp, then oml, one after the other, from the same starting module.

.PARAMETER Mode
    single = one agent per side.
    swarm  = the numbered components are split over -Agents agents that run in parallel, then one assembly agent.

.PARAMETER Model
    Any model id that claude accepts. The default is the model the published numbers use.

.PARAMETER Module
    The .oml file the oml side starts from. The script copies it first; the original is not changed.

.PARAMETER Out
    A new results folder. It must not exist yet, and it must be outside any project folder.

.PARAMETER OmlExe
    Path to oml.exe. Default: oml from PATH.

.PARAMETER SkillPath
    Folder that holds a servicestudio-mcp-oml SKILL.md (the skill folder itself). Without it, the mcp side
    needs -AllowDownload.

.PARAMETER SkillRef
    Optional git tag, branch or commit of OutSystems/outsystems11-mcp to use when cloning.

.PARAMETER AllowDownload
    Allows the script to clone https://github.com/OutSystems/outsystems11-mcp for the mcp skill. The URL is
    printed before anything is downloaded.

.PARAMETER DryRun
    Creates the run folders, prompts and mcp.json files, and prints the exact claude commands. Runs nothing,
    downloads nothing, and calls no network service.

.PARAMETER Yes
    Skips the confirmation prompt before a real run.

.EXAMPLE
    pwsh -NoProfile -File tests/bench/bench.ps1 -Task tests/bench/tasks/showcase-24.md -Side oml -Mode swarm -Module 'C:\bench\start\OML-parallel.oml' -Out 'C:\bench\run1'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Task,
    [Parameter(Mandatory)][ValidateSet('mcp', 'oml', 'both')][string]$Side,
    [ValidateSet('single', 'swarm')][string]$Mode = 'single',
    [ValidateRange(1, 64)][int]$Agents = 8,
    [string]$Model = 'claude-haiku-5-5',
    [string]$Module,
    [Parameter(Mandatory)][string]$Out,
    [string]$OmlExe,
    [string]$SkillPath,
    [string]$SkillRef,
    [switch]$AllowDownload,
    [switch]$DryRun,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$SkillRepo = 'https://github.com/OutSystems/outsystems11-mcp'
$McpSkillName = 'servicestudio-mcp-oml'
$OmlSkillName = 'outsystems-oml'
$DefaultStudioPort = 41820
$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$VerifyScript = Join-Path $PSScriptRoot 'verify.ps1'
$Utf8 = [Text.UTF8Encoding]::new($false)
$Inv = [Globalization.CultureInfo]::InvariantCulture

# ---------- prompts (text is data; nothing here is interpolated from the task file) ----------

$McpPreamble = @'
You work on the OutSystems 11 module that is open in Service Studio, using ONLY the OutSystems MCP server `servicestudio` and the `servicestudio-mcp-oml` skill. Do not use any `oml` command line tool, and do not read or edit .oml files yourself. Service Studio is set to merge changes automatically. Do not publish and do not install anything. A human may need to approve the first connection dialog; retry `createSessionToken` for up to 2 minutes if it is pending. Never write a session token or any other secret into a file or into your reply.
'@

# ---------- helpers ----------

function Write-Utf8([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, $Utf8) }

function Get-Sha256([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

# Removes values that follow token-like keys before any text is saved. Best effort: the agents are told not to print secrets.
function Hide-Secrets([string]$Text) {
    if (-not $Text) { return '' }
    $t = [regex]::Replace($Text, '(?i)\b(bearer)\s+[A-Za-z0-9._~+/=-]{8,}', '$1 [redacted]')
    return [regex]::Replace($t, '(?i)((?:token|secret|password|passwd|api[_-]?key)\w*["'']?\s*[:=]\s*["'']?)[^\s"'',;]{8,}', '$1[redacted]')
}

function Join-Prompt([string[]]$Parts) { ($Parts | Where-Object { $_ }) -join "`n`n" }

function Format-Components($Items) { (@($Items) | ForEach-Object { "$($_.Number). $($_.Text)" }) -join "`n" }

function Format-List($Items) {
    $list = @($Items | Where-Object { $_ })
    if ($list.Count) { $list -join ', ' } else { 'none' }
}

function Get-OmlPreamble([string]$Base) {
    'You use ONLY the `oml` command line tool and the `outsystems-oml` skill. The CLI is `' + $script:Cli + '`. Do not use any MCP server. Do not run `oml install`, `publish`, `references` or `lt`. The source module `' + $Base + '` is read-only: never write to it. Copy it into your own folder before you change anything.'
}

function Read-TaskFile([string]$Path) {
    $full = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    $expect = [Collections.Generic.List[string]]::new()
    $body = [Collections.Generic.List[string]]::new()
    foreach ($line in Get-Content -LiteralPath $full -Encoding utf8) {
        if ($line -match '^Expect:\s*(.+?)\s*$') {
            foreach ($rule in $Matches[1].Split(',')) { if ($rule.Trim()) { $expect.Add($rule.Trim()) } }
        }
        else { $body.Add($line) }
    }
    $numbered = @(0..($body.Count - 1) | Where-Object { $body[$_] -match '^\s*\d+\.\s+\S' })
    $components = @()
    $brief = ($body -join "`n").Trim()
    $tail = ''
    if ($numbered.Count -gt 0) {
        $first = $numbered[0]
        $last = $numbered[-1]
        for ($i = $first; $i -le $last; $i++) {
            if ($body[$i].Trim() -ne '' -and $body[$i] -notmatch '^\s*\d+\.\s+\S') {
                throw "Task ${full}: line $($i + 1) is between numbered components. Keep the components in one numbered list with no other text between them."
            }
        }
        $k = 0
        foreach ($i in $numbered) {
            $k++
            if ($body[$i] -notmatch "^\s*$k\.\s+(.+?)\s*$") { throw "Task ${full}: components must be numbered 1 to N in order; expected $k at line $($i + 1)." }
            $components += [pscustomobject]@{ Number = $k; Text = $Matches[1] }
        }
        $brief = if ($first -gt 0) { ($body[0..($first - 1)] -join "`n").Trim() } else { '' }
        $tail = if ($last -lt $body.Count - 1) { ($body[($last + 1)..($body.Count - 1)] -join "`n").Trim() } else { '' }
    }
    [pscustomobject]@{
        Path       = $full
        Name       = [IO.Path]::GetFileNameWithoutExtension($full)
        Sha256     = Get-Sha256 $full
        Expect     = @($expect)
        Whole      = (($body -join "`n").Trim())
        Brief      = $brief
        Components = @($components)
        Tail       = $tail
    }
}

# Splits components into contiguous chunks, one per agent. The first chunks get the extra items.
function Get-ComponentChunks($Components, [int]$AgentCount) {
    $n = [Math]::Min($AgentCount, $Components.Count)
    $size = [Math]::Floor($Components.Count / $n)
    $extra = $Components.Count % $n
    $chunks = [Collections.Generic.List[object]]::new()
    $pos = 0
    for ($a = 0; $a -lt $n; $a++) {
        $count = $size + $(if ($a -lt $extra) { 1 } else { 0 })
        $chunks.Add(@($Components[$pos..($pos + $count - 1)]))
        $pos += $count
    }
    , $chunks.ToArray()
}

function New-SinglePrompt($Task, [string]$SideName, [string]$Dir, [string]$Base) {
    if ($SideName -eq 'mcp') { return Join-Prompt @($McpPreamble, $Task.Whole) }
    $where = 'Work in this folder: ' + $Dir + '. Write intermediate files only here. The final module must be result.oml in this folder, and it must pass `oml validate result.oml --warnings`.'
    Join-Prompt @((Get-OmlPreamble $Base), $where, $Task.Whole)
}

function New-PartPrompt($Task, [string]$SideName, [int]$Index, [int]$Count, $Chunk, [string]$Dir, [string]$Base, [string]$PartFile) {
    $mine = "## Your part (agent $Index of $Count)`nBuild ONLY these components. Do not build the other parts of the task.`n" + (Format-Components $Chunk)
    if ($SideName -eq 'mcp') {
        $shared = 'Other agents are changing the same open module at the same time (each builds other components). Never delete, rename or change elements you did not create in this session. If a merge fails or your changes disappear, re-read the module and try again; report every conflict you saw.'
        $done = 'Done when: your components exist in the open module with zero validation errors. Report every failed tool call and why, every conflict with other agents, and anything you could not build.'
        return Join-Prompt @($McpPreamble, $shared, $Task.Brief, $mine, $done)
    }
    $shared = 'Other agents work at the same time on other parts, each in its own folder. Your folder: ' + $Dir
    $steps = 'Write your components as one oml spec with only a `blocks` array to ' + $PartFile + '. Test it: copy the source module to a file in your own folder, run `oml apply` from that copy to a new staged file in your own folder, then run `oml validate` on that staged file. Done when the apply has zero problems and zero new errors and ' + $PartFile + ' is final. Report every failed command and why, and anything you could not build.'
    Join-Prompt @((Get-OmlPreamble $Base), $shared, $Task.Brief, $mine, $steps)
}

function New-AssemblyPrompt($Task, [string]$SideName, [string]$Dir, [string]$Base, $ExistingParts, $MissingParts) {
    $components = 'The components below were built by other agents. Do not rebuild a component that exists. Use the ones that exist and list the ones that are missing.' + "`n" + (Format-Components $Task.Components)
    $rest = "## Your part: the rest of the task (assembly)`n" + $Task.Tail
    if ($SideName -eq 'mcp') {
        $shared = 'Other agents built the components in the open module at the same time. Never delete, rename or change elements you did not create in this session. If a merge fails or your changes disappear, re-read the module and try again; report every conflict you saw.'
        return Join-Prompt @($McpPreamble, $shared, $Task.Brief, $components, $rest)
    }
    $parts = 'The component part files written by the component agents are: ' + (Format-List $ExistingParts) + '. Part files that were not written: ' + (Format-List $MissingParts) + '.'
    $steps = 'Build ONE spec in this folder with all the `blocks` from the existing part files, plus everything the rest of the task needs. Check how `oml apply` takes its spec files: run `oml apply` with no arguments and read the usage. If it accepts several spec files, pass all the part files and your rest-of-task spec in one call. Otherwise merge all their `blocks` arrays and the rest-of-task content into one spec file, and apply that. Apply once with `oml apply ' + $Base + ' <spec> result.oml`. Then run `oml validate result.oml --warnings`. Done when result.oml has zero validation errors. Report every failed command and why, and anything you could not build.'
    Join-Prompt @((Get-OmlPreamble $Base), ('Work in this folder: ' + $Dir), $Task.Brief, $components, $parts, $rest, $steps)
}

# ---------- agent folders and claude commands ----------

function Get-ClaudeArgs([string]$SideName) {
    $claudeArgs = @('-p', '--model', $Model, '--output-format', 'json', '--mcp-config', 'mcp.json', '--strict-mcp-config', '--setting-sources', 'project', '--permission-mode', 'bypassPermissions')
    # No helper agents on either side: they can run other models and outlive a headless run (seen in a benchmark).
    $denied = @('Agent', 'Task')
    if ($SideName -eq 'mcp') { $denied += @('Bash', 'PowerShell') }
    $claudeArgs += @('--disallowedTools') + $denied
    , $claudeArgs
}

function Format-ClaudeCommand([string[]]$ClaudeArgs) {
    'claude ' + (($ClaudeArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
}

function New-AgentFolder([string]$Dir, [string]$SideName, [string]$Prompt) {
    New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    Write-Utf8 (Join-Path $Dir 'prompt.md') $Prompt
    $servers = if ($SideName -eq 'mcp') { @{ servicestudio = @{ type = 'http'; url = $script:McpUrl } } } else { @{} }
    Write-Utf8 (Join-Path $Dir 'mcp.json') (@{ mcpServers = $servers } | ConvertTo-Json -Depth 5 -Compress)
    $skills = Join-Path $Dir '.claude\skills'
    New-Item -ItemType Directory -Path $skills -Force | Out-Null
    if ($SideName -eq 'mcp') {
        if ($script:McpSkillSource) { Copy-Item -LiteralPath $script:McpSkillSource -Destination (Join-Path $skills $McpSkillName) -Recurse }
    }
    else {
        Copy-Item -LiteralPath (Join-Path $RepoRoot 'skill') -Destination (Join-Path $skills $OmlSkillName) -Recurse
    }
}

# ---------- running agents ----------

function Read-AgentResult($Agent, $Run) {
    $problem = $null
    $r = $null
    $stdout = Join-Path $Agent.Dir 'result.json'
    if (-not $Run) { $problem = 'the job produced no result' }
    elseif (-not (Test-Path -LiteralPath $stdout)) { $problem = "no output file (exit $($Run.Exit))" }
    else {
        try { $r = Get-Content -LiteralPath $stdout -Raw -Encoding utf8 | ConvertFrom-Json }
        catch { $problem = "output is not JSON (exit $($Run.Exit)); see stderr.txt in the agent folder" }
    }
    $u = if ($r) { $r.usage } else { $null }
    [pscustomobject]@{
        name              = $Agent.Name
        startedUtc        = if ($Run) { $Run.Started } else { $null }
        endedUtc          = if ($Run) { $Run.Ended } else { $null }
        exitCode          = if ($Run) { $Run.Exit } else { $null }
        isError           = [bool]($problem -or ($r -and $r.is_error))
        subtype           = if ($r) { $r.subtype } else { $null }
        costUsd           = if ($r) { [double]$r.total_cost_usd } else { 0.0 }
        durationMs        = if ($r) { [int64]$r.duration_ms } else { 0 }
        apiDurationMs     = if ($r) { [int64]$r.duration_api_ms } else { 0 }
        numTurns          = if ($r) { [int]$r.num_turns } else { 0 }
        inputTokens       = [int64]$u.input_tokens
        cacheReadTokens   = [int64]$u.cache_read_input_tokens
        cacheWriteTokens  = [int64]$u.cache_creation_input_tokens
        outputTokens      = [int64]$u.output_tokens
        permissionDenials = if ($r -and $r.permission_denials) { @($r.permission_denials).Count } else { 0 }
        sessionId         = if ($r) { $r.session_id } else { $null }
        finalText         = Hide-Secrets ([string]$(if ($r) { $r.result }))
        problem           = $problem
    }
}

function Invoke-Phase([string]$Name, $Agents, [string[]]$ClaudeArgs) {
    if ($DryRun) {
        Write-Host "  [dry run] phase ${Name}: $(@($Agents).Count) agent(s), not started"
        foreach ($a in @($Agents)) {
            Write-Host ('    cd "' + $a.Dir + '"')
            Write-Host ('    Get-Content prompt.md | ' + (Format-ClaudeCommand $ClaudeArgs))
        }
        return [pscustomobject]@{ name = $Name; wallSeconds = 0; agents = @() }
    }
    Write-Host "  phase ${Name}: starting $(@($Agents).Count) agent(s) in parallel"
    $jobs = foreach ($a in @($Agents)) {
        $job = Start-Job -Name $a.Name -ArgumentList $a.Dir, (Join-Path $a.Dir 'result.json'), (Join-Path $a.Dir 'stderr.txt'), $ClaudeArgs -ScriptBlock {
            param($Dir, $StdOut, $StdErr, $ClaudeCmdArgs)
            $started = (Get-Date).ToUniversalTime()
            Set-Location -LiteralPath $Dir
            Get-Content -LiteralPath (Join-Path $Dir 'prompt.md') -Raw -Encoding utf8 | & claude @ClaudeCmdArgs > $StdOut 2> $StdErr
            [pscustomobject]@{ Exit = $LASTEXITCODE; Started = $started.ToString('o'); Ended = (Get-Date).ToUniversalTime().ToString('o') }
        }
        $script:OwnJobs += $job
        $job
    }
    Wait-Job -Job @($jobs) | Out-Null
    $agentRecords = @()
    for ($i = 0; $i -lt @($Agents).Count; $i++) {
        $agent = @($Agents)[$i]
        $run = Receive-Job -Job $jobs[$i] -ErrorAction SilentlyContinue | Select-Object -Last 1
        Remove-Job -Job $jobs[$i] -Force -ErrorAction SilentlyContinue
        $rec = Read-AgentResult $agent $run
        $agentRecords += $rec
        Write-Host ('    {0}: exit {1}, ${2:N2}, {3} s, {4} turns, error: {5}' -f $rec.name, $rec.exitCode, $rec.costUsd, [int]($rec.durationMs / 1000), $rec.numTurns, $(if ($rec.isError) { 'yes' } else { 'no' }))
    }
    $starts = @($agentRecords | Where-Object { $_.startedUtc } | ForEach-Object { [datetime]$_.startedUtc } | Sort-Object)
    $ends = @($agentRecords | Where-Object { $_.endedUtc } | ForEach-Object { [datetime]$_.endedUtc } | Sort-Object)
    $wall = if ($starts.Count -and $ends.Count) { [Math]::Round(($ends[-1] - $starts[0]).TotalSeconds, 1) } else { 0 }
    [pscustomobject]@{ name = $Name; wallSeconds = $wall; agents = $agentRecords }
}

function Get-Totals($Agents, [double]$WallSeconds) {
    function Sum-Field([string]$Field) {
        $total = 0.0
        foreach ($a in @($Agents)) { $total += [double]$a.$Field }
        $total
    }
    $uncached = Sum-Field 'inputTokens'
    $cacheRead = Sum-Field 'cacheReadTokens'
    $cacheWrite = Sum-Field 'cacheWriteTokens'
    [pscustomobject]@{
        costUsd           = Sum-Field 'costUsd'
        agentDurationMs   = [int64](Sum-Field 'durationMs')
        wallSeconds       = $WallSeconds
        turns             = [int](Sum-Field 'numTurns')
        inputTokens       = [int64]$uncached
        cacheReadTokens   = [int64]$cacheRead
        cacheWriteTokens  = [int64]$cacheWrite
        totalInputTokens  = [int64]($uncached + $cacheRead + $cacheWrite)
        outputTokens      = [int64](Sum-Field 'outputTokens')
        agentErrors       = @($Agents | Where-Object { $_.isError }).Count
        permissionDenials = [int](Sum-Field 'permissionDenials')
    }
}

function Invoke-Verify([string]$ModulePath, [string[]]$Expect, [string]$OutDir) {
    $jsonPath = Join-Path $OutDir 'verify.json'
    $verifyArgs = @{ Oml = $script:Cli; Module = $ModulePath; OutFile = $jsonPath }
    if ($Expect.Count) { $verifyArgs.Expect = $Expect }
    & $VerifyScript @verifyArgs *>&1 | Tee-Object -FilePath (Join-Path $OutDir 'verify.txt') | Out-Host
    if (Test-Path -LiteralPath $jsonPath) { return Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json }
    [pscustomobject]@{ ok = $false; note = 'verify.ps1 did not finish' }
}

function Invoke-Side([string]$SideName, $Task, [string]$SideRoot) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Write-Host ''
    Write-Host "== side: $SideName, folder: $SideRoot"
    New-Item -ItemType Directory -Path $SideRoot -Force | Out-Null
    $base = Join-Path $SideRoot 'base.oml'
    if ($SideName -eq 'oml') { Copy-Item -LiteralPath $script:Snapshot -Destination $base }
    $claudeArgs = Get-ClaudeArgs $SideName
    $phases = @()
    if ($Mode -eq 'single') {
        New-AgentFolder -Dir $SideRoot -SideName $SideName -Prompt (New-SinglePrompt $Task $SideName $SideRoot $base)
        $phases += Invoke-Phase 'single' @([pscustomobject]@{ Name = 'single'; Dir = $SideRoot }) $claudeArgs
        $resultFile = Join-Path $SideRoot 'result.oml'
    }
    else {
        $parts = Join-Path $SideRoot 'parts'
        if ($SideName -eq 'oml') { New-Item -ItemType Directory -Path $parts | Out-Null }
        $chunks = Get-ComponentChunks $Task.Components $Agents
        $list = @()
        for ($i = 0; $i -lt $chunks.Count; $i++) {
            $n = $i + 1
            $dir = Join-Path $SideRoot "a$n"
            $partFile = Join-Path $parts "a$n.json"
            New-AgentFolder -Dir $dir -SideName $SideName -Prompt (New-PartPrompt $Task $SideName $n $chunks.Count $chunks[$i] $dir $base $partFile)
            $list += [pscustomobject]@{ Name = "a$n"; Dir = $dir }
        }
        $phases += Invoke-Phase 'components' $list $claudeArgs

        $existing = @()
        $missing = @()
        if ($SideName -eq 'oml') {
            $existing = @(Get-ChildItem -LiteralPath $parts -Filter 'a*.json' -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { $_.FullName })
            $missing = @(for ($i = 1; $i -le $list.Count; $i++) { $p = Join-Path $parts "a$i.json"; if (-not (Test-Path -LiteralPath $p)) { "a$i.json" } })
            if ($missing.Count -and -not $DryRun) { Write-Host "  warning: part file(s) not written: $($missing -join ', ')" }
        }
        $asmDir = Join-Path $SideRoot 'asm'
        New-AgentFolder -Dir $asmDir -SideName $SideName -Prompt (New-AssemblyPrompt $Task $SideName $asmDir $base $existing $missing)
        $phases += Invoke-Phase 'assembly' @([pscustomobject]@{ Name = 'asm'; Dir = $asmDir }) $claudeArgs
        $resultFile = Join-Path $asmDir 'result.oml'
    }

    $agents = @($phases | ForEach-Object { $_.agents })
    $sideSeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 1)
    $verify = $null
    if ($SideName -eq 'oml' -and -not $DryRun) {
        if (Test-Path -LiteralPath $resultFile) { $verify = Invoke-Verify $resultFile $Task.Expect $SideRoot }
        else { $verify = [pscustomobject]@{ ok = $false; note = 'no result.oml was written' } }
    }
    $skill = if ($SideName -eq 'mcp') {
        if ($script:McpSkillSource) { [pscustomobject]@{ source = $script:McpSkillSource; commit = $script:SkillCommit; skillMdSha256 = Get-Sha256 (Join-Path $script:McpSkillSource 'SKILL.md') } }
        else { [pscustomobject]@{ source = "$SkillRepo (not cloned: dry run)"; commit = $null; skillMdSha256 = $null } }
    }
    else { [pscustomobject]@{ source = (Join-Path $RepoRoot 'skill'); commit = $null; skillMdSha256 = Get-Sha256 (Join-Path $RepoRoot 'skill\SKILL.md') } }

    [pscustomobject]@{
        side        = $SideName
        folder      = $SideRoot
        phases      = $phases
        totals      = Get-Totals $agents $sideSeconds
        verify      = $verify
        skill       = $skill
    }
}

# ---------- environment checks ----------

function Find-ProjectAncestor([string]$Path) {
    $dir = $Path
    while ($dir) {
        if ($dir -ne $HOME) {
            foreach ($marker in '.git', '.claude', 'CLAUDE.md', 'AGENTS.md') {
                if (Test-Path -LiteralPath (Join-Path $dir $marker)) { return $dir }
            }
        }
        $parent = Split-Path -Parent $dir
        if (-not $parent -or $parent -eq $dir) { break }
        $dir = $parent
    }
    $null
}

function Get-SkillClone {
    $download = Join-Path $script:OutFull 'skill-download'
    Write-Host "Cloning $SkillRepo into $download (read only; nothing from it is run)."
    if ($SkillRef) { & git clone --quiet $SkillRepo $download } else { & git clone --quiet --depth 1 $SkillRepo $download }
    if ($LASTEXITCODE -ne 0) { throw "git clone of $SkillRepo failed." }
    if ($SkillRef) {
        & git -C $download checkout --quiet $SkillRef
        if ($LASTEXITCODE -ne 0) { throw "Could not check out $SkillRef in $SkillRepo." }
    }
    $found = Get-ChildItem -LiteralPath $download -Recurse -Directory -Filter $McpSkillName |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'SKILL.md') -PathType Leaf } | Select-Object -First 1
    if (-not $found) { throw "No $McpSkillName folder with a SKILL.md in $SkillRepo." }
    $script:SkillCommit = (& git -C $download rev-parse HEAD).Trim()
    Write-Host "Skill commit: $($script:SkillCommit)"
    $found.FullName
}

function Get-Versions {
    $v = [ordered]@{}
    $v.claudeCode = ((& claude --version 2>$null) | Out-String).Trim()
    if ($script:Cli) { $v.oml = ((& $script:Cli version 2>$null) | Out-String).Trim() }
    $v.repoCommit = ((& git -C $RepoRoot rev-parse --short HEAD 2>$null) | Out-String).Trim()
    $v.repoDirtyFiles = @(& git -C $RepoRoot status --porcelain 2>$null).Count
    $v
}

# ---------- results ----------

function Format-Money($Value) { '$' + ([double]$Value).ToString('N2', $Inv) }
function Format-Count($Value) { ([int64]$Value).ToString('N0', $Inv) }
function Get-SideLabel([string]$Name) { if ($Name -eq 'mcp') { 'OutSystems MCP + O11 skill' } else { 'oml' } }

function Get-VerifyText($Side) {
    $v = $Side.verify
    if (-not $v) { if ($Side.side -eq 'mcp') { 'not checked by bench: check the module in Service Studio' } else { 'dry run' } }
    elseif ($v.note) { "not checked: $($v.note)" }
    else {
        $checks = @($v.checks | ForEach-Object { "$($_.pattern) $($_.found)/$($_.expected)" })
        $text = "verify $(if ($v.ok) { 'OK' } else { 'FAILED' }): $($v.errorCount) errors, $($v.warningCount) warnings"
        if ($checks.Count) { $text += '; ' + ($checks -join ', ') }
        $text
    }
}

function Format-Markdown($Doc) {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("# Benchmark results: $($Doc.task.name)")
    $lines.Add('')
    $lines.Add("- Task file: $($Doc.task.file) (sha256 $($Doc.task.sha256))")
    $lines.Add("- Model: $($Doc.model)")
    $modeText = if ($Doc.mode -eq 'swarm') { "swarm, $($Doc.agents) agents + 1 assembly" } else { 'single agent' }
    $lines.Add("- Mode: $modeText")
    $lines.Add("- Started (UTC): $($Doc.startedUtc)")
    $lines.Add("- Claude Code: $($Doc.versions.claudeCode); oml: $($Doc.versions.oml); repo commit: $($Doc.versions.repoCommit) (uncommitted files: $($Doc.versions.repoDirtyFiles))")
    $lines.Add('')
    $lines.Add('## Summary')
    $lines.Add('')
    $lines.Add('| Side | Cost | Time | Turns | Input tokens (incl. cache) | Output tokens | Agent errors | Result |')
    $lines.Add('|---|---:|---:|---:|---:|---:|---:|---|')
    foreach ($s in $Doc.sides) {
        $t = $s.totals
        $lines.Add("| $(Get-SideLabel $s.side) | $(Format-Money $t.costUsd) | $([int]$t.wallSeconds) s | $($t.turns) | $(Format-Count $t.totalInputTokens) | $(Format-Count $t.outputTokens) | $($t.agentErrors) | $(Get-VerifyText $s) |")
    }
    $lines.Add('')
    $lines.Add('## Phases')
    $lines.Add('')
    $lines.Add('| Side | Phase | Wall time | Agents | Agent errors |')
    $lines.Add('|---|---|---:|---:|---:|')
    foreach ($s in $Doc.sides) {
        foreach ($p in $s.phases) {
            $lines.Add("| $(Get-SideLabel $s.side) | $($p.name) | $([int]$p.wallSeconds) s | $(@($p.agents).Count) | $(@($p.agents | Where-Object { $_.isError }).Count) |")
        }
    }
    $lines.Add('')
    $lines.Add('## Agents')
    $lines.Add('')
    $lines.Add('| Side | Agent | Exit | Cost | Duration | Turns | Input | Cache read | Cache write | Output | Denied calls | Problem |')
    $lines.Add('|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|')
    foreach ($s in $Doc.sides) {
        foreach ($p in $s.phases) {
            foreach ($a in $p.agents) {
                $problem = if ($a.problem) { ([string]$a.problem) -replace '\|', '/' } else { '' }
                $lines.Add("| $(Get-SideLabel $s.side) | $($a.name) | $($a.exitCode) | $(Format-Money $a.costUsd) | $([int]($a.durationMs / 1000)) s | $($a.numTurns) | $(Format-Count $a.inputTokens) | $(Format-Count $a.cacheReadTokens) | $(Format-Count $a.cacheWriteTokens) | $(Format-Count $a.outputTokens) | $($a.permissionDenials) | $problem |")
            }
        }
    }
    $lines.Add('')
    $lines.Add('## Skills')
    $lines.Add('')
    foreach ($s in $Doc.sides) {
        $commit = if ($s.skill.commit) { " at commit $($s.skill.commit)" } else { '' }
        $lines.Add("- $(Get-SideLabel $s.side): $($s.skill.source)$commit; SKILL.md sha256 $($s.skill.skillMdSha256)")
    }
    $lines.Add('')
    $lines.Add('## Notes')
    $lines.Add('')
    $lines.Add('- Input tokens include cache reads and cache writes. Cost is the figure Claude Code reports.')
    $lines.Add('- Failed tool calls are not counted by this script. Read the final text of each agent and its run folder.')
    $lines.Add('- Agents in one phase run in parallel, so the phase time is the slowest agent.')
    $lines.Add('- Each run folder keeps the full agent output (result.json). Check it before you share the folder.')
    ($lines -join "`n") + "`n"
}

function Save-Results($Sides) {
    $doc = [ordered]@{
        tool       = 'tests/bench/bench.ps1'
        task       = [ordered]@{ name = $script:TaskInfo.Name; file = $script:TaskInfo.Path; sha256 = $script:TaskInfo.Sha256 }
        mode       = $Mode
        agents     = if ($Mode -eq 'swarm') { [Math]::Min($Agents, $script:TaskInfo.Components.Count) } else { 1 }
        model      = $Model
        startedUtc = $script:RunStarted
        versions   = $script:Versions
        sides      = @($Sides)
    }
    Write-Utf8 (Join-Path $script:OutFull 'results.json') ($doc | ConvertTo-Json -Depth 12)
    Write-Utf8 (Join-Path $script:OutFull 'results.md') (Format-Markdown $doc)
}

# ---------- main ----------

$script:OwnJobs = @()
$script:Cli = $null
$script:McpUrl = $null
$script:McpSkillSource = $null
$script:SkillCommit = $null
$oldTimeout = $env:MCP_TOOL_TIMEOUT

try {
    $sides = if ($Side -eq 'both') { @('mcp', 'oml') } else { @($Side) }
    $script:TaskInfo = Read-TaskFile $Task
    if ($Mode -eq 'swarm' -and $script:TaskInfo.Components.Count -eq 0) {
        throw "Swarm mode needs a numbered component list in $($script:TaskInfo.Path). Use -Mode single for this task."
    }

    $script:ModulePath = $null
    if ($sides -contains 'oml') {
        if (-not $Module) { throw '-Module is required for the oml side: the .oml file both sides start from.' }
        $script:ModulePath = (Resolve-Path -LiteralPath $Module -ErrorAction Stop).Path
        if ([IO.Path]::GetExtension($script:ModulePath) -ne '.oml') { throw "-Module must be an .oml file: $script:ModulePath" }
        if (-not $OmlExe) {
            $found = Get-Command oml -CommandType Application -ErrorAction SilentlyContinue
            if (-not $found) { throw 'oml was not found on PATH. Pass -OmlExe <path to oml.exe>.' }
            $OmlExe = $found.Source
        }
        $script:Cli = (Resolve-Path -LiteralPath $OmlExe -ErrorAction Stop).Path
    }
    elseif ($Module) { Write-Warning '-Module is used by the oml side only; the mcp side works on the module open in Service Studio.' }

    if ($sides -contains 'mcp') {
        $port = $DefaultStudioPort
        if ($env:OML_STUDIO_PORT) {
            if ($env:OML_STUDIO_PORT -notmatch '^\d+$' -or [int]$env:OML_STUDIO_PORT -lt 1 -or [int]$env:OML_STUDIO_PORT -gt 65535) {
                throw 'OML_STUDIO_PORT must be a port number from 1 to 65535.'
            }
            $port = [int]$env:OML_STUDIO_PORT
        }
        $script:McpUrl = "http://127.0.0.1:$port/mcp"
    }

    $hasClaude = [bool](Get-Command claude -ErrorAction SilentlyContinue)
    if (-not $hasClaude -and -not $DryRun) { throw 'claude was not found on PATH. Install Claude Code and sign in (claude auth status).' }

    $needClone = $false
    if ($sides -contains 'mcp') {
        if ($SkillPath) {
            $skillDir = (Resolve-Path -LiteralPath $SkillPath -ErrorAction Stop).Path
            if (-not (Test-Path -LiteralPath (Join-Path $skillDir 'SKILL.md') -PathType Leaf)) { throw "No SKILL.md in -SkillPath: $skillDir" }
            $script:McpSkillSource = $skillDir
        }
        else {
            $needClone = $true
            if (-not $AllowDownload -and -not $DryRun) {
                throw "The mcp side needs the $McpSkillName skill folder from $SkillRepo. Re-run with -AllowDownload to clone that repository, or pass -SkillPath to a local copy of the skill folder."
            }
        }
    }

    $script:OutFull = [IO.Path]::GetFullPath($Out)
    if (Test-Path -LiteralPath $script:OutFull) { throw "The -Out folder already exists: $($script:OutFull). Choose a new folder; results are never overwritten." }
    $projectFolder = Find-ProjectAncestor (Split-Path -Parent $script:OutFull)
    if ($projectFolder) {
        $message = "-Out is inside a project folder ($projectFolder). Claude Code would read its CLAUDE.md, skills and settings into the run. Use a folder outside any project."
        if ($DryRun) { Write-Warning $message } else { throw $message }
    }

    Write-Host "Benchmark plan"
    Write-Host "  task:    $($script:TaskInfo.Path)  (sha256 $($script:TaskInfo.Sha256))"
    Write-Host "  sides:   $($sides -join ', ')   mode: $Mode   model: $Model"
    if ($Mode -eq 'swarm') { Write-Host "  agents:  $([Math]::Min($Agents, $script:TaskInfo.Components.Count)) component agents (in parallel) + 1 assembly agent; $($script:TaskInfo.Components.Count) components" }
    if ($script:ModulePath) { Write-Host "  module:  $($script:ModulePath) (copied to source.oml; the original is not changed)" }
    if ($sides -contains 'mcp') {
        Write-Host "  mcp:     $($script:McpUrl)"
        if ($script:McpSkillSource) { Write-Host "  skill:   $script:McpSkillSource" }
        else { Write-Host "  skill:   $McpSkillName folder from $SkillRepo$(if ($SkillRef) { " at $SkillRef" }) (cloned only with -AllowDownload)" }
    }
    Write-Host "  out:     $($script:OutFull)"

    if (-not $DryRun -and -not $Yes) {
        Write-Host ''
        Write-Host 'A real run starts Claude API calls. Each agent costs money, and a swarm runs several at once.'
        if ((Read-Host "Type 'yes' to start") -ne 'yes') { throw 'Cancelled. Nothing was run.' }
    }

    New-Item -ItemType Directory -Path $script:OutFull | Out-Null
    $script:Snapshot = Join-Path $script:OutFull 'source.oml'
    if ($script:ModulePath) {
        Copy-Item -LiteralPath $script:ModulePath -Destination $script:Snapshot
        Write-Host "source.oml sha256: $(Get-Sha256 $script:Snapshot)"
    }
    if ($needClone -and -not $DryRun) { $script:McpSkillSource = Get-SkillClone }
    if (-not $DryRun -and -not $oldTimeout) { $env:MCP_TOOL_TIMEOUT = '300000' }

    $script:RunStarted = (Get-Date).ToUniversalTime().ToString('o')
    $script:Versions = if ($DryRun) { $null } else { Get-Versions }

    $results = @()
    foreach ($s in $sides) {
        $results += Invoke-Side $s $script:TaskInfo (Join-Path $script:OutFull $s)
        if (-not $DryRun) { Save-Results $results }
    }

    if ($DryRun) {
        Write-Host ''
        Write-Host "Dry run complete. Nothing was run and nothing was downloaded. Folders and prompts are in $($script:OutFull)."
    }
    else {
        Write-Host ''
        $results | ForEach-Object {
            $t = $_.totals
            [pscustomobject]@{
                Side        = (Get-SideLabel $_.side)
                Cost        = (Format-Money $t.costUsd)
                Time        = "$([int]$t.wallSeconds) s"
                Turns       = $t.turns
                InputTokens = (Format-Count $t.totalInputTokens)
                OutputTokens = (Format-Count $t.outputTokens)
                AgentErrors = $t.agentErrors
                Result      = (Get-VerifyText $_)
            }
        } | Format-Table -Wrap | Out-String | Write-Host
        Write-Host "Results: $(Join-Path $script:OutFull 'results.md') and results.json"
    }
}
finally {
    foreach ($job in $script:OwnJobs) {
        if ($job.State -eq 'Running') { Stop-Job -Job $job -ErrorAction SilentlyContinue }
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    }
    $env:MCP_TOOL_TIMEOUT = $oldTimeout
}
