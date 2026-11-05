# Checks that no command crashes on a bad file or folder argument: a missing file, a file in a missing folder, an empty
# file, a file with the wrong extension and a path with spaces. A command passes when it exits 1 or 2 and prints no .NET
# crash (no "Unhandled exception", no stack frame lines, no "System." exception type). Each run has 60 s, so a hang fails.
# The commands come from the `oml ` lines of `oml help --all`; a command that is neither tested here nor in $skip fails the
# check, so a new command cannot slip by without a decision. Needs PowerShell 7.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($condition, $message) { if (-not $condition) { throw $message } }

# Not run, and why: these start a server, open a window, or reach the network, Service Studio's MCP server or LifeTime.
$skip = [ordered]@{
  'create'         = 'makes a project folder in the current folder; tests/create-check.ps1 covers its bad inputs'
  'dashboard'      = 'starts a web server'
  'show-change'    = 'starts a web server'
  'explorer-menu'  = 'writes Explorer registry entries'
  'lifetime'       = 'reaches the LifeTime server'
  'publish'        = 'publishes through Service Studio'
  'references'     = 'refreshes references through Service Studio'
  'pull'           = 'reads a module from Service Studio'
  'studio'         = 'reaches Service Studio''s MCP server'
  'qa'             = 'qa start/record/run/page start a browser and reach a running app; qa stop only closes one'
  'outsystems-docs' = 'outsystems-docs fetch/update download from GitHub (outsystems-docs status takes no path)'
  'hooks'          = 'hooks install/remove change the project''s Claude settings'
  'version'        = 'takes no path'
  'templates'      = 'takes no path'
  'expression'     = 'takes no path'
  'model-types'    = 'takes no path'
  'widget-types'   = 'takes no path'
  'selftest'       = 'takes no path and runs for minutes'
  'doctor'         = 'takes no path'
  'explain-warning' = 'takes no path'
  # map build is tested below; the map queries (overview, uses, impact, data, api, list) take names, not paths.
}
# A template that starts with ! writes to {p}: there a missing file or a wrong extension may simply succeed (exit 0), and
# only a missing folder must fail.
# A template that starts with ~ reads {p} as plain text (or only uses its folder): an empty file, a file of any extension
# and a path with spaces are valid input there, so exit 0 is right; a missing file or folder must still fail.
# command key (first word, or first two for these) -> argument lists. {p} is the path under test, with the extension
# named in $ext for the key; {o} is an output path in an existing folder. One list per argument shape worth trying.
$two = 'app', 'extension', 'test', 'translations', 'font', 'team-rules', 'memory', 'spec', 'learn'
$shapes = [ordered]@{
  'new'                    = @('!new {p}', 'new {p} --template {p}', 'new {o} --from-template {p}')
  'app list'               = @('app list {p}')
  'app extract'            = @('app extract {p} {o}')
  'app check'              = @('app check {p} --producers {p}')
  'app pack'               = @('app pack {p} --name X {p}')
  'extension inspect'      = @('extension inspect {p}')
  'extension new'          = @('!extension new {p} --name X', 'extension new {o} --name X --spec {p}')
  'inspect'                = @('inspect {p} --own', 'inspect --in {p}')
  'describe'               = @('describe {p} Foo', 'describe {o} Foo --in {p}')
  'find'                   = @('find Foo {p}', 'find {p} --text hello', 'find Foo --in {p}')
  'stats'                  = @('stats {p}')
  'header'                 = @('header {p}', 'header --diff {p} {p}')
  'architecture'           = @('architecture --in {p}', 'architecture --in {p} --out {p}', 'architecture --in {p} --publish-order')
  'check'                  = @('check {p}', 'check --in {p}', 'check --diff {p} {p}', '!check --init-rules --out {p}')
  'review'                 = @('review {p} {p}')
  'openapi'                = @('openapi {p}')
  'preview'                = @('preview {p} Foo')
  'test bdd'               = @('test bdd {p} Foo --bdd {p}')
  'test scenarios'         = @('test scenarios {p} {p}', 'test scenarios {p} {o}')
  'test data'              = @('test data module {p} --entity A:B:C', 'test data create Contact --file {p}')
  'flow'                   = @('flow {p} Foo')
  'tree'                   = @('tree {p} Foo')
  'validate'               = @('validate {p}')
  'apply'                  = @('apply {p} {p} {o}', 'apply --batch {p}')
  'spec'                   = @('spec check {p}', 'spec check {p} --module {p}', '!spec schema --out {p}')
  'doc'                    = @('doc --search hello --in {p}')
  'team-rules'             = @('team-rules add {p}', 'team-rules remove {p}')
  'memory'                 = @('~memory init --module {p}', 'memory init --dir {p}', 'memory doctor --module {p}', 'memory doctor --file {p}')
  'batch'                  = @('batch {p}')
  'translations export'    = @('translations export {p} {o}')
  'install'                = @('install {p} {p}', 'install --batch {p}')
  'font woff2'             = @('font woff2 {p}')
  'font batch'             = @('font batch {p}')
  'image'                  = @('image {p}', 'image batch {p}', 'image {p} --to svg')
  'log'                    = @('~log summary --file {p}')
  'learn'                  = @('~learn report --file {p}', 'learn report --check {p}', '~learn propose --file {p}')
  'map'                    = @('map build badinput --in {p}')
  'mock'                   = @('mock --all --personas {p}')
}
# The extension a path argument should have, by the first word of the shape (default .oml).
$ext = @{ 'app' = '.oap'; 'extension' = '.xif'; 'spec' = '.json'; 'batch' = '.json'; 'apply' = '.json'; 'log' = '.jsonl'; 'learn' = '.jsonl'; 'mock' = '.json';
          'font' = '.ttf'; 'image' = '.png'; 'team-rules' = '.md'; 'translations' = '.oml'; 'architecture' = '.oml' }

# 1. Every command of `oml help --all` is tested or skipped on purpose.
$helpLines = @(& $Oml help --all | Where-Object { $_ -match '^oml ' })
$commands = $helpLines | ForEach-Object {
  $w = ($_ -replace '^oml ', '') -split '[\s|]+'
  if ($two -contains $w[0] -and $w.Count -gt 1 -and $w[1] -match '^[a-z0-9-]+$') { "$($w[0]) $($w[1])" } else { $w[0] }
} | Sort-Object -Unique
$unknown = @($commands | Where-Object { $first = ($_ -split ' ')[0]; -not $skip.Contains($first) -and -not $shapes.Contains($_) -and -not $shapes.Contains($first) })
Require ($unknown.Count -eq 0) "badinput: commands in oml help --all that this check neither tests nor skips: $($unknown -join ', ')"
Write-Host "ok   badinput: $($commands.Count) help commands accounted for ($($skip.Count) skipped by design)"

$dir = Join-Path $Tmp ('badinput-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $dir | Out-Null
$env:OML_MAPS = Join-Path $dir 'maps' # map build must never write the person's maps

# Runs oml with a time limit; returns the exit code and all text.
function Invoke-Oml([string[]]$arguments, [string]$work) {
  $info = [Diagnostics.ProcessStartInfo]::new($Oml)
  foreach ($a in $arguments) { $info.ArgumentList.Add($a) }
  $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true; $info.RedirectStandardInput = $true
  $info.UseShellExecute = $false; $info.CreateNoWindow = $true
  $info.WorkingDirectory = $work
  $process = [Diagnostics.Process]::Start($info)
  $process.StandardInput.Close()
  $out = $process.StandardOutput.ReadToEndAsync(); $err = $process.StandardError.ReadToEndAsync()
  if (-not $process.WaitForExit(60000)) {
    try { $process.Kill($true) } catch { }   # this process only: the one started just above
    return @{ Code = -999; Text = 'TIMEOUT after 60 s' }
  }
  $process.WaitForExit()
  return @{ Code = $process.ExitCode; Text = $out.Result + "`n" + $err.Result }
}

try {
  $bad = New-Object System.Collections.Generic.List[string]
  $runs = 0
  foreach ($shape in $shapes.Keys) {
    foreach ($template in $shapes[$shape]) {
      $first = ($template -split ' ')[0]
      $e = if ($ext.ContainsKey($first)) { $ext[$first] } else { '.oml' }
      $output = $template.StartsWith('!')
      if ($output) { $template = $template.Substring(1) }
      $text = $template.StartsWith('~')
      if ($text) { $template = $template.Substring(1) }
      foreach ($case in 'missing file', 'file in missing folder', 'empty file', 'wrong extension', 'path with spaces') {
        # A fresh folder per run, so nothing one command writes (a store, an output) changes what the next one sees.
        $work = Join-Path $dir ('r' + $runs)
        New-Item -ItemType Directory -Force (Join-Path $work 'with space'), (Join-Path $work 'out') | Out-Null
        $path = switch ($case) {
          'missing file'           { Join-Path $work "nope$e" }
          'file in missing folder' { Join-Path $work "nofolder\x$e" }
          'empty file'             { $f = Join-Path $work "empty$e"; [IO.File]::WriteAllBytes($f, [byte[]]@()); $f }
          'wrong extension'        { $f = Join-Path $work 'wrong.txt'; [IO.File]::WriteAllText($f, 'this is not a module'); $f }
          'path with spaces'       { $f = Join-Path $work "with space\a b$e"; [IO.File]::WriteAllText($f, 'this is not a module'); $f }
        }
        $arguments = @($template -split ' ' | ForEach-Object { $_ -replace '\{p\}', $path -replace '\{o\}', (Join-Path $work "out\o$e") })
        $r = Invoke-Oml $arguments $work
        $runs++
        $problem = $null
        $mayWrite = ($output -and $case -ne 'file in missing folder') -or ($text -and $case -notin 'missing file', 'file in missing folder')
        # A command over several inputs may report the bad one inside its JSON result and exit 0 (inspect --in, find --in, architecture --in).
        if ($r.Code -eq 0 -and $r.Text -match '"(problems|warnings|error)"\s*:\s*\[?\s*(\{|")') { $mayWrite = $true }
        if ($r.Code -ne 1 -and $r.Code -ne 2 -and -not ($mayWrite -and $r.Code -eq 0)) { $problem = "exit $($r.Code), not 1 or 2" }
        elseif ($r.Text -match 'Unhandled exception') { $problem = 'Unhandled exception' }
        elseif ($r.Text -match '(?m)^\s+at \S') { $problem = 'stack frame lines' }
        elseif ($r.Text -match 'System\.[A-Za-z.]*(Exception|Error)\b') { $problem = 'a System. exception type' }
        if ($problem) {
          $line = ($r.Text -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
          $bad.Add("oml $template [$case]: $problem ($line)")
        }
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
      }
    }
  }
  if ($bad.Count -gt 0) { $bad | ForEach-Object { Write-Host "     $_" } }
  Require ($bad.Count -eq 0) "badinput: $($bad.Count) of $runs runs crashed or hung"
  Write-Host "ok   badinput: $runs runs on bad paths, none crashed (exit 1 or 2, no stack trace)"
} finally {
  Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
}
