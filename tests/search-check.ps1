# oml doc --search, oml spec --search, oml warn ranking and oml find --text: the search index covers every heading
# and recipe, the benchmark (tests/search-bench.json) meets its thresholds against the baselines, normalization,
# edge cases, output size, speed, and module search on a fixture module. Throws on the first failure.
# -RealModule <file.oml>: also run the module benchmark on a copy of a real module (not part of the suite).
param([string]$Oml, [string]$Tmp, [string]$Module, [string]$RealModule)
$ErrorActionPreference = 'Stop'
$work = Join-Path $Tmp 'search-check'; New-Item -ItemType Directory -Force $work | Out-Null
$bench = Join-Path $PSScriptRoot 'search-bench.json'

function Fail($m) { throw "search: $m" }
function Run([string[]]$a) {
  $out = & $Oml @a 2>&1
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = ($out | ForEach-Object { "$_" }) -join "`n" }
}
function Search([string]$q, [string[]]$more = @()) {
  $r = Run (@('doc', '--search', $q) + $more)
  if ($r.Exit -ne 0) { Fail "doc --search '$q' exit $($r.Exit): $($r.Text)" }
  $r.Text | ConvertFrom-Json
}

# 1. every heading of every topic and of the spec is indexed, found by its title (top 3) and opened by its command;
#    every recipe has a header comment that finds it
$r = Run 'doc', '--search-verify'
if ($r.Exit -ne 0) { Fail "search-verify:`n$($r.Text)" }
Write-Host "ok   search: $($r.Text)"

# 2. benchmark: each mode on five sets. tune: used for tuning. held: 60 queries written blind after the first tuning.
# blind2: 30 blind queries from a reviewer, in general words. After both were measured, the synonym gate and the
# concept links were cross-validated on all three, so held and blind2 are no longer blind. blind3: 60 queries from a
# final independent evaluator, mostly broad questions in general words (6 with typos, 6 mixed, 4 unanswerable),
# measured once without any change to the ranking: it gives the release number. Thresholds for the full engine are a
# few points below the measured values:
#   tune 0.867/0.948/0.914, held 0.842/0.982/0.914, blind2 0.741/1.000/0.858, blind3 0.536/0.679/0.621 (top1/top3/MRR).
# Then the glossary (src/SearchGlossary.cs), the stem fixes and the new weights were cross-validated on all four sets, so
# blind3 is no longer blind either: a fresh set gives the final number. Measured after that change:
#   tune 0.867/0.963/0.919, held 0.860/1.000/0.924, blind2 0.778/0.963/0.865, blind3 0.732/0.875/0.810.
# blind4: 60 queries from a last independent evaluator, the same mix as blind3, written without the engine, the maps or the
# earlier sets, and measured once with no ranking change: 0.589/0.750/0.681 (BM25F 0.446/0.643/0.562, BM25 0.304/0.482/
# 0.435, grep 0.268/0.482/0.391). The command entries (one per oml --help line) were then added and checked on tune, held,
# blind2 and blind3 only (MRR 0.913, 0.933, 0.884, 0.812; tune -0.007, the others up); blind4, measured once after that
# to confirm, dropped to 0.536/0.732/0.648 (BM25F 0.411/0.643/0.542, BM25 0.286/0.482/0.420, grep 0.268/0.482/0.390):
# a command entry is first in 1 of the 7 changed queries, the others moved with the index statistics. This is the
# release number. Do not tune on blind4.
$min = @{
  'tune' = @{ top1 = 0.83; top3 = 0.94; mrr = 0.89 }
  'held' = @{ top1 = 0.82; top3 = 0.96; mrr = 0.89 }
  'blind2' = @{ top1 = 0.74; top3 = 0.92; mrr = 0.83 }
  'blind3' = @{ top1 = 0.68; top3 = 0.83; mrr = 0.77 }
  'blind4' = @{ top1 = 0.50; top3 = 0.70; mrr = 0.61 }
}
$file = Get-Content -Raw $bench | ConvertFrom-Json
$tuneCount = @($file.tune).Count; $held = @($file.held).Count; $blind2 = @($file.blind2).Count; $blind3 = @($file.blind3).Count; $blind4 = @($file.blind4).Count
if ($tuneCount -lt 130) { Fail "search-bench.json has $tuneCount tune queries; at least 130 expected" }
$sets = @('tune') + @(if ($held -gt 0) { 'held' }) + @(if ($blind2 -gt 0) { 'blind2' }) + @(if ($blind3 -gt 0) { 'blind3' }) + @(if ($blind4 -gt 0) { 'blind4' })
function Metrics($rows) {
  $a = @($rows | Where-Object answerable)
  $n = [math]::Max(1, $a.Count)
  [pscustomobject]@{
    n = $a.Count
    top1 = @($a | Where-Object rank -eq 1).Count / $n
    top3 = @($a | Where-Object { $_.rank -ge 1 -and $_.rank -le 3 }).Count / $n
    mrr = ($a | ForEach-Object { if ($_.rank -gt 0) { 1 / $_.rank } else { 0 } } | Measure-Object -Sum).Sum / $n
  }
}
$table = foreach ($mode in 'grep', 'bm25', 'bm25f', 'synonyms', 'typos', 'full') {
  $r = Run 'doc', '--search-bench', $bench, '--mode', $mode
  if ($r.Exit -ne 0) { Fail "bench $mode exit $($r.Exit): $($r.Text)" }
  $out = $r.Text | ConvertFrom-Json
  foreach ($set in $sets) {
    $m = Metrics ($out.results | Where-Object set -eq $set)
    [pscustomobject]@{ mode = $mode; set = $set; n = $m.n; top1 = $m.top1; top3 = $m.top3; mrr = $m.mrr; ms = $out.ms; build = $out.buildMs; results = $out.results }
  }
}
Write-Host ('     {0,-9} {1,-6} {2,4} {3,6} {4,6} {5,6}' -f 'mode', 'set', 'n', 'top1', 'top3', 'MRR')
foreach ($t in $table) { Write-Host ('     {0,-9} {1,-6} {2,4} {3,6:P0} {4,6:P0} {5,6:N3}' -f $t.mode, $t.set, $t.n, $t.top1, $t.top3, $t.mrr) }
$full = @($table | Where-Object mode -eq 'full')
foreach ($t in $full) {
  foreach ($k in 'top1', 'top3', 'mrr') { if ($t.$k -lt $min[$t.set][$k]) { Fail "full $($t.set) $k $([math]::Round($t.$k, 3)) is below $($min[$t.set][$k])" } }
}
# The full engine must beat every baseline on each set (on held it is close: MRR 0.933 against 0.924 for BM25F).
foreach ($set in $sets) {
  $f = $full | Where-Object set -eq $set
  foreach ($b in @($table | Where-Object { $_.set -eq $set -and $_.mode -in 'grep', 'bm25', 'bm25f' })) {
    if ($b.mrr -ge $f.mrr) { Fail "baseline $($b.mode) (MRR $([math]::Round($b.mrr, 3))) is not below the full engine (MRR $([math]::Round($f.mrr, 3))) on the $set set" }
  }
}
# An exact key or id is always the first hit. (--search-verify above also searches every spec key and warning id alone.)
$exact = @($full[0].results | Where-Object { $_.type -eq 'exact' -and $_.rank -ne 1 })
if ($exact) { Fail "exact-key queries not first: $(($exact | ForEach-Object q) -join ', ')" }
# The weak flag, calibrated on the four earlier sets pooled to flag at most about 10% of the answerable queries: with
# blind4 it flags 10 of the 17 unanswerable queries and 31 of the 331 answerable ones (9%; precision 0.24, recall 0.59).
# On blind4 alone: 4 of 4 unanswerable and 9 of 56 answerable (16%).
# A question the docs do not answer often shares all its words with them, so most cannot be told apart by words alone.
$rows = $full[0].results
$un = @($rows | Where-Object { -not $_.answerable }); $an = @($rows | Where-Object answerable)
$tp = @($un | Where-Object weak).Count; $fp = @($an | Where-Object weak).Count
if ($tp -lt 6 -or $fp -gt 0.10 * $an.Count) {
 Fail "weak flag: $tp of $($un.Count) unanswerable and $fp of $($an.Count) answerable queries flagged" }
# On a quiet machine the index builds in about 50 ms and a query takes about 1 ms (target: under 200 ms). The limit is
# 500 ms because the parallel suite loads the machine (161 ms measured there).
$perQuery = $full[0].ms / $rows.Count
if ($full[0].build + $perQuery -gt 500) { Fail "index build $($full[0].build) ms + $([math]::Round($perQuery, 1)) ms per query is over 500 ms" }
Write-Host ("ok   search bench: tune top1 {0:P0}, top3 {1:P0}, MRR {2:N3}; held MRR {3:N3}; blind2 MRR {4:N3}; blind3 MRR {13:N3}; blind4 top1 {14:P0}, top3 {15:P0}, MRR {16:N3}; every exact key first; weak flag on {5}/{6} unanswerable, {7}/{8} answerable; index {9} ms, {10:N1} ms per query in-process" -f `
  $full[0].top1, $full[0].top3, $full[0].mrr, ($full | Where-Object set -eq 'held').mrr, ($full | Where-Object set -eq 'blind2').mrr, $tp, $un.Count, $fp, $an.Count, $full[0].build, $perQuery,
  ($full | Where-Object set -eq 'blind3').top1, ($full | Where-Object set -eq 'blind3').top3, ($full | Where-Object set -eq 'blind3').mrr,
  ($full | Where-Object set -eq 'blind4').top1, ($full | Where-Object set -eq 'blind4').top3, ($full | Where-Object set -eq 'blind4').mrr)

# 3. normalization: camel and snake case split with the joined form kept, '-' joins, light stemming, stop words,
#    synonyms (lower weight), typo fixes (lower weight), and a known word is not "fixed"
function Terms([string]$t) { $r = Run 'doc', '--search-terms', $t; if ($r.Exit -ne 0) { Fail "search-terms exit $($r.Exit)" }; $r.Text | ConvertFrom-Json }
$t = Terms 'OnAfterFetch snake_case_name HTTPRequest z-index'
foreach ($want in 'after', 'fetch', 'onafterfetch', 'snak', 'cas', 'snakecasenam', 'http', 'request', 'httprequest', 'z', 'index', 'zindex') {
  if ($want -notin $t.terms) { Fail "terms of 'OnAfterFetch snake_case_name HTTPRequest z-index' lack '$want': $($t.terms -join ' ')" }
}
if ('on' -in $t.terms) { Fail "stop word 'on' kept: $($t.terms -join ' ')" }
$t = Terms 'pages pagination paginations page entities entity validated validate callable call securely secure'
$stems = @($t.terms)
foreach ($pair in @(0, 3), @(1, 2), @(4, 5), @(6, 7), @(8, 9), @(10, 11)) {
  if ($stems[$pair[0]] -ne $stems[$pair[1]]) { Fail "stemming: '$($stems[$pair[0]])' and '$($stems[$pair[1]])' differ ($($stems -join ' '))" }
}
$t = Terms 'how do I do it with the'
if (@($t.terms).Count -ne 0) { Fail "stop words kept: $($t.terms -join ' ')" }
$t = Terms 'modal'
if ($t.query.modal -ne 1 -or -not ($t.query.popup -gt 0 -and $t.query.popup -lt 1) -or $t.query.model) { Fail "synonyms of modal: $($t.query | ConvertTo-Json -Compress)" }
$t = Terms 'pagnation'
if (-not ($t.query.pagination -gt 0 -and $t.query.pagination -lt 1)) { Fail "typo pagnation not fixed to pagination: $($t.query | ConvertTo-Json -Compress)" }
# stems that must not meet an unrelated word
$t = @((Terms 'logged log setting set typing type paging page locale local date dat').terms)
for ($i = 0; $i -lt $t.Count; $i += 2) { if ($t[$i] -eq $t[$i + 1]) { Fail "stem collision: $($t -join ' ')" } }
# every stem that merges words seen 5 times or more is in the reviewed list tests/search-stems.txt
$reviewed = @(Get-Content (Join-Path $PSScriptRoot 'search-stems.txt') | Where-Object { $_ -and -not $_.StartsWith('#') })
$merges = @((Run 'doc', '--search-stems', '5').Text -split "`n" | Where-Object { $_ } | ForEach-Object { ($_ -replace ' \d+(,|$)', '$1').Trim() })
$new = @($merges | Where-Object { $_ -notin $reviewed })
if ($new) { Fail "stems that merge words and are not reviewed (check each, protect a word in Search.cs if it merges two concepts, then add the line to tests/search-stems.txt): $($new -join ' | ')" }
# glossary: an everyday word alone finds the OutSystems concept's section
foreach ($pair in @('enum', 'oml doc data-rules#static-entities'), @('modal', 'oml doc ui-reactive#popups'), @('page', 'oml doc ui-reactive#screens-and-blocks'), @('cron', 'oml doc processes-timers-email#timers'), @('screen reader', 'oml doc ui-reactive#accessibility-musts')) {
  $h = @((Search $pair[0]).hits)
  if ($h[0].open -ne $pair[1]) { Fail "glossary '$($pair[0])': first hit $($h[0] | ConvertTo-Json -Compress)" }
}

# a typo is fixed on the word as typed, before stemming: unsed -> unused
$h = @((Search 'unsed action warning').hits)
if (-not ($h | Where-Object open -eq 'oml warn UnusedUserAction')) { Fail "typo 'unsed': $(($h | ForEach-Object open) -join ', ')" }
# singular and plural of an exact key: screen and edit find the screens and edits keys first
foreach ($q in 'screen', 'edit', 'event') {
  $h = @((Search $q).hits)
  if ($h[0].kind -ne 'key') { Fail "exact key '$q' (singular): first hit $($h[0] | ConvertTo-Json -Compress)" }
}
# every hit opens a different place, and a key opens its own section
foreach ($q in 'screens', 'paging', 'edits delete', 'osui pattern for tabs and cards') {
  $opens = @((Search $q @('--top', '5')).hits | ForEach-Object { $_.open.ToLowerInvariant() })
  if (@($opens | Select-Object -Unique).Count -ne $opens.Count) { Fail "'$q' lists one place twice: $($opens -join ', ')" }
}
$h = @((Search 'screens').hits | Where-Object kind -eq 'key')
if (($h | ForEach-Object open | Sort-Object) -join '|' -ne 'oml spec "Reactive UI and Mobile UI"|oml spec "Traditional Web UI"') { Fail "screens keys open: $(($h | ForEach-Object open) -join ', ')" }
$h = @((Search 'rule 2').hits)
if ($h[0].open -ne 'oml doc rules' -or $h[0].title -notmatch '^Rule 2: Never delete') { Fail "rule 2: $($h[0] | ConvertTo-Json -Compress)" }
# exact boost: an identifier is first even inside a longer query
$h = (Search 'how to use replaceWidgets on a screen').hits
if ($h[0].open -ne 'oml spec Edits' -or $h[0].title -ne 'replaceWidgets') { Fail "exact identifier in a longer query: $($h[0] | ConvertTo-Json -Compress)" }
# commands: every usage line of oml --help is an entry that opens 'oml <command> --help'; commands no doc topic
# describes are found (oml image: ICO, PNG to SVG tracing, the SVG optimizer)
$cmds = @(((Run 'doc', '--search-list').Text -split "`n") | Where-Object { $_ -like 'command:*' })
if ($cmds.Count -lt 40) { Fail "only $($cmds.Count) command entries" }
foreach ($q in 'convert png to svg', 'make a favicon ico', 'optimize svg file size', 'trace a logo to vector paths') {
  $h = @((Search $q).hits)
  if ($h[0].kind -ne 'command' -or $h[0].open -ne 'oml image --help') { Fail "'$q': first hit $($h[0] | ConvertTo-Json -Compress)" }
}
$r = Run 'image', '--help'
if ($r.Exit -ne 0 -or $r.Text -notmatch '--to ico' -or $r.Text -notmatch '--mode trace') { Fail "oml image --help: exit $($r.Exit), $($r.Text)" }
Write-Host 'ok   search normalization: camel/snake/hyphen split, joined form, stemming, stop words, synonyms, typo fix, exact boost, command entries'

# 4. output: kind, title, open, snippet (at most about 160 characters), score; 3 hits by default; about 400 tokens at most
$sizes = foreach ($q in 'show a list with paging', 'why does my screen flicker', 'send an email from a server action', 'only managers can open this screen', 'fix SQL injection warning') {
  $r = Run 'doc', '--search', $q
  $o = $r.Text | ConvertFrom-Json
  if (@($o.hits).Count -ne 3) { Fail "'$q' gave $(@($o.hits).Count) hits, 3 expected" }
  foreach ($x in $o.hits) {
    foreach ($k in 'kind', 'title', 'open', 'snippet', 'score') { if ($null -eq $x.$k) { Fail "'$q': hit without $k" } }
    if ($x.kind -notin 'doc', 'spec', 'key', 'warning', 'recipe', 'command') { Fail "'$q': kind $($x.kind)" }
    if ($x.snippet.Length -gt 170) { Fail "'$q': snippet of $($x.snippet.Length) characters" }
    if ($x.open -notmatch '^(oml (doc|spec|warn) |skill/recipes/|oml [a-z]+ --help$)') { Fail "'$q': open '$($x.open)'" }
  }
  $r.Text.Length
}
$maxTokens = [math]::Ceiling(($sizes | Measure-Object -Maximum).Maximum / 4)
if ($maxTokens -gt 400) { Fail "a 3-hit result is $maxTokens tokens; at most 400" }
$o = Search 'paging' @('--top', '1')
if (@($o.hits).Count -ne 1) { Fail '--top 1' }
$o = Search 'show a list with paging'
if ($o.hits[0].open -notin 'skill/recipes/list-search-paging.jsonc', 'oml doc ui-reactive#tables-lists-sorting-paging') { Fail "paging: first hit $($o.hits[0].open)" }

# 5. edge cases: empty, bad --top, very long, non-ASCII (output stays ASCII), nothing found
foreach ($bad in @(@('doc', '--search'), @('doc', '--search', '   '), @('doc', '--search', 'x', '--top', '0'), @('doc', '--search', 'x', '--top', '99'), @('doc', '--search', 'x', '--bogus'), @('spec', '--search'))) {
  $r = Run $bad
  if ($r.Exit -ne 2 -or $r.Text -notmatch 'Usage|--top|Unknown option') { Fail "$($bad -join ' '): exit $($r.Exit), $($r.Text)" }
}
$long = ((1..400) | ForEach-Object { "paging list screen aggregate $_" }) -join ' '
$watch = [Diagnostics.Stopwatch]::StartNew()
$o = Search $long
if ($watch.ElapsedMilliseconds -gt 3000 -or @($o.hits).Count -eq 0) { Fail "very long query: $($watch.ElapsedMilliseconds) ms, $(@($o.hits).Count) hits" }
$r = Run 'doc', '--search', "caf$([char]0xE9) a$([char]0xE7)$([char]0xE3)o $([char]0x65E5)$([char]0x672C) popup"
if ($r.Exit -ne 0 -or $r.Text -match '[^\x00-\x7F]' -or $r.Text -notmatch '\\u00E9') { Fail "non-ASCII query: exit $($r.Exit), $($r.Text)" }
$o = $r.Text | ConvertFrom-Json
if ($o.query -ne "caf$([char]0xE9) a$([char]0xE7)$([char]0xE3)o $([char]0x65E5)$([char]0x672C) popup" -or @($o.hits).Count -eq 0) { Fail "non-ASCII query lost: $($r.Text)" }
$o = Search 'qqqq zzzz xxxx'
if (@($o.hits).Count -ne 0 -or $o.hint -notmatch 'oml doc') { Fail "no match: $($o | ConvertTo-Json -Compress)" }
# a weak result says so in one field (and a hint); a strong one has neither
$o = Search 'export a list to PDF'
if ($o.weak -ne $true -or $o.hint -notmatch 'may not cover') { Fail "weak result: $($o | ConvertTo-Json -Compress -Depth 4)" }
$o = Search 'send an email from a server action'
if ($null -ne $o.weak -or $null -ne $o.hint) { Fail "strong result flagged: $($o | ConvertTo-Json -Compress -Depth 4)" }
Write-Host "ok   search output: fields, 3 hits by default, at most $maxTokens tokens, edge cases (empty, --top, long, non-ASCII, no match)"

# 6. oml spec --search: only spec sections and keys; oml warn: closest ids ranked by the engine
$r = Run 'spec', '--search', 'load data only when a row is picked'
$o = $r.Text | ConvertFrom-Json
if ($r.Exit -ne 0 -or @($o.hits | Where-Object { $_.kind -notin 'spec', 'key' })) { Fail "spec --search kinds: $($r.Text)" }
$r = Run 'warn', 'input', 'value', 'missing', 'from', 'mandatory', 'block', 'argument'
if ($r.Exit -ne 2 -or $r.Text -notmatch 'Closest: [^.]*(RequiredPropertyValue_PropertyIn|InputMandatoryMismatch)') { Fail "warn ranking: $($r.Text)" }

# 7. --in: a folder of the user's own markdown, read at run time
$docs = Join-Path $work 'team-docs'; New-Item -ItemType Directory -Force (Join-Path $docs 'sub') | Out-Null
Set-Content (Join-Path $docs 'naming.md') "# Team naming`n`n## Entities`n`nEntity names are singular nouns.`n`n## Timers`n`nTimer names start with Timer_ and run at night." -Encoding utf8
Set-Content (Join-Path $docs 'sub/screens.md') "# Screens`n`n| Screen | Rule |`n|---|---|`n| Dashboard | Show at most six counters. |" -Encoding utf8
Push-Location $work
try {
  $o = (Run 'doc', '--search', 'nightly job naming', '--in', $docs).Text | ConvertFrom-Json
  if ($o.hits[0].open -ne 'team-docs/naming.md#timers' -or $o.hits[0].kind -ne 'file') { Fail "--in: $($o | ConvertTo-Json -Compress -Depth 4)" }
  $o = (Run 'doc', '--search', 'how many counters on the dashboard', '--in', $docs).Text | ConvertFrom-Json
  if ($o.hits[0].title -ne 'Dashboard') { Fail "--in table row: $($o | ConvertTo-Json -Compress -Depth 4)" }
  $r = Run 'doc', '--search', 'x', '--in', (Join-Path $work 'missing')
  if ($r.Exit -ne 2 -or $r.Text -notmatch 'Folder not found') { Fail "--in missing folder: $($r.Text)" }
  # A junction back to the folder (a loop) is not followed; a .md file with a NUL byte is skipped with a note; a long
  # heading is clipped to 80 characters.
  New-Item -ItemType Junction -Path (Join-Path $docs 'loop') -Target $docs | Out-Null
  [IO.File]::WriteAllBytes((Join-Path $docs 'binary.md'), [byte[]](35, 32, 65, 0, 66, 10))
  Set-Content (Join-Path $docs 'long.md') ("# Long`n`n## " + ('heading ' * 40) + "flamingo`n`nPink birds.") -Encoding utf8
  $o = (Run 'doc', '--search', 'flamingo heading', '--in', $docs).Text | ConvertFrom-Json
  if ($o.hits[0].title.Length -gt 80 -or $o.hits[0].open.Length -gt 120) { Fail "--in long heading: $($o.hits[0] | ConvertTo-Json -Compress)" }
  if (@($o.notes | Where-Object { $_ -match 'binary .md' -and $_ -match 'binary.md' }).Count -ne 1) { Fail "--in binary file note: $($o | ConvertTo-Json -Compress -Depth 4)" }
  $o = (Run 'doc', '--search', 'nightly job naming', '--in', $docs).Text | ConvertFrom-Json
  if (@($o.hits | Where-Object { $_.open -match '/loop/' }).Count -ne 0) { Fail "--in followed the junction loop: $($o | ConvertTo-Json -Compress -Depth 4)" }
  # Above 20 MB, reading stops with a note.
  $big = Join-Path $work 'big-docs'; New-Item -ItemType Directory -Force $big | Out-Null
  $line = ('lorem ipsum dolor sit amet ' * 40) + "`n"
  [IO.File]::WriteAllText((Join-Path $big 'a.md'), "# Small`n`nPelican notes.")
  [IO.File]::WriteAllText((Join-Path $big 'b.md'), "# Big`n`n" + ($line * 20000))
  $watch = [Diagnostics.Stopwatch]::StartNew()
  $o = (Run 'doc', '--search', 'pelican', '--in', $big).Text | ConvertFrom-Json
  if ($o.hits[0].title -ne 'Small' -or @($o.notes | Where-Object { $_ -match '20 MB' }).Count -ne 1) { Fail "--in size cap: $($o | ConvertTo-Json -Compress -Depth 4)" }
  [IO.Directory]::Delete((Join-Path $docs 'loop')) # the link only, so no later clean-up walks the loop
  Write-Host "ok   --in: junction loop skipped, binary file noted, long heading clipped, 20 MB cap noted ($($watch.ElapsedMilliseconds) ms)"
} finally { Pop-Location }

# 8. end-to-end time of one search (process start included)
$e2e = foreach ($i in 1..5) { (Measure-Command { & $Oml doc --search 'confirm before delete' | Out-Null }).TotalMilliseconds }
$e2e = [math]::Round(($e2e | Sort-Object)[2])
if ($e2e -gt 2000) { Fail "end-to-end doc --search takes $e2e ms" }
Write-Host "ok   search: spec --search, warn ranking, --in folder, end to end $e2e ms (median of 5)"

# 9. module search: a fixture module, in one oml run with doc and warn steps (they run in the launcher, no model)
$mod = Join-Path $work 'search.oml'
$a = & $Oml apply $Module (Join-Path $PSScriptRoot 'search-module.json') $mod | ConvertFrom-Json
if (-not $a.saved -or $a.newErrors) { Fail "fixture apply: $($a | ConvertTo-Json -Depth 6 -Compress)" }
# expect: a name or path of an element that answers; exact: it must be the first hit
$moduleBench = @(
  @{ q = 'unpaid bills'; expect = 'GetOverdueInvoices' }, @{ q = 'approve invoice'; expect = 'Invoice_Approve' },
  @{ q = 'Invoice_Approve'; expect = 'Invoice_Approve'; exact = $true }, @{ q = 'DueDate'; expect = 'DueDate'; exact = $true },
  @{ q = 'overdue'; expect = 'GetOverdueInvoices' }, @{ q = 'welcome message'; expect = 'CustomerGreeting', 'Build' },
  @{ q = 'who buys from us'; expect = 'Customer' }, @{ q = 'pay before the due date'; expect = 'InvoiceCard/Container[0]/Text[0]' },
  @{ q = 'invoice card block'; expect = 'InvoiceCard' }, @{ q = 'sql late invoices due'; expect = 'FetchLate' },
  @{ q = 'manger checks'; expect = 'Invoice_Approve' }, @{ q = 'invoice-card style class'; expect = 'InvoiceCard/Container[0]', 'InvoiceCard' }
)
function ModuleBench($file, $cases) {
  $steps = Join-Path $work ("steps-" + [IO.Path]::GetFileNameWithoutExtension($file) + '.json')
  $list = @(foreach ($c in $cases) { , @('find', $file, '--text', $c.q, '--top', '3') })
  $list += , @('doc', '--search', 'block event', '--top', '1')
  $list += , @('warn', 'NoErrorHandlingInFlow')
  @{ steps = $list } | ConvertTo-Json -Depth 5 | Set-Content $steps -Encoding utf8
  $run = (& $Oml run $steps) | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0) { Fail "module bench run exit $LASTEXITCODE" }
  $docStep = $run.results[$cases.Count]; $warnStep = $run.results[$cases.Count + 1]
  if ($docStep.exitCode -ne 0 -or @($docStep.output.hits).Count -ne 1) { Fail "doc --search inside oml run: $($docStep | ConvertTo-Json -Depth 6 -Compress)" }
  if ($warnStep.exitCode -ne 0 -or $warnStep.output -notmatch 'NoErrorHandlingInFlow') { Fail "warn inside oml run: $($warnStep | ConvertTo-Json -Depth 6 -Compress)" }
  $top1 = 0; $top3 = 0; $ms = @()
  for ($i = 0; $i -lt $cases.Count; $i++) {
    $c = $cases[$i]; $res = $run.results[$i]
    if ($res.exitCode -ne 0) { Fail "find --text '$($c.q)': exit $($res.exitCode) $($res | ConvertTo-Json -Depth 6 -Compress)" }
    $hits = @($res.output.hits)
    foreach ($x in $hits) { if (-not $x.kind -or -not $x.why) { Fail "find --text '$($c.q)': hit without kind or why" } }
    $rank = 0
    for ($j = 0; $j -lt $hits.Count; $j++) { if ($hits[$j].name -in $c.expect -or $hits[$j].path -in $c.expect) { $rank = $j + 1; break } }
    if ($rank -eq 1) { $top1++ }
    if ($rank -ge 1) { $top3++ } else { Fail "find --text '$($c.q)' does not find $($c.expect -join ' or ') in the top 3: $(($hits | ForEach-Object { $_.path }) -join ', ')" }
    if ($c.exact -and $rank -ne 1) { Fail "find --text '$($c.q)': exact name not first" }
    $ms += $res.output.ms
  }
  [pscustomobject]@{ top1 = $top1 / $cases.Count; top3 = $top3 / $cases.Count; ms = [math]::Round(($ms | Measure-Object -Average).Average); searched = $run.results[0].output.searched }
}
$m = ModuleBench $mod $moduleBench
# A hit's path is one that edits accept: describe resolves it.
$d = & $Oml describe $mod 'InvoiceCard/Container[0]/Text[0]'
if ($LASTEXITCODE -ne 0) { Fail "describe of a find --text path: $d" }
$r = Run 'find', $mod, '--text'
if ($r.Exit -ne 2 -or $r.Text -notmatch 'oml find <module.oml> --text') { Fail "find --text without words: $($r.Text)" }
Write-Host ("ok   find --text: fixture {0} queries, top1 {1:P0}, top3 {2:P0}, {3} elements, {4} ms per search in oml run; doc and warn steps in oml run" -f $moduleBench.Count, $m.top1, $m.top3, $m.searched, $m.ms)

if ($RealModule) {
  $copy = Join-Path $work 'real.oml'; Copy-Item $RealModule $copy
  $real = @(
    @{ q = 'theme button'; expect = 'ThemeBtn' }, @{ q = 'serif font button'; expect = 'FontSerif' },
    @{ q = 'toggle theme action'; expect = 'ShowcaseToggleTheme', 'Toggle' }, @{ q = 'copy to clipboard'; expect = 'Copy', 'FxCopyText', 'ShowcaseCopy', 'CopyText' },
    @{ q = 'dark mode'; expect = 'Toggle', 'Prefs', 'ThemeBtn' }, @{ q = 'intro text'; expect = 'Intro' },
    @{ q = 'Desc07'; expect = 'Desc07'; exact = $true }, @{ q = 'Card16'; expect = 'Card16'; exact = $true }
  )
  $m = ModuleBench $copy $real
  Write-Host ("ok   find --text: real module {0} queries, top1 {1:P0}, top3 {2:P0}, {3} elements, {4} ms per search in oml run" -f $real.Count, $m.top1, $m.top3, $m.searched, $m.ms)
}
