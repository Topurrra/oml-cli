<#
.SYNOPSIS
    Checks an oml module the same way every time: validation counts and element counts.

.DESCRIPTION
    Runs `oml validate <module> --warnings` and `oml inspect <module>`, prints the errors, warnings and
    counts, and checks each -Expect rule. It never changes the module.

    -Expect rules:
      Pattern:N            counts blocks whose name matches Pattern (a * wildcard is allowed)
      section:Pattern:N    counts another section: blocks, screens, entities, structures,
                           serverActions, clientActions or actions (server and client actions)

    Exit codes: 0 = no validation errors and every rule matches; 1 = errors or a rule does not match;
    2 = the check could not run (missing oml or module, or oml did not return JSON).

.PARAMETER OutFile
    Optional. Writes the result as JSON (used by bench.ps1).

.EXAMPLE
    pwsh -NoProfile -File tests/bench/verify.ps1 -Oml 'C:\oml\oml.exe' -Module 'C:\bench\oml\result.oml' -Expect 'Fx*:24', 'screens:Showcase:1'
#>
[CmdletBinding()]
param(
    [string]$Oml = 'oml',
    [Parameter(Mandatory)][string]$Module,
    [string[]]$Expect = @(),
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'
$Sections = 'blocks', 'screens', 'entities', 'structures', 'serverActions', 'clientActions', 'actions'
$CountedSections = 'blocks', 'screens', 'entities', 'structures', 'serverActions', 'clientActions'

function Stop-Check([string]$Message) {
    Write-Host "ERROR: $Message"
    exit 2
}

function Get-Section($Data, [string]$Section) {
    $names = if ($Section -eq 'actions') { 'serverActions', 'clientActions' } else { @($Section) }
    foreach ($name in $names) {
        $prop = $Data.PSObject.Properties[$name]
        if ($prop -and $prop.Value) { foreach ($item in @($prop.Value)) { $item } }
    }
}

$omlPath = if (Test-Path -LiteralPath $Oml -PathType Leaf) {
    (Resolve-Path -LiteralPath $Oml).Path
} else {
    (Get-Command -Name $Oml -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
}
if (-not $omlPath) { Stop-Check "oml not found: $Oml" }
if (-not (Test-Path -LiteralPath $Module -PathType Leaf)) { Stop-Check "module not found: $Module" }
$modulePath = (Resolve-Path -LiteralPath $Module).Path
if ([IO.Path]::GetExtension($modulePath) -ne '.oml') { Stop-Check 'the module must be an .oml file' }

$rules = foreach ($rule in $Expect) {
    $parts = $rule.Split(':')
    if ($parts.Count -eq 2) { $section = 'blocks'; $pattern = $parts[0]; $count = $parts[1] }
    elseif ($parts.Count -eq 3) { $section = $parts[0]; $pattern = $parts[1]; $count = $parts[2] }
    else { Stop-Check "bad -Expect '$rule': use Pattern:N or section:Pattern:N" }
    if ($Sections -notcontains $section) { Stop-Check "bad -Expect '$rule': section must be one of $($Sections -join ', ')" }
    if ($count -notmatch '^\d+$') { Stop-Check "bad -Expect '$rule': the count must be a whole number" }
    [pscustomobject]@{ Expect = $rule; Section = $section; Pattern = $pattern; Expected = [int]$count }
}

# Validation: oml prints JSON with errorCount and warningCount.
$validateOut = & $omlPath validate $modulePath --warnings 2>$null
$validateCode = $LASTEXITCODE
$validateText = ($validateOut | Out-String).Trim()
try { $validate = $validateText | ConvertFrom-Json } catch { $validate = $null }
if ($validateCode -eq 2) {
    $detail = if ($validate -and $validate.error) { $validate.error } else { $validateText }
    Stop-Check "oml validate could not run (exit 2): $detail"
}
if (-not $validate -or $null -eq $validate.errorCount) {
    Stop-Check "oml validate did not return the expected JSON (exit $validateCode). Run: oml validate `"$modulePath`" --warnings"
}
$errorCount = [int]$validate.errorCount
$warningCount = [int]$validate.warningCount

# Inspect: element lists with names.
$inspectOut = & $omlPath inspect $modulePath --only entities,structures,actions,screens,blocks 2>$null
$inspectCode = $LASTEXITCODE
if ($inspectCode -ne 0) { Stop-Check "oml inspect failed (exit $inspectCode)" }
try { $inspect = ($inspectOut | Out-String) | ConvertFrom-Json } catch { Stop-Check 'oml inspect did not return JSON' }

$counts = [ordered]@{}
foreach ($section in $CountedSections) { $counts[$section] = @(Get-Section $inspect $section).Count }

$results = @(foreach ($rule in $rules) {
    $found = @(Get-Section $inspect $rule.Section | Where-Object { $_.name -like $rule.Pattern })
    [pscustomobject]@{
        expect   = $rule.Expect
        section  = $rule.Section
        pattern  = $rule.Pattern
        expected = $rule.Expected
        found    = $found.Count
        names    = @($found | ForEach-Object { $_.name })
        ok       = ($found.Count -eq $rule.Expected)
    }
})

$ok = ($errorCount -eq 0) -and (@($results | Where-Object { -not $_.ok }).Count -eq 0)

Write-Host "Module: $modulePath"
Write-Host "Validation: $errorCount error(s), $warningCount warning(s)"
Write-Host ('Counts: ' + (($counts.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join ', '))
foreach ($r in $results) {
    $status = if ($r.ok) { 'OK      ' } else { 'MISMATCH' }
    Write-Host "$status $($r.section) '$($r.pattern)': expected $($r.expected), found $($r.found)"
    if (-not $r.ok -and $r.names.Count) { Write-Host ('          ' + (($r.names | Select-Object -First 40) -join ', ')) }
}
if ($errorCount -gt 0) {
    Write-Host 'oml validate output:'
    Write-Host $validateText.Substring(0, [Math]::Min(3000, $validateText.Length))
}
Write-Host ('Result: ' + $(if ($ok) { 'OK' } else { 'FAILED' }))

if ($OutFile) {
    $report = [ordered]@{
        module       = $modulePath
        ok           = $ok
        errorCount   = $errorCount
        warningCount = $warningCount
        counts       = $counts
        checks       = $results
        validate     = $validate
    }
    $outPath = [IO.Path]::GetFullPath($OutFile)
    [IO.File]::WriteAllText($outPath, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}

if (-not $ok) { exit 1 }
