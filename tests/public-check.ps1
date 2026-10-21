# Rule 7 and the public setting, for every kind with a public setting, through the creation list and edits.set:
# private to public is allowed; a server action that becomes public (new or existing) adds a PublicServerAction
# new warning; a service action never does; public to private on an existing element is refused (rule 4).
# All applies run in one `oml run`, so Service Studio loads once.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $Tmp ('public-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir -Force | Out-Null
function Require($condition, $message) { if (-not $condition) { throw "public: $message" } }
function P($name) { Join-Path $dir $name }
$hint = 'Ask the user. If they say no, remove `public` from the spec and apply again from the same source.'

& $Oml new (P 'blank.oml') --name Pub --kind CrossDevice | Out-Null
& $Oml new (P 'svc-blank.oml') --name PubSvc --kind Service | Out-Null
Set-Content (P 'logo.svg') '<svg xmlns="http://www.w3.org/2000/svg" width="1" height="1"/>'
Set-Content (P 'helper.js') 'var a = 1;'
Set-Content (P 'data.txt') 'x'
$file = { param($n) ConvertTo-Json (P $n) }

# kind, name, edits.set path, creation-list key (none for a theme: only edits.set changes it)
$kinds = @(
  @('entity', 'E', 'E', 'entities'), @('structure', 'St', 'St', 'structures'), @('static entity', 'SE', 'SE', 'staticEntities'),
  @('block', 'B', 'MainFlow/B', 'blocks'), @('screen', 'Sc', 'MainFlow/Sc', 'screens'), @('client action', 'CA', 'CA', 'clientActions'),
  @('role', 'R', 'R', 'roles'), @('image', 'Logo', 'Logo', 'images'), @('script', 'Helper', 'Helper', 'scripts'),
  @('resource', 'Data', 'Data', 'resources'), @('theme', 'Look', 'Look', $null))
# The creation lists of every kind but the theme, without braces; and one edits.set per kind.
$listed = { param([bool]$value) ((@($kinds | Where-Object { $_[3] }) | Group-Object { $_[3] } | ForEach-Object {
  '"' + $_.Name + '": [' + (($_.Group | ForEach-Object {
    $extra = if ($_[3] -in 'images', 'scripts', 'resources') { ', "file": ' + (& $file ($(switch ($_[3]) { 'images' { 'logo.svg' } 'scripts' { 'helper.js' } default { 'data.txt' } }))) } else { '' }
    '{ "name": "' + $_[1] + '", "public": ' + "$value".ToLower() + $extra + ' }' }) -join ', ') + ']' }) -join ', ') }
$setAll = { param([bool]$value) '"edits": [' + (($kinds | ForEach-Object { '{ "op": "set", "element": "' + $_[2] + '", "properties": { "Public": ' + "$value".ToLower() + ' } }' }) -join ', ') + ']' }

$specs = [ordered]@{
  base = '{ "entities": [{ "name": "E", "attributes": [{ "name": "Name" }] }], "structures": [{ "name": "St", "attributes": [{ "name": "A" }] }],
    "staticEntities": [{ "name": "SE", "records": [{ "identifier": "A" }] }], "roles": [{ "name": "R" }],
    "blocks": [{ "name": "B", "widgets": [{ "type": "Text", "text": "x" }] }], "screens": [{ "name": "Sc", "widgets": [{ "type": "Text", "text": "x" }] }],
    "clientActions": [{ "name": "CA" }], "serverActions": [{ "name": "SA" }],
    "images": [{ "name": "Logo", "file": ' + (& $file 'logo.svg') + ' }], "scripts": [{ "name": "Helper", "file": ' + (& $file 'helper.js') + ' }],
    "resources": [{ "name": "Data", "file": ' + (& $file 'data.txt') + ' }], "themes": [{ "name": "Look", "styleSheet": ".a{color:red;}" }] }'
  # private to public: every kind in its creation list (the theme through set), and SA; only SA warns
  list = '{ ' + (& $listed $true) + ', "serverActions": [{ "name": "SA", "public": true }], "edits": [{ "op": "set", "element": "Look", "properties": { "Public": true } }] }'
  set = '{ ' + (& $setAll $true) + ' }'
  newActions = '{ "serverActions": [{ "name": "SA", "public": true }, { "name": "NewPub", "public": true }, { "name": "NewPriv" }] }'
  setAction = '{ "edits": [{ "op": "set", "element": "SA", "properties": { "Public": true } }] }'
  again = '{ "serverActions": [{ "name": "SA", "public": true }] }'
  blockEvent = '{ "blocks": [{ "name": "B", "public": true, "events": [{ "name": "OnDone", "mandatory": false }] }] }'
  many = '{ "serverActions": [' + ((1..13 | ForEach-Object { '{ "name": "Many' + $_.ToString('00') + '", "public": true }' }) -join ', ') + '] }'
  # public to private: one creation list and one edits list for every kind (SA included); each one is refused
  privList = '{ ' + (& $listed $false) + ', "serverActions": [{ "name": "SA", "public": false }], "edits": [{ "op": "set", "element": "Look", "properties": { "Public": false } }] }'
  privSet = '{ ' + (& $setAll $false).TrimEnd(']') + ', { "op": "set", "element": "SA", "properties": { "Public": false } }] }'
  svcBase = '{ "serviceActions": [{ "name": "SvA" }, { "name": "SvP", "public": true }], "serverActions": [{ "name": "Helper" }] }'
  svcList = '{ "serviceActions": [{ "name": "SvA", "public": true }], "serverActions": [{ "name": "Helper", "public": true }] }'
  svcSet = '{ "edits": [{ "op": "set", "element": "SvA", "properties": { "Public": true } }] }'
  svcPriv = '{ "serviceActions": [{ "name": "SvP", "public": false }] }'
}
foreach ($k in $specs.Keys) { Set-Content (P "$k.json") $specs[$k] -Encoding utf8 }
$steps = @(
  @('base', 'blank.oml'), @('list', 'base.oml'), @('set', 'base.oml'), @('newActions', 'base.oml'), @('setAction', 'base.oml'),
  @('again', 'list.oml'), @('blockEvent', 'base.oml'), @('many', 'base.oml'), @('privList', 'list.oml'), @('privSet', 'list.oml'),
  @('svcBase', 'svc-blank.oml'), @('svcList', 'svcBase.oml'), @('svcSet', 'svcBase.oml'), @('svcPriv', 'svcBase.oml')
) | ForEach-Object { , @('apply', (P $_[1]), (P "$($_[0]).json"), (P "$($_[0]).oml")) }
$finds = @(foreach ($k in $kinds + , @('server action', 'SA')) { , @('find', $k[1], (P 'list.oml')) }) + @(foreach ($k in $kinds) { , @('find', $k[1], (P 'set.oml')) })
$run = P 'steps.json'
@{ steps = @($steps) + @($finds) } | ConvertTo-Json -Depth 5 | Set-Content $run -Encoding utf8
$results = (& $Oml run $run --continue | ConvertFrom-Json).results
$byName = @{}; $i = 0
foreach ($s in $steps) { $byName[[IO.Path]::GetFileNameWithoutExtension($s[2])] = $results[$i++] }

function Saved($name, [string[]]$warned) {
  $r = $byName[$name]
  Require ($r.exitCode -eq 0 -and $r.output.saved) "$name was refused: $($r.output | ConvertTo-Json -Depth 8 -Compress)"
  $w = @($r.output.newWarnings | Where-Object { $_ -and $_.id -eq 'PublicServerAction' })
  $names = @($w | ForEach-Object { $_.samples.owner })
  Require ((($names | Sort-Object) -join ',') -eq (($warned | Sort-Object) -join ',')) "$name`: PublicServerAction for '$($names -join ',')', expected '$($warned -join ',')'"
  if ($warned) {
    Require ($w[0].count -eq $warned.Count -and $w[0].hint -eq $hint) "$name`: count or hint: $($w | ConvertTo-Json -Depth 5 -Compress)"
    Require (@($w[0].samples.message) -contains "server action $($warned[0]) is public: other modules can call it (rule 7).") "$name`: message"
  }
}
function Refused($name, [string[]]$texts) {
  $r = $byName[$name]
  $problems = @($r.output.problems) -join "`n"
  Require ($r.exitCode -eq 1 -and -not $r.output.saved -and -not (Test-Path (P "$name.oml"))) "$name was not refused: $($r.output | ConvertTo-Json -Depth 8 -Compress)"
  foreach ($t in $texts) { Require ($problems.Contains($t)) "$name`: no problem with '$t': $problems" }
}

Require ($byName['base'].exitCode -eq 0) "base: $($byName['base'].output | ConvertTo-Json -Depth 8 -Compress)"
Saved 'list' @('SA'); Saved 'set' @(); Saved 'newActions' @('NewPub', 'SA'); Saved 'setAction' @('SA')
Saved 'again' @(); Saved 'blockEvent' @()
# Each existing element made public is in changed, as "public of <kind> <name>", by every path; nothing when it already was.
foreach ($name in 'list', 'set') {
  $changed = @($byName[$name].output.changed)
  foreach ($k in $kinds) { Require (@($changed | Where-Object { $_ -match "^public of [a-z ]+ $([regex]::Escape($k[1]))$" }).Count -eq 1) "$name`: no 'public of ... $($k[1])' in changed: $($changed -join '|')" }
  foreach ($t in 'public of entity E', 'public of static entity SE', 'public of block B', 'public of screen Sc', 'public of client action CA') { Require ($changed -contains $t) "$name`: no '$t' in changed: $($changed -join '|')" }
}
Require (@($byName['list'].output.changed) -contains 'public of server action SA') "list: no 'public of server action SA' in changed"
Require (-not @($byName['again'].output.changed | Where-Object { $_ -like 'public of *' })) "again: an element that was public is in changed: $(@($byName['again'].output.changed) -join '|')"
# More than 12 server actions: one message names them all.
$many = @($byName['many'].output.newWarnings | Where-Object id -eq 'PublicServerAction')
Require ($many.Count -eq 1 -and $many[0].count -eq 13 -and @($many[0].samples).Count -eq 1 -and $many[0].samples[0].message.StartsWith('server actions Many01, Many02,') -and $many[0].samples[0].message.Contains('Many13 are public')) "13 public server actions: $($many | ConvertTo-Json -Depth 5 -Compress)"
# Every kind is public after the creation list and after set (find reports isPublic; an image is kind Image).
foreach ($r in $results[$steps.Count..($results.Count - 1)]) {
  Require ($r.exitCode -eq 0 -and @($r.output.matches | Where-Object { $_.source -eq 'this module' -and $_.isPublic -eq $true }).Count -eq 1) "$($r.args -join ' ') is not public: $($r.output | ConvertTo-Json -Depth 5 -Compress)"
}
Require (@($results | Where-Object { $_.args[1] -eq 'Logo' })[0].output.matches[0].kind -eq 'Image') 'find: an image is not kind Image'
$private = foreach ($k in $kinds + , @('element', 'SA')) { "refused: '$($k[1])' is an existing public $(if ($k[0] -in 'entity', 'static entity') { 'entity' } else { 'element' }). oml never makes it private" }
Refused 'privList' $private; Refused 'privSet' $private
Write-Host "ok   public: private to public for $($kinds.Count) kinds and server actions (creation list and set), PublicServerAction for server actions only (13 in one message), public to private refused"

Saved 'svcBase' @(); Saved 'svcList' @('Helper'); Saved 'svcSet' @()
Refused 'svcPriv' @("refused: 'SvP' is an existing public element. oml never makes it private")
Write-Host 'ok   public: service actions get no PublicServerAction warning'
