# oml create: a project folder with its layout, memory, the agent's skill and hooks, .gitignore and PROJECT.md; with --in,
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

  # 1. Without modules: the layout, memory, skill, hooks.
  $r = Json create alpha --dir $Tmp
  $root = Join-Path $Tmp 'alpha'
  Require ($LASTEXITCODE -eq 0 -and $r.folder -eq $root) "create: $($r | ConvertTo-Json -Compress)"
  foreach ($p in 'modules', 'staged', 'specs', 'qa', 'mocks', 'scenarios', '.oml\memory\memory.log', '.oml\map.json', 'skill\SKILL.md', 'AGENTS.md', 'CLAUDE.md', '.claude\settings.local.json', '.gitignore', 'PROJECT.md') {
    Require (Test-Path (Join-Path $root $p)) "missing $p"
  }
  Require ((Get-Content (Join-Path $root 'CLAUDE.md') -Raw).Trim() -eq '@AGENTS.md') 'CLAUDE.md imports AGENTS.md'
  Require ((Get-Content (Join-Path $root '.claude\settings.local.json') -Raw) -match 'hooks check') 'hooks installed'
  Require ((Get-Content (Join-Path $root '.gitignore') -Raw) -match '(?m)^staged/') '.gitignore'
  Push-Location $root
  try {
    $m = Json memory add "alpha uses the Core layer for entities" --kind decision
    Require ($LASTEXITCODE -eq 0 -and $m.id -eq 0) "memory works in the new project: $($m | ConvertTo-Json -Compress)"
  } finally { Pop-Location }
  Write-Host 'ok   create: layout, memory, skill, AGENTS.md, CLAUDE.md, hooks, .gitignore, PROJECT.md'

  # 2. With modules: copied in, and the map built under the project's name.
  & $Oml new (Join-Path $Tmp 'is-blank.oml') --name CrmIS --kind CrossDevice | Out-Null
  $src = Join-Path $Tmp 'src'; New-Item -ItemType Directory $src | Out-Null
  $a = Json apply (Join-Path $Tmp 'is-blank.oml') (Join-Path $PSScriptRoot 'map-is.json') (Join-Path $src 'CrmIS.oml')
  Require $a.saved 'fixture'
  $r = Json create beta --dir $Tmp --in $src --no-skill --no-hooks
  $beta = Join-Path $Tmp 'beta'
  Require ($LASTEXITCODE -eq 0 -and $r.map.project -eq 'beta' -and $r.map.read -eq 1 -and (Test-Path (Join-Path $beta 'modules\CrmIS.oml'))) "create --in: $($r | ConvertTo-Json -Compress -Depth 4)"
  Require (-not (Test-Path (Join-Path $beta 'skill')) -and -not (Test-Path (Join-Path $beta '.claude'))) '--no-skill and --no-hooks'
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
} finally {
  foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
  Remove-TestWorkFolder $Tmp
}
