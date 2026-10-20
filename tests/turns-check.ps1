param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
function Require($condition, $message) { if (!$condition) { throw $message } }
# Runs the CLI and returns the parsed JSON plus the exit code.
function Run-Oml([string[]]$cliArgs) {
    $text = & $Oml @cliArgs 2>$null
    return [pscustomobject]@{ Code = $LASTEXITCODE; Json = (($text -join "`n") | ConvertFrom-Json) }
}
$blank = Join-Path $Tmp 'turns-blank.oml'
Remove-Item (Join-Path $Tmp 'turns-*') -ErrorAction SilentlyContinue
& $Oml new $blank --kind CrossDevice --name TurnsTest | Out-Null
function Spec($name, $json) { $p = Join-Path $Tmp $name; Set-Content $p $json; return $p }

# apply --describe and --describe-created: one call builds and shows the result
$data = Spec 'turns-data.json' '{ "entities": [{ "name": "Gadget", "attributes": [{ "name": "Label" }] }] }'
$logic = Spec 'turns-logic.json' '{ "serverActions": [{ "name": "CountGadgets", "outputs": [{ "name": "N", "type": "Integer" }], "flow": [{ "kind": "assign", "assign": { "N": "0" } }] }] }'
$out = Join-Path $Tmp 'turns-out.oml'
$r = Run-Oml @('apply', $blank, $data, $logic, $out, '--describe', 'Gadget,Missing', '--describe-created')
Require ($r.Code -eq 0 -and $r.Json.saved) 'multi-spec apply with --describe failed'
Require (@($r.Json.specs).Count -eq 2) 'report lacks the specs list'
Require ($r.Json.describe.Gadget.text.Contains('Label: Text')) '--describe did not describe the entity'
Require ($r.Json.describe.CountGadgets.text.Contains('set N = 0')) '--describe-created did not describe the new action'
Require (@($r.Json.describeErrors).Count -eq 1 -and $r.Json.describeErrors[0].element -eq 'Missing') 'unknown --describe name not listed in describeErrors'

# describe with several names: array; one name: object
$many = Run-Oml @('describe', $out, 'Gadget', 'CountGadgets')
Require ($many.Code -eq 0 -and @($many.Json).Count -eq 2 -and $many.Json[1].element -like '*CountGadgets') 'describe with two names is not an array of two'
$single = Run-Oml @('describe', $out, 'Gadget')
Require ($single.Code -eq 0 -and $single.Json.element -like '*Gadget' -and -not ($single.Json -is [array])) 'describe with one name changed shape'
$bad = Run-Oml @('describe', $out, 'Gadget', 'Missing')
Require ($bad.Code -eq 1 -and @($bad.Json).Count -eq 2 -and $bad.Json[1].error) 'a missing name among several is not an error entry'

# inspect --only takes several groups at once
$inspect = Run-Oml @('inspect', $out, '--only', 'entities,actions')
Require ($inspect.Code -eq 0 -and $inspect.Json.entities -and $inspect.Json.serverActions -and -not $inspect.Json.screens) 'inspect --only entities,actions'

# spec errors that teach
$typo = Run-Oml @('apply', $blank, (Spec 'turns-typo.json' '{ "entites": [{ "name": "X" }] }'), (Join-Path $Tmp 'turns-typo.oml'))
Require ($typo.Code -eq 1 -and -not $typo.Json.saved -and $typo.Json.problems[0].Contains('did you mean `entities`?') -and $typo.Json.problems[0].Contains('Example:')) 'misspelled key: no suggestion and example'
$wrong = Run-Oml @('apply', $blank, (Spec 'turns-wrong.json' '{ "serverActions": "Run" }'), (Join-Path $Tmp 'turns-wrong.oml'))
Require ($wrong.Code -eq 1 -and $wrong.Json.problems[0].Contains('expected an array') -and $wrong.Json.problems[0].Contains('"serverActions": [{')) 'wrong value type: no example'
$kind = Run-Oml @('apply', $blank, (Spec 'turns-kind.json' '{ "serverActions": [{ "name": "A", "flow": [{ "kind": "asign" }] }] }'), (Join-Path $Tmp 'turns-kind.oml'))
Require ($kind.Code -eq 1 -and $kind.Json.problems[0].Contains('did you mean `assign`?')) 'misspelled node kind: no suggestion'

# multi-spec conflicts name both files, and nothing is staged
$dup = Run-Oml @('apply', $blank, $data, (Spec 'turns-dup.json' '{ "entities": [{ "name": "Gadget" }] }'), (Join-Path $Tmp 'turns-dup.oml'))
Require ($dup.Code -eq 1 -and $dup.Json.problems[0].Contains('turns-data.json') -and $dup.Json.problems[0].Contains('turns-dup.json') -and -not (Test-Path (Join-Path $Tmp 'turns-dup.oml'))) 'duplicate element across specs not refused with both files'
$scalar = Run-Oml @('apply', $blank, (Spec 'turns-k1.json' '{ "keepComments": true }'), (Spec 'turns-k2.json' '{ "keepComments": false }'), (Join-Path $Tmp 'turns-scalar.oml'))
Require ($scalar.Code -eq 1 -and $scalar.Json.problems[0].Contains('turns-k1.json') -and $scalar.Json.problems[0].Contains('turns-k2.json')) 'scalar conflict across specs not refused with both files'

# single-spec usage is unchanged: no specs list in the report
$plain = Run-Oml @('apply', $blank, $data, (Join-Path $Tmp 'turns-plain.oml'))
Require ($plain.Code -eq 0 -and $plain.Json.saved -and -not $plain.Json.specs -and -not $plain.Json.describe) 'single-spec report changed'
Write-Host 'ok   turns: apply --describe, multi describe, teaching spec errors, multi-spec apply'
