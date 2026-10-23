# oml library and doc --search --library, offline: no library yet, an import with --from, status, search with a hit that
# opens its file and section, a "{ #anchor }" title, refused options, and a failed fetch (a proxy that refuses) that keeps
# the last copy. OML_LIBRARY points the library at a test folder, so the user's library is never touched.
# Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$work = Join-Path $Tmp ('library-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Force $work | Out-Null
function Fail($m) { throw "library: $m" }
function Require($condition, $message) { if (-not $condition) { Fail $message } }
function Call([string[]]$a) {
  $out = & $Oml @a 2>&1
  $text = ($out | ForEach-Object { "$_" }) -join "`n"
  $json = $null; try { $json = $text | ConvertFrom-Json } catch { }
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $text; Json = $json }
}
$saved = @{ OML_LIBRARY = $env:OML_LIBRARY; HTTPS_PROXY = $env:HTTPS_PROXY; HTTP_PROXY = $env:HTTP_PROXY; NO_PROXY = $env:NO_PROXY }
try {
  $env:OML_LIBRARY = Join-Path $work 'lib'

  $r = Call 'library'
  Require ($r.Exit -eq 0 -and $r.Json.sources.Count -eq 0 -and $r.Json.hint -match 'library fetch') "empty status: $($r.Text)"
  $r = Call 'doc', '--search', 'zone address', '--library'
  Require ($r.Exit -eq 2 -and $r.Text -match 'oml library fetch') "search without a library: $($r.Text)"

  # A repository as GitHub's "Download ZIP" gives it: src\ with Markdown, a LICENSE, and files that are not copied.
  $repo = Join-Path $work 'docs-product-master'
  New-Item -ItemType Directory -Force (Join-Path $repo 'src\deploying-apps\zones'), (Join-Path $repo 'images') | Out-Null
  Set-Content (Join-Path $repo 'src\deploying-apps\zones\address.md') "# Deployment zones`n`nIntro.`n`n## Zone address { #zone-address }`n`nThe zorblax address of a zone is a bare host name.`n"
  Set-Content (Join-Path $repo 'src\other.md') "# Other page`n`nNothing here.`n"
  Set-Content (Join-Path $repo 'README.md') "# Not under src"
  Set-Content (Join-Path $repo 'LICENSE') 'Attribution-NonCommercial-NoDerivatives 4.0 International'

  $r = Call 'library', 'fetch', '--from', $repo
  Require ($r.Exit -eq 2 -and $r.Text -match 'exactly one --source') "--from without --source: $($r.Text)"
  $r = Call 'library', 'fetch', '--source', 'nosuch'
  Require ($r.Exit -eq 2 -and $r.Text -match 'Unknown source') "unknown source: $($r.Text)"
  $r = Call 'library', 'fetch', '--source', 'product', '--from', $work
  Require ($r.Exit -eq 2 -and $r.Text -match 'No src folder') "--from without src: $($r.Text)"

  $r = Call 'library', 'fetch', '--source', 'product', '--from', $repo
  Require ($r.Exit -eq 0 -and $r.Json.sources[0].files -eq 2 -and $r.Json.sources[0].commit -eq 'local copy') "import: $($r.Text)"
  $copy = Join-Path $env:OML_LIBRARY 'docs-product'
  Require (Test-Path (Join-Path $copy 'LICENSE.txt')) 'LICENSE.txt not copied'
  Require (-not (Test-Path (Join-Path $copy 'README.md'))) 'a file outside src was copied'
  Require (@(Get-ChildItem $env:OML_LIBRARY -Directory | Where-Object Name -like '*.tmp-*').Count -eq 0) 'a temp folder was left'
  $r = Call 'library'
  Require ($r.Json.sources.Count -eq 1 -and $r.Json.sources[0].repo -eq 'OutSystems/docs-product') "status: $($r.Text)"

  $r = Call 'doc', '--search', 'zorblax zone address', '--library'
  $hit = $r.Json.hits[0]
  Require ($r.Exit -eq 0 -and $hit.kind -eq 'library' -and $hit.title -eq 'Zone address') "search: $($r.Text)"
  Require ($hit.open -match '#zone-address$' -and (Test-Path ($hit.open -replace '#.*$', ''))) "hit does not open its file: $($hit.open)"
  # The saved index: written by the import and the first search, read by the next one (same hit), rebuilt when it is
  # damaged or when a source changes.
  $index = Join-Path $env:OML_LIBRARY 'index.bin'
  Require (Test-Path $index) 'no saved index after the import'
  $again = Call 'doc', '--search', 'zorblax zone address', '--library'
  Require ($again.Json.hits[0].open -eq $hit.open -and $again.Json.hits[0].score -eq $hit.score) "saved index gives another hit: $($again.Text)"
  Set-Content $index 'damaged'
  $again = Call 'doc', '--search', 'zorblax zone address', '--library'
  Require ($again.Exit -eq 0 -and $again.Json.hits[0].open -eq $hit.open -and (Get-Item $index).Length -gt 100) "damaged index: $($again.Text)"
  Add-Content (Join-Path $repo 'src\other.md') "`n## Gloopwerk`n`nA word that only the second import has.`n"
  $r = Call 'library', 'fetch', '--source', 'product', '--from', $repo
  Require ($r.Exit -eq 0 -and $r.Json.indexed -gt 0) "re-import: $($r.Text)"
  $again = Call 'doc', '--search', 'gloopwerk', '--library'
  Require ($again.Json.hits[0].title -eq 'Gloopwerk') "a changed source kept the old index: $($again.Text)"
  $r = Call 'doc', '--search', 'zone', '--library', '--in', $repo
  Require ($r.Exit -eq 2 -and $r.Text -match 'do not go together') "--in with --library: $($r.Text)"
  $r = Call 'doc', '--search', 'zorblax'
  Require ($r.Exit -eq 0 -and @($r.Json.hits | Where-Object kind -eq 'library').Count -eq 0) "plain search read the library: $($r.Text)"

  # A fetch that cannot reach GitHub fails with the ZIP hint and keeps the last copy.
  $env:HTTPS_PROXY = 'http://127.0.0.1:9'; $env:HTTP_PROXY = 'http://127.0.0.1:9'; $env:NO_PROXY = ''
  $r = Call 'library', 'fetch', '--source', 'product'
  Require ($r.Exit -eq 2 -and $r.Json.sources[0].error -and $r.Json.hint -match 'Download ZIP') "failed fetch: $($r.Text)"
  Require ((Get-Content (Join-Path $copy 'source.json') -Raw) -match 'local copy') 'a failed fetch removed the last copy'
  Require (@(Get-ChildItem $env:OML_LIBRARY -Directory | Where-Object Name -like '*.tmp-*').Count -eq 0) 'a failed fetch left a temp folder'

  $r = Call 'library', '--help'
  Require ($r.Exit -eq 0 -and $r.Text -match 'oml library fetch') "help: $($r.Text)"
  Write-Host 'ok   library: empty status, import with --from (src only, LICENSE), status, search hit with file and section, refusals, failed fetch keeps the last copy'
}
finally {
  foreach ($k in $saved.Keys) { Set-Item "env:$k" $saved[$k] -ErrorAction SilentlyContinue; if ($null -eq $saved[$k]) { Remove-Item "env:$k" -ErrorAction SilentlyContinue } }
}
