# oml on real applications: the fixtures of the other checks are small modules the suite builds itself, and real ones
# hold what those never do (Reactive screen actions, Forge components, extensions, years of history). This check runs the
# reading commands on copies of real .oap/.oml files and fails on any skipped module or crash.
# The files come from OML_CORPUS (folders or files separated by ;) or, without it, from corpus.txt in the repository root
# (one path per line; not committed, since the files are the user's). Without either it prints "skip" and passes, so a
# fresh clone still runs the suite; release.ps1 refuses to release without a corpus unless -NoCorpus is given.
# Originals are never opened for writing: everything runs on copies in -Tmp. Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$sources = if ($env:OML_CORPUS) { $env:OML_CORPUS -split ';' } else {
  $list = Join-Path (Split-Path $PSScriptRoot -Parent) 'corpus.txt'
  if (Test-Path $list) { Get-Content $list | ForEach-Object Trim | Where-Object { $_ -and -not $_.StartsWith('#') } } else { @() }
}
$sources = @($sources | Where-Object { $_ })
if ($sources.Count -eq 0) { Write-Host 'skip corpus: no OML_CORPUS and no corpus.txt (real applications are not checked)'; return }
$Tmp = New-TestFolder $Tmp 'corpus'
$saved = @{}; foreach ($n in 'OML_MAPS', 'OML_LOG') { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
try {
  function Fail($m) { throw "corpus: $m" }
  # A crash, not a refusal: a .NET exception name or a stack frame in the output.
  function Crashed([string]$text) { $text -match '(?m)^\s+at [\w\.`<>]+\(|Unhandled exception|\bSystem\.[\w\.]*Exception\b|NotImplementedException|NullReferenceException|InvalidCastException' }
  $env:OML_LOG = '0'; $env:OML_MAPS = Join-Path $Tmp 'maps'

  # 1. Copies of the inputs.
  $in = Join-Path $Tmp 'in'; New-Item -ItemType Directory $in | Out-Null
  $files = foreach ($s in $sources) {
    if (-not (Test-Path $s)) { Fail "corpus path not found: $s" }
    if (Test-Path $s -PathType Container) { Get-ChildItem $s -Recurse -File -Include *.oap, *.oml } else { Get-Item $s }
  }
  $files = @($files | Where-Object { $_.Extension -in '.oap', '.oml' } | Sort-Object FullName -Unique)
  if ($files.Count -eq 0) { Fail "no .oap or .oml file in: $($sources -join '; ')" }
  $n = 0
  foreach ($f in $files) { Copy-Item $f.FullName (Join-Path $in ('{0:D3}-{1}' -f $n++, $f.Name)) }

  # 2. The whole project: map, architecture, check. No module may be skipped.
  $work = Join-Path $Tmp 'project'; New-Item -ItemType Directory $work | Out-Null
  Push-Location $work
  try {
    $text = (& $Oml map build corpus --in $in 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or (Crashed $text)) { Fail "map build (exit $LASTEXITCODE): $text" }
    $b = $text | ConvertFrom-Json
    if (@($b.failed | Where-Object { $_ }).Count -gt 0) { Fail "map build skipped: $(($b.failed | ForEach-Object { "$($_.file): $($_.reason)" }) -join '; ')" }
    $skips = @($b.warnings | Where-Object { $_ -like 'Skipped *' -and $_ -notlike 'Skipped duplicate*' })
    if ($skips) { Fail "map build: $($skips -join '; ')" }
    $o = (& $Oml map overview) -join "`n" | ConvertFrom-Json
    $modules = @($o.applications | ForEach-Object { $_.modules } | ForEach-Object name)
    $text = (& $Oml architecture --in $in --json (Join-Path $Tmp 'architecture.json') --out (Join-Path $Tmp 'architecture.html') 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or (Crashed $text)) { Fail "architecture (exit $LASTEXITCODE): $text" }
    $text = (& $Oml check --in $in --json (Join-Path $Tmp 'check.json') --out (Join-Path $Tmp 'check.html') 2>&1) -join "`n"
    if ($LASTEXITCODE -notin 0, 1 -or (Crashed $text)) { Fail "check (exit $LASTEXITCODE): $text" }
    $text = (& $Oml mock --all --out (Join-Path $Tmp 'mocks') 2>&1) -join "`n"
    if ($LASTEXITCODE -notin 0, 1 -or (Crashed $text)) { Fail "mock --all (exit $LASTEXITCODE): $text" }
    # A map query for every module name, and uses for a few of its elements below.
    foreach ($m in $modules) {
      $text = (& $Oml map uses $m 2>&1) -join "`n"
      if ($LASTEXITCODE -notin 0, 1 -or (Crashed $text)) { Fail "map uses $m (exit $LASTEXITCODE): $text" }
    }
  } finally { Pop-Location }
  Write-Host "ok   corpus: $($files.Count) real file(s), $($modules.Count) modules: map build with none skipped, overview, uses, architecture, check, mock"

  # 3. Every module on its own: inspect, then flow and describe of up to 3 server actions, and test scenarios against itself.
  $omls = Join-Path $Tmp 'modules'; New-Item -ItemType Directory $omls | Out-Null
  foreach ($f in Get-ChildItem $in -File) {
    if ($f.Extension -eq '.oml') { Copy-Item $f.FullName $omls }
    else {
      $text = (& $Oml app extract $f.FullName (Join-Path $omls $f.BaseName) 2>&1) -join "`n"
      if ($LASTEXITCODE -ne 0 -or (Crashed $text)) { Fail "app extract $($f.Name) (exit $LASTEXITCODE): $text" }
    }
  }
  $each = @(Get-ChildItem $omls -Recurse -File -Filter *.oml)
  $problems = $each | ForEach-Object -ThrottleLimit 8 -Parallel {
    $Oml = $using:Oml; $m = $_.FullName; $name = $_.Name
    function Crashed([string]$text) { $text -match '(?m)^\s+at [\w\.`<>]+\(|Unhandled exception|\bSystem\.[\w\.]*Exception\b|NotImplementedException|NullReferenceException|InvalidCastException' }
    function Run-Oml([string]$what, [string[]]$a, [int[]]$ok = @(0)) {
      $text = (& $Oml @a 2>&1) -join "`n"
      if ($LASTEXITCODE -notin $ok -or (Crashed $text)) { "${name}: $what (exit $LASTEXITCODE): $($text.Substring(0, [math]::Min(400, $text.Length)))" }
      $script:last = $text
    }
    $script:last = ''
    Run-Oml 'inspect' @('inspect', $m, '--own')
    $actions = @(); try { $actions = @(($script:last | ConvertFrom-Json).serverActions | Select-Object -First 3 | ForEach-Object name) } catch { }
    foreach ($a in $actions) {
      Run-Oml "flow $a" @('flow', $m, $a)
      Run-Oml "describe $a" @('describe', $m, $a)
    }
    $out = Join-Path (Split-Path $m) ([IO.Path]::GetFileNameWithoutExtension($m) + '-scenarios')
    Run-Oml 'test scenarios' @('test', 'scenarios', $m, $m, '--out', $out) @(0, 1)
  }
  $problems = @($problems | Where-Object { $_ })
  if ($problems) { Fail ($problems -join "`n") }
  Write-Host "ok   corpus: $($each.Count) module(s) one by one: inspect, flow and describe of server actions, test scenarios"
} finally {
  foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
  Remove-TestWorkFolder $Tmp
}
