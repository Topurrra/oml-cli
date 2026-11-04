# oml test scenarios: a source module and a staged copy with a new public action (a branch, a raised exception, a
# mandatory input, an entity write) and a new screen (a named field, a button whose action shows a warning or a success
# message). Checks the BDD and QA scenarios, the qa.json skeleton, that a private action and an unchanged module give
# nothing, and the refusals. Throws on the first failure.
param([string]$Oml, [string]$Tmp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib.ps1')
$Fixtures = $Tmp
$Tmp = New-TestFolder $Tmp 'scenarios'
$saved = $env:OML_LOG
try {
  function Require($condition, $message) { if (-not $condition) { throw "scenarios: $message" } }
  function Json { param([Parameter(ValueFromRemainingArguments)]$a) $out = & $Oml @a; ($out -join "`n") | ConvertFrom-Json }
  $env:OML_LOG = '0'
  $blank = Join-Path $Tmp 'Forms.oml'; $staged = Join-Path $Tmp 'Forms.staged.oml'
  & $Oml new $blank --name Forms --kind CrossDevice | Out-Null
  $r = Json apply $blank (Join-Path $PSScriptRoot 'scenarios.json') $staged
  Require $r.saved "fixture: $($r | ConvertTo-Json -Compress -Depth 5)"

  $out = Join-Path $Tmp 'out'
  $s = Json test scenarios $blank $staged --out $out
  Require ($LASTEXITCODE -eq 0 -and $s.actions.Count -eq 1 -and $s.actions[0].name -eq 'SaveContact' -and $s.actions[0].status -eq 'new' -and $s.screens[0].name -eq 'ContactForm') "result: $($s | ConvertTo-Json -Compress -Depth 5)"
  Require ($s.bdd -like 'oml test scaffold *SaveContact --bdd*') 'bdd command'
  $md = Get-Content (Join-Path $out 'scenarios.md') -Raw
  foreach ($title in 'Main path', 'Raises NameRequired', 'Name <> "" is true', 'Name <> "" is false', 'Name is empty', 'Click SaveButton: NewName <> "" is true', 'Click SaveButton: NewName <> "" is false') {
    Require ($md.Contains("**Scenario: $title**")) "missing scenario: $title"
  }
  Require ($md -match 'saves a Contact record' -and $md -match 'warning message shows: "Type a name"' -and $md -match 'success message shows: "Contact saved"') 'scenario outcomes'
  Require (-not ($md -match 'Helper')) 'a private action must not be listed'
  Write-Host 'ok   test scenarios: BDD per public action (branches, raise, empty input, entity write), QA per screen path'

  $qa = Get-Content (Join-Path $out 'qa\ContactForm.qa.json') -Raw | ConvertFrom-Json
  Require ($qa.steps[0].open -eq 'ContactForm' -and $qa.steps[1].fill.NameInput -and $qa.steps[2].click -eq 'SaveButton' -and $qa.steps[2].expect.feedback.type -eq 'success') "qa skeleton: $($qa | ConvertTo-Json -Compress -Depth 6)"
  Require ((Get-Content (Join-Path $out 'qa\ContactForm.qa.json') -Raw) -match '<FILL: https://') 'qa skeleton: readable placeholder'
  Write-Host 'ok   test scenarios: the qa.json skeleton follows the success path'

  # Nothing changed: no scenario. A folder that is not empty is refused.
  $same = Json test scenarios $staged $staged --out (Join-Path $Tmp 'same')
  Require ($same.actions.Count -eq 0 -and $same.screens.Count -eq 0 -and (Get-Content (Join-Path $Tmp 'same\scenarios.md') -Raw) -match 'nothing to test') "unchanged: $($same | ConvertTo-Json -Compress -Depth 5)"
  $busy = Json test scenarios $blank $staged --out $out
  Require ($LASTEXITCODE -eq 2 -and $busy.error -like '*not empty*') 'non-empty folder'
  Write-Host 'ok   test scenarios: an unchanged module gives none; a used folder is refused'
} finally {
  $env:OML_LOG = $saved
  Remove-TestWorkFolder $Tmp
}
