# Entity options and indexes, aggregate options, group-by aliases, and the aggregate edit ops, end to end through the CLI.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($condition, $message) { if (-not $condition) { throw $message } }
$blank = Join-Path $Tmp 'r0.oml'
$fixture = Join-Path $PSScriptRoot 'data.json'
function Apply-Spec($name, $source, $json) {
  $spec = Join-Path $Tmp "$name.json"; $out = Join-Path $Tmp "$name.oml"
  Set-Content -LiteralPath $spec -Value $json -Encoding utf8
  $r = & $Oml apply $source $spec $out 2>$null | ConvertFrom-Json
  [pscustomobject]@{ Exit = $LASTEXITCODE; Report = $r; Out = $out; Text = ($r | ConvertTo-Json -Depth 8 -Compress) }
}
function Describe-Text($module, $element) { ((& $Oml describe $module $element) | ConvertFrom-Json).text }

# The checker knows the new keys and edit ops; apply suggests the right name for a misspelled key.
$check = & $Oml spec check $fixture 2>&1
Require ($LASTEXITCODE -eq 0) "spec check rejected data.json: $check"
$edits = Join-Path $Tmp 'data-edits-check.json'
Set-Content -LiteralPath $edits -Value @'
{ "edits": [ { "op": "addToAggregate", "aggregate": "A/B", "filters": ["True"], "groupSorts": [] },
             { "op": "setArgs", "element": "A/C", "args": { "X": "1" } } ],
  "screens": [ { "name": "S", "dataActions": [ { "name": "D", "fetch": "onDemand", "timeout": 5 } ] } ] }
'@
$check = & $Oml spec check $edits 2>&1
Require ($LASTEXITCODE -eq 0) "spec check rejected the edit ops: $check"
$typo = Apply-Spec 'data-typo' $blank '{ "entities": [ { "name": "A", "indexs": [], "attributes": [ { "name": "B" } ] } ] }'
Require ($typo.Exit -eq 1 -and $typo.Text.Contains('did you mean `indexes`')) "misspelled indexes: $($typo.Text)"
Write-Host 'ok   data spec keys'

$built = Join-Path $Tmp 'out-data.oml'
$r = & $Oml apply $blank $fixture $built | ConvertFrom-Json
Require ($LASTEXITCODE -eq 0 -and $r.saved -and -not $r.reopenNewErrors -and (@($r.newWarnings.id) -join ',') -eq 'PublicServerAction' -and (@($r.newWarnings.samples.owner) -join ',') -eq 'PeopleByYear') "data.json: $($r | ConvertTo-Json -Depth 8 -Compress)"
$person = Describe-Text $built Person
foreach ($line in 'label attribute Name', 'is active attribute Active', 'expose read only: no', 'index IX_CountryId_BirthDate(CountryId, BirthDate)') {
  Require ($person.Contains($line)) "Person describe lacks '$line': $person"
}
Require ((Describe-Text $built Country).Contains('identifier Code')) 'Country identifier missing'
$query = Describe-Text $built PeopleByYear
foreach ($line in 'group by BirthYear = Year(Person.BirthDate), Country.Name', 'having People >= 2', 'calculate grouped AverageSalary = Payroll / People', 'order groups by People desc', 'cache 10 min') {
  Require ($query.Contains($line)) "PeopleByYear describe lacks '$line': $query"
}
# tree used to stop on an aggregate with a group-level filter (a source whose getter throws).
$tree = & $Oml tree $built PeopleByYear/ByYear
Require ($LASTEXITCODE -eq 0 -and ($tree -join '').Contains('People >= 2')) 'tree failed on a grouped aggregate'
Write-Host 'ok   data.json entity options, indexes, group by'

# An existing entity takes new indexes and attributes; its other options are refused.
$more = Apply-Spec 'data-more' $built '{ "entities": [ { "name": "Person", "attributes": [ { "name": "Email", "type": "Email" } ], "indexes": [ { "name": "IX_Email", "attributes": ["Email"], "unique": true } ] } ] }'
Require ($more.Exit -eq 0 -and (Describe-Text $more.Out Person).Contains('index IX_Email(Email) [unique]')) "index on existing entity: $($more.Text)"
$unique = @($more.Report.newWarnings | Where-Object id -eq 'UniqueIndexOnExistingEntity')
Require ($unique.Count -eq 1 -and $unique[0].hint -and @($unique[0].samples.message) -contains 'unique index IX_Email on existing entity Person: publish fails if production has duplicate values in (Email); check before publishing.') "unique index warning must be a new warning: $($more.Text)"

# Listing an existing public element without "public" keeps it public (before, it silently became private).
$kept = Apply-Spec 'data-public' $built '{ "entities": [ { "name": "Country", "attributes": [ { "name": "Iso3", "length": 3 } ], "indexes": [ { "attributes": ["Iso3"] } ] } ],
  "structures": [ { "name": "PersonDto", "attributes": [ { "name": "Email", "type": "Email" } ] } ] }'
Require ($kept.Exit -eq 0) "public entity and structure edit: $($kept.Text)"
$inspect = & $Oml inspect $kept.Out --only entities,structures | ConvertFrom-Json
Require (($inspect.entities | Where-Object name -eq 'Country').public -eq $true -and ($inspect.structures | Where-Object name -eq 'PersonDto').public -eq $true) 'an existing public entity or structure became private'
Write-Host 'ok   data existing elements keep public'

# The model accepts these names, so oml checks them; each message is exact.
$exact = @(
  @{ name = 'alias-invalid'; spec = '{ "serverActions": [ { "name": "Bad", "flow": [ { "kind": "aggregate", "name": "Q", "sources": ["Person"], "groupBy": [ { "attribute": "Person.Name", "as": "Group Name" } ] } ] } ] }'; problem = "serverAction Bad/Q.groupBy: group name 'Group Name' is invalid; use ASCII letters, digits and _, not starting with a digit" },
  @{ name = 'alias-taken'; spec = '{ "serverActions": [ { "name": "Dup", "flow": [ { "kind": "aggregate", "name": "Q", "sources": ["Person"], "groupBy": [ "Person.Name", { "attribute": "Person.Name", "as": "Name" } ] } ] } ] }'; problem = 'serverAction Dup/Q.groupBy: group name ''Name'' is taken in this aggregate; give the group another name with "as"' }
)
foreach ($case in $exact) {
  $r = Apply-Spec "data-$($case.name)" $built $case.spec
  Require ($r.Exit -eq 1 -and @($r.Report.problems).Count -eq 1 -and @($r.Report.problems)[0] -eq $case.problem) "$($case.name): expected exactly '$($case.problem)', got $($r.Text)"
}
# A text literal gives no part of the default name.
$alias = Apply-Spec 'data-alias' $built '{ "serverActions": [ { "name": "Bands", "public": true, "outputs": [ { "name": "N", "type": "Integer" } ], "flow": [
  { "kind": "aggregate", "name": "Q", "sources": ["Person"], "groupBy": [ "If(Person.Salary > 1000, \"High\", \"Low\")" ], "aggregates": [ { "name": "People", "function": "Count", "attribute": "Person.Id" } ] },
  { "kind": "assign", "assign": { "N": "Q.List.Length" } } ] } ] }'
Require ($alias.Exit -eq 0 -and (Describe-Text $alias.Out Bands).Contains('group by IfSalary = If(Person.Salary > 1000, "High", "Low")')) "group alias with literals: $($alias.Text)"

# The seeded Id of a new entity takes the spec's type; a Text identifier has no auto number.
$ids = Apply-Spec 'data-ids' $blank '{ "entities": [ { "name": "IntKey", "attributes": [ { "name": "Id", "type": "Integer" }, { "name": "T" } ] },
  { "name": "TextKey", "identifier": "Id", "attributes": [ { "name": "Id", "type": "Text", "length": 20 }, { "name": "T" } ] } ] }'
Require ($ids.Exit -eq 0 -and @($ids.Report.created) -contains 'attribute IntKey.Id' -and @($ids.Report.created) -contains 'attribute TextKey.Id') "Id attributes: $($ids.Text)"
$intKey = Describe-Text $ids.Out IntKey; $textKey = Describe-Text $ids.Out TextKey
Require ($intKey.Contains('Id: Integer [mandatory]') -and -not $intKey.Contains('identifier')) "IntKey Id is not an auto-number Integer: $intKey"
Require ($textKey.Contains('Id: Text [mandatory]') -and $textKey.Contains('identifier Id') -and -not $textKey.Contains('[auto number]')) "TextKey Id is not a Text identifier without auto number: $textKey"

# A number is an expression too.
$numbers = Apply-Spec 'data-number-args' $built '{ "serverActions": [ { "name": "Callee", "public": true, "inputs": [ { "name": "X", "type": "Integer" } ] },
  { "name": "Caller", "public": true, "flow": [ { "kind": "execute", "name": "Run", "action": "Callee", "args": { "X": 1 } } ] } ] }'
Require ($numbers.Exit -eq 0) "number arguments: $($numbers.Text)"
$numbers = Apply-Spec 'data-number-set' $numbers.Out '{ "edits": [ { "op": "setArgs", "element": "Caller/Run", "args": { "X": 2 } } ] }'
Require ($numbers.Exit -eq 0 -and (Describe-Text $numbers.Out Caller).Contains('X = 2')) "number setArgs: $($numbers.Text)"
Write-Host 'ok   data names, Id types, number arguments'
$cases = @(
  @{ name = 'identifier'; spec = '{ "entities": [ { "name": "Person", "identifier": "Name" } ] }'; problem = "refused: 'Person' is an existing entity. oml never changes its identifier" },
  @{ name = 'update'; spec = '{ "entities": [ { "name": "Person", "updateBehavior": "changedAttributes" } ] }'; problem = 'oml never changes its update behavior' },
  @{ name = 'set-label'; spec = '{ "edits": [ { "op": "set", "element": "Person", "properties": { "labelAttribute": "Active" } } ] }'; problem = 'oml never changes its label attribute' },
  @{ name = 'set-auto'; spec = '{ "edits": [ { "op": "set", "element": "Person/Id", "properties": { "isAutoNumber": "No" } } ] }'; problem = 'mandatory or auto number setting' },
  @{ name = 'index-changed'; spec = '{ "entities": [ { "name": "Person", "indexes": [ { "name": "IX_CountryId_BirthDate", "attributes": ["BirthDate"] } ] } ] }'; problem = 'oml does not change existing indexes' },
  @{ name = 'index-attribute'; spec = '{ "entities": [ { "name": "Person", "indexes": [ { "attributes": ["Name", "Name"] } ] } ] }'; problem = 'an attribute is listed twice' },
  @{ name = 'index-missing'; spec = '{ "entities": [ { "name": "Person", "indexes": [ { "attributes": ["Missing"] } ] } ] }'; problem = "'Missing' is not an attribute of Person" },
  @{ name = 'update-value'; spec = '{ "entities": [ { "name": "New1", "updateBehavior": "sometimes", "attributes": [ { "name": "A" } ] } ] }'; problem = "updateBehavior 'sometimes' is not changedAttributes or allAttributes" },
  @{ name = 'autonumber-text'; spec = '{ "entities": [ { "name": "Tag", "identifier": "Code", "autoNumber": true, "attributes": [ { "name": "Code" }, { "name": "N" } ] } ] }'; problem = 'autoNumber needs an Integer or Long Integer identifier; Code is Text' },
  @{ name = 'identifier-missing'; spec = '{ "entities": [ { "name": "Tag", "identifier": "Code", "attributes": [ { "name": "N" } ] } ] }'; problem = "identifier 'Code' is not an attribute of Tag" },
  @{ name = 'alias-taken'; spec = '{ "serverActions": [ { "name": "Dup", "flow": [ { "kind": "aggregate", "name": "Q", "sources": ["Person"], "groupBy": [ "Person.Name", { "attribute": "Person.Name", "as": "Name" } ] } ] } ] }'; problem = "group name 'Name' is taken in this aggregate" },
  @{ name = 'fetch-server'; spec = '{ "serverActions": [ { "name": "F", "flow": [ { "kind": "aggregate", "name": "Q", "sources": ["Person"], "fetch": "onDemand" } ] } ] }'; problem = 'fetch applies only to screen and block aggregates and data actions' },
  @{ name = 'after-fetch'; spec = '{ "screens": [ { "name": "S", "aggregates": [ { "name": "Q", "sources": ["Person"], "onAfterFetch": "Missing" } ] } ] }'; problem = "onAfterFetch: no screen action 'Missing' in 'S'" },
  @{ name = 'after-fetch-server'; spec = '{ "serverActions": [ { "name": "F", "flow": [ { "kind": "aggregate", "name": "Q", "sources": ["Person"], "onAfterFetch": "X" } ] } ] }'; problem = 'onAfterFetch applies only to screen and block aggregates and data actions' },
  @{ name = 'fetch-value'; spec = '{ "screens": [ { "name": "S", "aggregates": [ { "name": "Q", "sources": ["Person"], "fetch": "later" } ] } ] }'; problem = "fetch 'later' is not atStart or onDemand" },
  @{ name = 'add-other'; spec = '{ "edits": [ { "op": "addToAggregate", "aggregate": "PeopleByYear/ByYear", "maxRecords": 5 } ] }'; problem = "addToAggregate does not take 'maxRecords'" },
  @{ name = 'add-not-aggregate'; spec = '{ "edits": [ { "op": "addToAggregate", "aggregate": "PeopleByYear", "filters": ["True"] } ] }'; problem = 'aggregate must be the path of an aggregate' },
  @{ name = 'set-args'; spec = '{ "edits": [ { "op": "setArgs", "element": "PeopleByYear/ByYear", "args": { "X": "1" } } ] }'; problem = 'node does not accept arguments' },
  @{ name = 'set-public'; spec = '{ "edits": [ { "op": "set", "element": "Country", "properties": { "public": false } } ] }'; problem = "refused: 'Country' is an existing public entity. oml never makes it private: that breaks the modules that use it (rule 4)" },
  @{ name = 'set-public-action'; spec = '{ "edits": [ { "op": "set", "element": "PeopleByYear", "properties": { "public": false } } ] }'; problem = "refused: 'PeopleByYear' is an existing public element. oml never makes it private" },
  @{ name = 'public-key'; spec = '{ "structures": [ { "name": "PersonDto", "public": false } ] }'; problem = "refused: 'PersonDto' is an existing public element. oml never makes it private" },
  @{ name = 'multi-tenant'; spec = '{ "edits": [ { "op": "set", "element": "Person", "properties": { "isMultiTenant": "Yes" } } ] }'; problem = 'oml never changes its multi-tenant setting' },
  @{ name = 'index-name'; spec = '{ "entities": [ { "name": "Person", "indexes": [ { "name": "Bad Name!", "attributes": ["Name"] } ] } ] }'; problem = "'Bad Name!' is not a valid index name" },
  @{ name = 'cache-screen'; spec = '{ "screens": [ { "name": "S", "aggregates": [ { "name": "Q", "sources": ["Person"], "cacheInMinutes": 5 } ] } ] }'; problem = 'cacheInMinutes applies only to aggregates in server actions' },
  @{ name = 'timeout-text'; spec = '{ "screens": [ { "name": "S", "aggregates": [ { "name": "Q", "sources": ["Person"], "timeout": "ten" } ] } ] }'; problem = 'timeout must be a whole number of seconds, got "ten"' },
  @{ name = 'sort-key'; spec = '{ "serverActions": [ { "name": "K", "flow": [ { "kind": "aggregate", "name": "Q", "sources": ["Person"], "sorts": [ { "attribute": "Person.Name", "dynamc": true } ] } ] } ] }'; problem = "sorts[0]: unknown key 'dynamc'; use attribute, descending, dynamic" },
  @{ name = 'group-key'; spec = '{ "edits": [ { "op": "addToAggregate", "aggregate": "PeopleByYear/ByYear", "groupBy": [ { "attribute": "Person.Name", "alias": "N" } ] } ] }'; problem = "groupBy[0]: unknown key 'alias'; use attribute, as" },
  # Deleting a filter, sort or group of a grouped aggregate can drop its source; oml refuses such a delete.
  @{ name = 'delete-last-filter'; spec = '{ "edits": [ { "op": "delete", "element": "PeopleByYear/ByCountry/Persons/Filter[0]" } ] }'; problem = "deleting 'PeopleByYear/ByCountry/Persons/Filter[0]' would also remove its source Person (a model side effect); do it in Service Studio" },
  @{ name = 'delete-group'; spec = '{ "edits": [ { "op": "delete", "element": "PeopleByYear/ByMonth/GroupBy[0]/MonthBirthDate" } ] }'; problem = 'would also remove its source Person' },
  @{ name = 'delete-group-sort'; spec = '{ "edits": [ { "op": "delete", "element": "PeopleByYear/ByMonth/CombineSources1/AttributeSort[0]" } ] }'; problem = 'would also remove its source Person' },
  @{ name = 'delete-structure'; spec = '{ "edits": [ { "op": "delete", "element": "PeopleByYear/ByYear/AddSource[0]" } ] }'; problem = 'is internal structure of an aggregate' }
)
foreach ($case in $cases) {
  $r = Apply-Spec "data-$($case.name)" $built $case.spec
  $problems = @($r.Report.problems) -join "`n"
  Require ($r.Exit -eq 1 -and -not (Test-Path -LiteralPath $r.Out) -and $problems.Contains($case.problem)) "$($case.name): expected '$($case.problem)', got $($r.Text)"
}
$check = & $Oml spec check (Join-Path $Tmp 'data-sort-key.json') 2>&1
Require ($LASTEXITCODE -ne 0 -and ($check -join '').Contains("sorts[0]: unknown key 'dynamc'")) "spec check missed a misspelled sort key: $check"
Write-Host 'ok   data refusal paths'

# Aggregate edits on an existing aggregate: add, then change and remove what was added.
$add = Apply-Spec 'data-add' $built '{ "edits": [
  { "op": "addToAggregate", "aggregate": "PeopleByYear/ByYear", "filters": [ "Person.Salary > 0" ], "groupFilters": [ "Payroll > 0" ] } ] }'
Require ($add.Exit -eq 0) "addToAggregate: $($add.Text)"
$query = Describe-Text $add.Out PeopleByYear
Require ($query.Contains('where Person.Active, Person.Salary > 0') -and $query.Contains('having People >= 2, Payroll > 0')) "addToAggregate result: $query"
$change = Apply-Spec 'data-change' $add.Out '{ "edits": [
  { "op": "set", "element": "PeopleByYear/ByYear/Persons/JoinCondition[0]", "properties": { "joinType": "Left" } },
  { "op": "set", "element": "PeopleByYear/ByYear/Persons/Filter[1]", "properties": { "condition": "Person.Salary >= 100" } },
  { "op": "delete", "element": "PeopleByYear/ByYear/Persons/Filter[0]" } ] }'
Require ($change.Exit -eq 0) "aggregate set/delete: $($change.Text)"
$query = Describe-Text $change.Out PeopleByYear
Require ($query.Contains('Person with or without Country on Person.CountryId = Country.Code where Person.Salary >= 100 group by')) "aggregate set/delete result: $query"
# A group-level filter of an aggregate that also has a plain filter can go.
$groupFilter = Apply-Spec 'data-group-filter' $built '{ "edits": [ { "op": "delete", "element": "PeopleByYear/ByCountry/CombineSources1/Filter[0]" } ] }'
Require ($groupFilter.Exit -eq 0) "group filter delete: $($groupFilter.Text)"
$query = Describe-Text $groupFilter.Out PeopleByYear
Require ($query.Contains('query ByCountry: Person where Person.Salary > 0 group by Person.CountryId aggregate People = Count(Person.Id)') -and -not $query.Contains('having People > 1')) "group filter delete result: $query"
# A model failure inside delete is a reported problem, not a crash without output.
$crash = Apply-Spec 'data-delete-crash' $built '{ "edits": [ { "op": "delete", "element": "PeopleByYear/ByYear/GroupBy[0]/People" } ] }'
Require ($crash.Exit -eq 1 -and $crash.Report -and -not (Test-Path -LiteralPath $crash.Out)) "delete of a used aggregate attribute: exit $($crash.Exit) $($crash.Text)"
Write-Host 'ok   data aggregate edits'
