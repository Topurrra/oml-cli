# oml spec: key lookup, shape check and JSON Schema. None of it needs Service Studio. Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $Tmp 'spec-check'; New-Item -ItemType Directory -Force $work | Out-Null

function Fail($m) { throw "spec: $m" }
function Run([string[]]$a) {
  $out = & $Oml @a 2>&1
  [pscustomobject]@{ Exit = $LASTEXITCODE; Text = ($out | ForEach-Object { "$_" }) -join "`n"; Lines = @($out).Count }
}
function SpecFile($name, $json) { $p = Join-Path $work $name; Set-Content -Path $p -Value $json -Encoding utf8; $p }

# 1. one key prints one section
$r = Run 'spec', 'entities'
if ($r.Exit -ne 0) { Fail "entities exit $($r.Exit)" }
if ($r.Text -notmatch '(?m)^### Data model$') { Fail 'entities: Data model heading missing' }
if ($r.Text -match '(?m)^### REST$') { Fail 'entities: unrelated REST section printed' }
if ($r.Lines -ge 80) { Fail "entities prints $($r.Lines) lines" }
if (($r.Text -split "`n")[-1] -ne 'Full reference: skill/reference/spec.md#data-model') { Fail 'entities: last line is not the Full reference line' }

# 2. the keys the task names, and every flow node kind, resolve to a short section
foreach ($key in 'aggregate', 'screens', 'edits', 'sql', 'restClients', 'tidyFlows', 'keepComments', 'serverActions', 'assign', 'if', 'forEach', 'execute', 'javaScript', 'ENTITIES', 'rest', 'removeUnusedDependencies') {
  $r = Run 'spec', $key
  if ($r.Exit -ne 0) { Fail "'$key' exit $($r.Exit): $($r.Text)" }
  if ($r.Lines -lt 8 -or $r.Lines -gt 90) { Fail "'$key' prints $($r.Lines) lines" }
  if (($r.Text -split "`n")[-1] -notmatch '^Full reference: skill/reference/spec\.md#[a-z0-9-]+$') { Fail "'$key': no Full reference line" }
}
$tidy = Run 'spec', 'tidyFlows'
if ($tidy.Text -notmatch 'Use the root key `tidyFlows`' -or $tidy.Text -match '(?m)^### Reactive') { Fail 'tidyFlows: wrong section' }

# 3. unknown key: exit 2 and the closest names
$r = Run 'spec', 'entitis'
if ($r.Exit -ne 2 -or $r.Text -notmatch 'entities') { Fail "unknown key: exit $($r.Exit), text: $($r.Text)" }

# 4. the key list
$r = Run 'spec'
if ($r.Exit -ne 0 -or $r.Lines -lt 15 -or $r.Lines -gt 40) { Fail "oml spec list: exit $($r.Exit), $($r.Lines) lines" }
foreach ($key in 'entities', 'tidyFlows', 'keepComments', 'restClients', 'edits') { if ($r.Text -notmatch "\b$key\b") { Fail "oml spec list lacks $key" } }

# 5. every key in the SKILL.md index and every alias still leads to a heading in spec.md
$r = Run 'spec', '--verify'
if ($r.Exit -ne 0) { Fail "key index out of date: $($r.Text)" }

# 6. no false alarms: every recipe and every fixture spec passes
$good = @(Get-ChildItem (Join-Path $repo 'skill/recipes') -Filter *.jsonc) +
  @(Get-ChildItem (Join-Path $repo 'tests') -Filter *.json | Where-Object Name -ne 'architecture-sample.json')
foreach ($f in $good) {
  $r = Run 'spec', 'check', $f.FullName
  if ($r.Exit -ne 0 -or ($r.Text | ConvertFrom-Json).ok -ne $true) { Fail "false alarm on $($f.Name): $($r.Text)" }
}
Write-Host "ok   spec check: $($good.Count) recipes and fixtures pass"

# 7. one wrong spec, four faults
$badPath = SpecFile 'bad.json' @'
{
  // comments are allowed
  "entities": [
    { "name": "Customer", "attributes": [ { "name": "A" }, { "name": "B" }, { "name": "Name", "lenght": 100 } ] },
    { "attributes": [ { "name": "X" } ] }
  ],
  "serverActions": [ { "name": "Work", "flow": [
    { "kind": "assign", "name": "Set", "assign": { "A": "1" }, "next": "Nowhere" } ] } ],
  "keepComments": "yes"
}
'@
$r = Run 'spec', 'check', $badPath
$report = $r.Text | ConvertFrom-Json
if ($r.Exit -ne 1 -or $report.ok -ne $false) { Fail "bad spec: exit $($r.Exit)" }
$expected = [ordered]@{
  'entities[0].attributes[2].lenght'  = "did you mean 'length'"
  'entities[1]'                       = "missing required key 'name'"
  'serverActions[0].flow[0].next'     = "no node named 'Nowhere'"
  'keepComments'                      = 'expected true or false'
}
if ($report.problems.Count -ne $expected.Count) { Fail "bad spec: $($report.problems.Count) problems: $($r.Text)" }
foreach ($path in $expected.Keys) {
  $p = $report.problems | Where-Object path -eq $path
  if (-not $p -or $p.message -notlike "*$($expected[$path])*") { Fail "bad spec: no problem at $path with '$($expected[$path])': $($r.Text)" }
}

# trailing commas are refused, as apply refuses them; bad node kind and edit op are named
$r = Run 'spec', 'check', (SpecFile 'comma.json' '{ "exceptions": ["A",], }')
if ($r.Exit -ne 1 -or $r.Text -notmatch 'Invalid spec JSON') { Fail "trailing comma accepted: $($r.Text)" }
$r = Run 'spec', 'check', (SpecFile 'kinds.json' '{ "clientActions": [ { "name": "A", "flow": [ { "kind": "asign" }, { "kind": "if", "condition": "True", "true": "End" } ] } ], "edits": [ { "op": "renam", "element": "A" } ] }')
$paths = ($r.Text | ConvertFrom-Json).problems.path
if ($r.Exit -ne 1 -or $paths -notcontains 'clientActions[0].flow[0].kind' -or $paths -notcontains 'clientActions[0].flow[1]' -or $paths -notcontains 'edits[0].op') {
  Fail "kind/op faults not found: $($r.Text)"
}
if ((Run "spec", "check", "no-such-file.json").Exit -ne 2) { Fail 'missing file should exit 2' }

# 8. every edit op that src/Core/Edits.cs switches on is known to the checker
$edits = Get-Content -Raw (Join-Path $repo 'src/Core/Edits.cs')
$block = [regex]::Match($edits, '(?s)switch \(Required\("op"\)\)(.*?)default:').Groups[1].Value
$ops = [regex]::Matches($block, 'case "(\w+)":') | ForEach-Object { $_.Groups[1].Value }
if ($ops.Count -lt 9) { Fail "found only $($ops.Count) edit ops in Edits.cs" }
foreach ($op in $ops) {
  $r = Run 'spec', 'check', (SpecFile "op-$op.json" (@{ edits = @(@{ op = $op }) } | ConvertTo-Json -Compress))
  if ($r.Text -match 'unknown edit op') { Fail "edit op '$op' (src/Core/Edits.cs) is unknown to oml spec check" }
}

# 9. the JSON Schema
$schemaPath = Join-Path $work 'spec.schema.json'
Remove-Item $schemaPath -ErrorAction SilentlyContinue
$r = Run 'spec', 'schema', '--out', $schemaPath
if ($r.Exit -ne 0 -or -not (Test-Path $schemaPath)) { Fail "schema --out: $($r.Text)" }
$schema = Get-Content -Raw $schemaPath | ConvertFrom-Json -AsHashtable
if ($schema['$schema'] -ne 'https://json-schema.org/draft/2020-12/schema' -or -not $schema['$defs'] -or -not $schema['properties']['entities']) { Fail 'schema: missing $schema, $defs or entities' }
if ((Run 'spec', 'schema', '--out', $schemaPath).Exit -ne 2) { Fail 'schema --out over an existing file should exit 2' }
if ((Run 'spec', 'schema', '--out', $schemaPath, '--force').Exit -ne 0) { Fail 'schema --force failed' }
$stdout = (Run 'spec', 'schema').Text | ConvertFrom-Json -AsHashtable
if ($stdout['$defs'].Count -ne $schema['$defs'].Count) { Fail 'schema on stdout differs from the file' }

# A small structural validator: type, required, properties, additionalProperties false, items, $ref, oneOf, const.
function Resolve-Ref($ref) { $node = $schema; foreach ($part in $ref.Substring(2) -split '/') { $node = $node[$part] }; $node }
function Test-Schema($value, $s, $path, $problems) {
  if ($s.ContainsKey('$ref')) { $s = Resolve-Ref $s['$ref'] }
  if ($s.ContainsKey('const')) { if ($value -ne $s['const']) { $problems.Add("$path const") }; return }
  if ($s.ContainsKey('oneOf')) {
    $ok = $false
    foreach ($branch in $s['oneOf']) { $sub = [Collections.Generic.List[string]]::new(); Test-Schema $value $branch $path $sub; if ($sub.Count -eq 0) { $ok = $true } }
    if (-not $ok) { $problems.Add("$path oneOf") }
  }
  switch ($s['type']) {
    'string' { if ($value -isnot [string]) { $problems.Add("$path string") } }
    'boolean' { if ($value -isnot [bool]) { $problems.Add("$path boolean") } }
    'integer' { if ($value -isnot [int] -and $value -isnot [long]) { $problems.Add("$path integer") } }
    'array' {
      if ($value -isnot [array]) { $problems.Add("$path array"); return }
      $i = 0; foreach ($item in $value) { if ($s.ContainsKey('items')) { Test-Schema $item $s['items'] "$path[$i]" $problems }; $i++ }
    }
    'object' {
      if ($value -isnot [System.Collections.IDictionary]) { $problems.Add("$path object"); return }
      foreach ($req in $s['required']) { if (-not $value.ContainsKey($req)) { $problems.Add("$path missing $req") } }
      foreach ($key in $value.Keys) {
        $child = if ($path) { "$path.$key" } else { $key }
        if ($s.ContainsKey('properties') -and $s['properties'].ContainsKey($key)) { Test-Schema $value[$key] $s['properties'][$key] $child $problems }
        elseif ($s['additionalProperties'] -is [System.Collections.IDictionary]) { Test-Schema $value[$key] $s['additionalProperties'] $child $problems }
        elseif ($s['additionalProperties'] -eq $false) { $problems.Add("$child unknown") }
      }
    }
  }
}
function Read-Spec($path) {
  # PowerShell 7 reads JSON comments but a trailing comma is an error, as in apply.
  Get-Content -Raw $path | ConvertFrom-Json -AsHashtable
}
foreach ($f in $good) {
  $problems = [Collections.Generic.List[string]]::new()
  Test-Schema (Read-Spec $f.FullName) $schema '' $problems
  if ($problems.Count) { Fail "schema rejects $($f.Name): $($problems -join '; ')" }
}
$problems = [Collections.Generic.List[string]]::new()
Test-Schema (Read-Spec $badPath) $schema '' $problems
foreach ($want in 'entities[0].attributes[2].lenght unknown', 'entities[1] missing name', 'keepComments boolean') {
  if ($problems -notcontains $want) { Fail "schema does not report '$want' for the bad spec: $($problems -join '; ')" }
}
Write-Host 'ok   spec schema: accepts the same good specs and flags the same typed faults as spec check'

# 10. speed: the largest recipe checks in under a second, process start included
$largest = $good | Sort-Object Length -Descending | Select-Object -First 1
$best = 1e9
1..5 | ForEach-Object {
  $t = Measure-Command { & $Oml spec check $largest.FullName | Out-Null }
  $best = [Math]::Min($best, $t.TotalMilliseconds)
}
if ($best -ge 1000) { Fail "spec check on $($largest.Name) took $([int]$best) ms" }
Write-Host "ok   spec check on $($largest.Name) ($($largest.Length) bytes): $([int]$best) ms"
# Note keys ("_" or "$" prefix) are allowed by apply, so spec check must allow them too.
$notes = Join-Path $Tmp 'spec-notes.json'
'{ "_note": "x", "$schema": "s.json", "entities": [ { "name": "A", "_why": "y", "attributes": [ { "name": "B" } ] } ] }' | Set-Content $notes
$r = & $Oml spec check $notes | ConvertFrom-Json
if (-not $r.ok) { Fail "spec check refused note keys: $($r.problems | ConvertTo-Json -Compress)" }
Write-Host 'ok   oml spec: key lookup, check, schema'
