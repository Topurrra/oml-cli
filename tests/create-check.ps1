# oml create: a project folder with its layout, memory, the agent's skill, .gitignore and PROJECT.md; with --in,
# the modules copied in and the application map built under the project's name. Refusals: a folder with files, a bad
# name, a bad input. Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'create'
$saved = @{}; foreach ($n in 'OML_MAPS', 'OML_LOG') { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
try {
  function Require($condition, $message) { if (-not $condition) { throw "create: $message" } }
  function Json { param([Parameter(ValueFromRemainingArguments)]$a) $out = & $Oml @a; ($out -join "`n") | ConvertFrom-Json }
  $env:OML_LOG = '0'; $env:OML_MAPS = Join-Path $Tmp 'maps'

  # 1. Without modules: the layout, memory, skill; no hooks.
  $r = Json create alpha --dir $Tmp --no-library --no-doctor
  $root = Join-Path $Tmp 'alpha'
  Require ($LASTEXITCODE -eq 0 -and $r.folder -eq $root) "create: $($r | ConvertTo-Json -Compress)"
  foreach ($p in 'modules', 'staged', 'specs', 'browser', 'mocks', 'scenarios', '.oml\memory\memory.log', '.oml\map.json', '.oml\oml-config.json', 'skill\SKILL.md', 'AGENTS.md', 'CLAUDE.md', '.gitignore', 'PROJECT.md') {
    Require (Test-Path (Join-Path $root $p)) "missing $p"
  }
  Require ((Get-Content (Join-Path $root 'CLAUDE.md') -Raw).Trim() -eq '@AGENTS.md') 'CLAUDE.md imports AGENTS.md'
  # Hooks are opt-in: create writes none, and names oml hooks install as optional.
  Require (-not (Test-Path (Join-Path $root '.claude')) -and -not (Test-Path (Join-Path $root '.codex')) -and -not (Test-Path (Join-Path $root '.gemini'))) 'no hooks by default'
  Require (@($r.optional | Where-Object { $_ -like 'oml hooks install*' }).Count -eq 1) "optional names oml hooks install: $($r.optional -join '; ')"
  Require ((Get-Content (Join-Path $root '.gitignore') -Raw) -match '(?m)^staged/') '.gitignore'
  Require (@($r.ready | Where-Object { $_.step -in 'folders', 'memory', 'config', 'skill' -and $_.status -eq 'done' }).Count -eq 4 -and @($r.ready | Where-Object { $_.step -in 'library', 'doctor' -and $_.status -eq 'skipped' }).Count -eq 2) "ready list: $($r.ready | ConvertTo-Json -Compress)"
  Require ((Get-Content (Join-Path $root '.oml\oml-config.json') -Raw | ConvertFrom-Json).ui.targets.Count -eq 3) 'oml-config.json has the defaults'
  Push-Location $root
  try {
    $m = Json memory add "alpha uses the Core layer for entities" --kind decision
    Require ($LASTEXITCODE -eq 0 -and $m.id -eq 0) "memory works in the new project: $($m | ConvertTo-Json -Compress)"
  } finally { Pop-Location }
  Write-Host 'ok   create: layout, memory, skill, AGENTS.md, CLAUDE.md, no hooks, .gitignore, PROJECT.md'

  # 1a. --author goes into the project's config; without it, setting one is the first optional step.
  Require (@($r.optional)[0] -like 'oml config set author*') "create without --author: $($r.optional -join ' | ')"
  $a = Json create author-x --dir $Tmp --author "Ana Silva" --no-skill --no-hooks --no-library --no-doctor
  Require ($LASTEXITCODE -eq 0 -and (Get-Content (Join-Path $Tmp 'author-x\.oml\oml-config.json') -Raw | ConvertFrom-Json).author -eq 'Ana Silva' -and -not (@($a.optional) -like 'oml config set author*')) "create --author: $($a | ConvertTo-Json -Compress)"
  $x = Json create author-y --dir $Tmp --author ('a' * 101) --no-skill --no-hooks --no-library --no-doctor
  Require ($LASTEXITCODE -eq 2 -and -not (Test-Path (Join-Path $Tmp 'author-y'))) 'create --author with a name over 100 characters was not refused, or made a folder'
  Write-Host 'ok   create --author: written to .oml/oml-config.json; refused when too long'

  # 1b. skill refresh: an older or edited copy is brought up to this oml; the project's own files stay.
  $version = (Get-Content (Join-Path $root 'skill\.oml-version') -Raw).Trim()
  Require ($version -match '^\d+\.\d+\.\d+$') "create did not stamp skill/.oml-version: $version"
  Set-Content (Join-Path $root 'skill\.oml-version') '0.1.0'
  Set-Content (Join-Path $root 'skill\SKILL.md') 'old'
  Set-Content (Join-Path $root 'skill\mine.md') 'project note'
  $s = Json skill refresh --dir $root
  Require ($LASTEXITCODE -eq 0 -and $s.from -eq '0.1.0' -and $s.to -eq $version -and $s.updated -ge 1) "skill refresh: $($s | ConvertTo-Json -Compress)"
  Require ((Get-Content (Join-Path $root 'skill\SKILL.md') -Raw).Trim() -ne 'old' -and (Test-Path (Join-Path $root 'skill\mine.md'))) 'skill refresh did not update SKILL.md or removed the project file'
  $bad = Json skill refresh --dir $Tmp
  Require ($LASTEXITCODE -eq 2) "skill refresh in a folder without skill/ was not refused: $($bad | ConvertTo-Json -Compress)"
  Write-Host 'ok   skill refresh: older copy updated, project files kept, a folder without skill/ refused'

  # 2. With modules: copied in, and the map built under the project's name.
  & $Oml new (Join-Path $Tmp 'is-blank.oml') --name CrmIS --kind CrossDevice | Out-Null
  $src = Join-Path $Tmp 'src'; New-Item -ItemType Directory $src | Out-Null
  $a = Json apply (Join-Path $Tmp 'is-blank.oml') (Join-Path $PSScriptRoot 'map-is.json') (Join-Path $src 'CrmIS.oml')
  Require $a.saved 'fixture'
  $r = Json create beta --dir $Tmp --in $src --no-skill --no-hooks --no-library --no-doctor
  $beta = Join-Path $Tmp 'beta'
  Require ($LASTEXITCODE -eq 0 -and $r.map.project -eq 'beta' -and $r.map.read -eq 1 -and (Test-Path (Join-Path $beta 'modules\CrmIS.oml'))) "create --in: $($r | ConvertTo-Json -Compress -Depth 4)"
  Require (-not (Test-Path (Join-Path $beta 'skill')) -and -not (Test-Path (Join-Path $beta '.claude')) -and -not (Test-Path (Join-Path $beta '.codex'))) '--no-skill and --no-hooks'
  Push-Location $beta
  try { Require ((Json map overview).totals.modules -eq 1) 'map overview in the new project without --project' } finally { Pop-Location }
  Write-Host 'ok   create --in: modules copied, the map built and remembered by the folder'

  # 3. Refusals.
  $x = Json create alpha --dir $Tmp
  Require ($LASTEXITCODE -eq 2 -and $x.error -like '*not empty*') 'existing folder'
  $x = Json create Bad_Name --dir $Tmp
  Require ($LASTEXITCODE -eq 2 -and $x.error -like '*lower-case*') 'bad name'
  $x = Json create gamma --dir $Tmp --in (Join-Path $Tmp 'missing')
  Require ($LASTEXITCODE -eq 2 -and -not (Test-Path (Join-Path $Tmp 'gamma'))) 'bad input creates nothing'
  Write-Host 'ok   create: refuses a folder with files, a bad name and a bad input (and creates nothing)'

  # A path is the parent folder and the name; missing folders are made. --server keeps only the host, --hooks installs one agent's hooks.
  $deep = Join-Path $Tmp 'deep\er'
  $r = Json create (Join-Path $deep 'gamma') --server 'https://dev.example.com/Orders' --hooks copilot --no-library --no-doctor
  $gamma = Join-Path $deep 'gamma'
  Require ($LASTEXITCODE -eq 0 -and $r.folder -eq $gamma -and (Test-Path (Join-Path $gamma '.github\hooks\oml.json'))) "create <path> --hooks copilot: $($r | ConvertTo-Json -Compress)"
  Require ((Get-Content (Join-Path $gamma '.oml\oml-config.json') -Raw | ConvertFrom-Json).outsystems.serverHost -eq 'dev.example.com') 'create --server keeps the host'
  Require (@($r.ready | Where-Object { $_.step -eq 'hooks' -and $_.status -eq 'done' }).Count -eq 1 -and @($r.optional | Where-Object { $_ -like 'oml hooks install*' -or $_ -like '*serverHost*' }).Count -eq 0) "create hooks step and optional: $($r | ConvertTo-Json -Compress)"
  $r = Json create (Join-Path $Tmp 'Upper')
  Require ($LASTEXITCODE -eq 2 -and $r.hint -eq 'Use upper.') "a path with an upper-case name: $($r | ConvertTo-Json -Compress)"
  # Asked, for a person (OML_ASK=1 lets a pipe answer): name, folder, modules, author, server, agent, yes.
  $answers = "Delta`n$Tmp`n`nAna Dev`ndev.example.com`n1`ny`n"
  $env:OML_ASK = '1'
  try { $out = $answers | & $Oml create --no-library --no-doctor 2>&1 | Out-String } finally { $env:OML_ASK = $null }
  $delta = Join-Path $Tmp 'delta'
  Require ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $delta '.claude\settings.local.json')) -and $out -match 'The same as: oml create delta') "create asked: $out"
  Require ((Get-Content (Join-Path $delta '.oml\oml-config.json') -Raw | ConvertFrom-Json).author -eq 'Ana Dev') 'create asked: author'
  $env:OML_ASK = '1'
  try { $out = "eps`n$Tmp`n`n`n`n0`nn`n" | & $Oml create 2>&1 | Out-String } finally { $env:OML_ASK = $null }
  Require ($LASTEXITCODE -eq 2 -and -not (Test-Path (Join-Path $Tmp 'eps'))) "create asked, answered no: $out"
  Write-Host 'ok   create: a path makes missing folders, --server, --hooks, and the questions for a person (no creates nothing)'
} finally {
  foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
  Remove-TestWorkFolder $Tmp
}
