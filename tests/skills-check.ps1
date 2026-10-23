# oml skills (company skills): add, list, oml doc <name> and #section, the oml doc list, search with company hits first,
# the refusals (no frontmatter, bad name, a topic name, short description, too big, a secret, a duplicate without
# --force), the warnings, --force, remove. OML_SKILLS points at a test folder, so the user's skills are never touched.
# Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$work = Join-Path $Tmp ('skills-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Force $work | Out-Null
function Fail($m) { throw "skills: $m" }
function Require($condition, $message) { if (-not $condition) { Fail $message } }
function Call([string[]]$a) {
  $out = & $Oml @a 2>&1
  $text = ($out | ForEach-Object { "$_" }) -join "`n"
  $json = $null; try { $json = $text | ConvertFrom-Json } catch { }
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text; Json = $json }
}
function Skill([string]$file, [string]$text) { $p = Join-Path $work $file; Set-Content $p $text; $p }
$saved = $env:OML_SKILLS
try {
  $env:OML_SKILLS = Join-Path $work 'installed'
  $r = Call 'skills'
  Require ($r.Exit -eq 0 -and $r.Json.skills.Count -eq 0 -and $r.Json.hint -match 'skills add') "empty list: $($r.Text)"

  $good = Skill 'naming.md' "---`nname: acme-naming`ndescription: Use when you name modules, entities or actions in Acme projects.`n---`n# Acme naming`n`n## Modules`n`nCore modules end with _CS. The quuxor prefix marks shared modules.`n`n## Entities`n`nEntity names are singular.`n"
  $r = Call 'skills', 'add', $good
  Require ($r.Exit -eq 0 -and $r.Json.added -eq 'acme-naming' -and -not $r.Json.replaced) "add: $($r.Text)"
  Require (Test-Path (Join-Path $env:OML_SKILLS 'acme-naming.md')) 'installed copy missing'
  $r = Call 'skills'
  Require ($r.Json.skills.Count -eq 1 -and $r.Json.skills[0].open -eq 'oml doc acme-naming') "list: $($r.Text)"

  $r = Call 'doc', 'acme-naming'
  Require ($r.Exit -eq 0 -and $r.Text -match 'quuxor prefix' -and $r.Text -notmatch '^---') "oml doc <name>: $($r.Text)"
  $r = Call 'doc', 'acme-naming#entities'
  Require ($r.Exit -eq 0 -and $r.Text -match 'singular' -and $r.Text -notmatch 'quuxor') "oml doc <name>#section: $($r.Text)"
  $r = Call 'doc'
  Require ($r.Text -match 'Company skills' -and $r.Text -match 'acme-naming  -  Use when you name') "oml doc list: $($r.Text)"
  $r = Call 'doc', '--search', 'quuxor prefix modules'
  Require ($r.Exit -eq 0 -and $r.Json.hits[0].kind -eq 'company' -and $r.Json.hits[0].open -match '^oml doc acme-naming') "search: $($r.Text)"
  $r = Call 'doc', '--search', 'aggregate join group by'
  Require ($r.Exit -eq 0 -and $r.Json.hits[0].kind -ne 'company') "an unrelated search put a company hit first: $($r.Text)"

  $cases = @(
    @('nofront.md', "# Just text", 'frontmatter'),
    @('badname.md', "---`nname: Acme Naming`ndescription: Use when naming things in Acme.`n---`nText.", 'lower-case'),
    @('topic.md', "---`nname: security`ndescription: Use when securing Acme apps.`n---`nText.", 'oml doc topic'),
    @('short.md', "---`nname: acme-short`ndescription: Naming`n---`nText.", 'at least 10'),
    @('big.md', ("---`nname: acme-big`ndescription: Use when testing the size limit.`n---`n" + ('word ' * 7000)), 'limit is 8000'),
    @('secret.md', ("---`nname: acme-secret`ndescription: Use when connecting to the Acme database.`n---`nConnection: Server=db;Password=" + ('Ab1' * 6) + ";"), 'Remove it')
  )
  foreach ($c in $cases) {
    $r = Call 'skills', 'add', (Skill $c[0] $c[1])
    Require ($r.Exit -eq 2 -and $r.Text -match [regex]::Escape($c[2])) "$($c[0]) not refused with '$($c[2])': $($r.Text)"
  }
  $r = Call 'skills', 'add', (Skill 'notmd.txt' 'x')
  Require ($r.Exit -eq 2 -and $r.Text -match 'Markdown') "a .txt file: $($r.Text)"
  $r = Call 'skills', 'add', $good
  Require ($r.Exit -eq 2 -and $r.Text -match '--force') "duplicate without --force: $($r.Text)"

  $long = Skill 'long.md' ("---`nname: acme-naming`ndescription: Use when you name modules, entities or actions in Acme projects.`n---`n" + ('one two three four five six seven eight nine ten ' * 3) + 'end.')
  $r = Call 'skills', 'add', $long, '--force'
  Require ($r.Exit -eq 0 -and $r.Json.replaced -and ($r.Json.warnings -join ' ') -match 'over 25 words' -and ($r.Json.warnings -join ' ') -match 'No headings') "--force with warnings: $($r.Text)"
  Require (@(Get-ChildItem $env:OML_SKILLS -Filter *.md).Count -eq 1) 'replace left two files'

  $r = Call 'skills', 'remove', 'acme-naming'
  Require ($r.Exit -eq 0 -and $r.Json.removed -eq 'acme-naming' -and (Test-Path $good)) "remove: $($r.Text)"
  $r = Call 'skills', 'remove', 'acme-naming'
  Require ($r.Exit -eq 1) "remove twice: $($r.Text)"
  $r = Call 'doc', 'acme-naming'
  Require ($r.Exit -eq 2) "a removed skill still prints: $($r.Text)"
  Write-Host 'ok   skills: add, list, doc <name> and #section, doc list, company hits first, 8 refusals, warnings, --force, remove'
}
finally { if ($null -eq $saved) { Remove-Item env:OML_SKILLS -ErrorAction SilentlyContinue } else { $env:OML_SKILLS = $saved } }
