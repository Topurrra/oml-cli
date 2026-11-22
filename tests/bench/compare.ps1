<#
.SYNOPSIS
    Compares a benchmark result module with a reference module, element by element. Read only.

.DESCRIPTION
    Made for the OUI subset task (tasks/oui-subset.md): the reference is the OUI kit's own build of the same blocks and
    screen (tests/bench/oui-reference.py, applied to the start module). Works for any pair of .oml files.

    For both modules: `oml validate --warnings` (errors and warnings) and `oml inspect` (names). Then `oml export` of
    every reference block that matches -Pattern, every reference screen and the same names in the result, and per block:
      inputs     present by name; type, mandatory and default compared (Boolean "" = False, Text "" = "", number "" = 0)
      events     present by name; parameter names compared
      placeholders present by name
    Per screen: present, access (anonymous/registered), and which -Pattern blocks it uses (layout included).
    Structures named in the reference: present by name.

    Names are compared without case, as OutSystems does. Never changes either module.

    The score is the share of reference items found in the result: blocks, inputs (name and type), events,
    placeholders, screens, blocks used by each screen and structures. Defaults, mandatory flags and event parameters
    are listed as differences but not scored.

    Exit codes: 0 = compared (whatever the score); 2 = the comparison could not run.

.EXAMPLE
    pwsh -NoProfile -File tests/bench/compare.ps1 -Oml 'C:\oml\oml.exe' -Reference 'C:\bench\oui-20261123\reference.oml' -Result 'C:\bench\oui-20261123\run-omlcode\omlcode\result.oml' -OutFile 'C:\bench\oui-20261123\run-omlcode\compare.json'
#>
[CmdletBinding()]
param(
    [string]$Oml = 'oml',
    [Parameter(Mandatory)][string]$Reference,
    [Parameter(Mandatory)][string]$Result,
    [string]$Pattern = 'Oui*',
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

function Stop-Compare([string]$Message) {
    Write-Host "ERROR: $Message"
    exit 2
}

$omlPath = if (Test-Path -LiteralPath $Oml -PathType Leaf) { (Resolve-Path -LiteralPath $Oml).Path }
else { (Get-Command -Name $Oml -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source }
if (-not $omlPath) { Stop-Compare "oml not found: $Oml" }
foreach ($f in $Reference, $Result) {
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { Stop-Compare "module not found: $f" }
}
$refPath = (Resolve-Path -LiteralPath $Reference).Path
$resPath = (Resolve-Path -LiteralPath $Result).Path

function Invoke-OmlJson([string[]]$OmlArgs) {
    $out = & $omlPath @OmlArgs 2>$null
    $code = $LASTEXITCODE
    $text = ($out | Out-String).Trim()
    try { $json = $text | ConvertFrom-Json -Depth 100 } catch { $json = $null }
    [pscustomobject]@{ Exit = $code; Json = $json; Text = $text }
}

function Get-Validation([string]$Path) {
    $r = Invoke-OmlJson @('validate', $Path, '--warnings')
    if ($r.Exit -eq 2 -or -not $r.Json -or $null -eq $r.Json.errorCount) { Stop-Compare "oml validate failed on ${Path}: $($r.Text.Substring(0, [Math]::Min(500, $r.Text.Length)))" }
    [pscustomobject]@{ errors = [int]$r.Json.errorCount; warnings = [int]$r.Json.warningCount }
}

function Get-Names([string]$Path) {
    $r = Invoke-OmlJson @('inspect', $Path, '--own', '--only', 'blocks,screens,structures')
    if ($r.Exit -ne 0 -or -not $r.Json) { Stop-Compare "oml inspect failed on $Path" }
    [pscustomobject]@{
        blocks     = @($r.Json.blocks | Where-Object { $_ } | ForEach-Object { $_.name })
        screens    = @($r.Json.screens | Where-Object { $_ } | ForEach-Object { $_.name })
        structures = @($r.Json.structures | Where-Object { $_ } | ForEach-Object { $_.name })
    }
}

# Exports elements in one call; returns a name -> element map (lower-case keys).
function Get-Exports([string]$Path, [string[]]$Names) {
    $map = @{}
    if (-not $Names.Count) { return $map }
    $r = Invoke-OmlJson (@('export', $Path) + $Names)
    if ($r.Exit -ne 0 -or -not $r.Json) { Stop-Compare "oml export failed on ${Path}: $($r.Text.Substring(0, [Math]::Min(500, $r.Text.Length)))" }
    foreach ($e in @($r.Json)) { if ($e.name) { $map[$e.name.ToLowerInvariant()] = $e } }
    $map
}

# Walks an exported widget tree and collects placeholder names and the blocks it uses.
function Get-WidgetFacts($Element) {
    $placeholders = [Collections.Generic.List[string]]::new()
    $blocks = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $stack = [Collections.Generic.Stack[object]]::new()
    $stack.Push($Element)
    while ($stack.Count) {
        $node = $stack.Pop()
        if ($null -eq $node) { continue }
        if ($node -is [Array]) { foreach ($x in $node) { $stack.Push($x) }; continue }
        if ($node -isnot [pscustomobject]) { continue }
        $type = $node.PSObject.Properties['type']
        if ($type -and $type.Value -eq 'Placeholder' -and $node.name) { $placeholders.Add($node.name) }
        foreach ($key in 'sourceBlock', 'layout') {
            $p = $node.PSObject.Properties[$key]
            if ($p -and $p.Value -is [string] -and $p.Value) { [void]$blocks.Add(($p.Value -split '\.')[-1]) }
        }
        foreach ($p in $node.PSObject.Properties) {
            if ($p.Value -is [Array] -or $p.Value -is [pscustomobject]) { $stack.Push($p.Value) }
        }
    }
    [pscustomobject]@{ placeholders = @($placeholders | Select-Object -Unique); blocks = @($blocks) }
}

function Get-Default($Param) {
    $d = [string]$Param.default
    if ($d -ne '') { return $d }
    switch -Regex ([string]$Param.type) {
        '^Boolean$' { 'False' }
        '^(Text|Email|Phone Number)$' { '""' }
        '^(Integer|Long Integer|Decimal|Currency)$' { '0' }
        default { '' }
    }
}

function Find-ByName($Items, [string]$Name) { @($Items) | Where-Object { $_.name -and $_.name -ieq $Name } | Select-Object -First 1 }

function Compare-Block([string]$Name, $Ref, $Res) {
    $refFacts = Get-WidgetFacts $Ref.widgets
    $row = [ordered]@{
        block = $Name; present = [bool]$Res
        inputs = @($Ref.inputs | Where-Object { $_ }).Count; inputsFound = 0; inputsTyped = 0
        events = @($Ref.events | Where-Object { $_ }).Count; eventsFound = 0
        placeholders = $refFacts.placeholders.Count; placeholdersFound = 0
        missing = [Collections.Generic.List[string]]::new(); differences = [Collections.Generic.List[string]]::new(); extra = [Collections.Generic.List[string]]::new()
    }
    if (-not $Res) { $row.missing.Add('the block'); return [pscustomobject]$row }
    foreach ($i in @($Ref.inputs | Where-Object { $_ })) {
        $j = Find-ByName $Res.inputs $i.name
        if (-not $j) { $row.missing.Add("input $($i.name)"); continue }
        $row.inputsFound++
        if ([string]$j.type -ieq [string]$i.type) { $row.inputsTyped++ } else { $row.differences.Add("input $($i.name) type $($j.type), reference $($i.type)") }
        if ([bool]$j.mandatory -ne [bool]$i.mandatory) { $row.differences.Add("input $($i.name) mandatory $([bool]$j.mandatory), reference $([bool]$i.mandatory)") }
        $dr = Get-Default $i; $dj = Get-Default $j
        if ($dr -ine $dj -and -not ($dr -match '^\d+(\.0+)?$' -and $dj -match '^\d+(\.0+)?$' -and [double]$dr -eq [double]$dj)) { $row.differences.Add("input $($i.name) default $dj, reference $dr") }
    }
    foreach ($j in @($Res.inputs | Where-Object { $_ })) { if (-not (Find-ByName $Ref.inputs $j.name)) { $row.extra.Add("input $($j.name)") } }
    foreach ($e in @($Ref.events | Where-Object { $_ })) {
        $f = Find-ByName $Res.events $e.name
        if (-not $f) { $row.missing.Add("event $($e.name)"); continue }
        $row.eventsFound++
        $pr = (@($e.inputs | ForEach-Object { $_.name }) -join ',')
        $pf = (@($f.inputs | ForEach-Object { $_.name }) -join ',')
        if ($pr -ine $pf) { $row.differences.Add("event $($e.name)($pf), reference ($pr)") }
    }
    foreach ($f in @($Res.events | Where-Object { $_ })) { if (-not (Find-ByName $Ref.events $f.name)) { $row.extra.Add("event $($f.name)") } }
    $resFacts = Get-WidgetFacts $Res.widgets
    foreach ($p in $refFacts.placeholders) {
        if ($resFacts.placeholders | Where-Object { $_ -ieq $p }) { $row.placeholdersFound++ } else { $row.missing.Add("placeholder $p") }
    }
    foreach ($p in $resFacts.placeholders) { if (-not ($refFacts.placeholders | Where-Object { $_ -ieq $p })) { $row.extra.Add("placeholder $p") } }
    [pscustomobject]$row
}

function Compare-Screen([string]$Name, $Ref, $Res) {
    $refUses = @((Get-WidgetFacts $Ref).blocks | Where-Object { $_ -like $Pattern } | Sort-Object)
    $row = [ordered]@{ screen = $Name; present = [bool]$Res; blocksUsed = $refUses.Count; blocksUsedFound = 0; info = ''; missing = [Collections.Generic.List[string]]::new(); differences = [Collections.Generic.List[string]]::new() }
    if (-not $Res) { $row.missing.Add('the screen'); return [pscustomobject]$row }
    $resUses = @((Get-WidgetFacts $Res).blocks)
    foreach ($b in $refUses) { if ($resUses | Where-Object { $_ -ieq $b }) { $row.blocksUsedFound++ } else { $row.missing.Add("uses $b") } }
    foreach ($k in 'anonymous', 'registered') {
        if ([bool]$Res.access.$k -ne [bool]$Ref.access.$k) { $row.differences.Add("access $k $([bool]$Res.access.$k), reference $([bool]$Ref.access.$k)") }
    }
    $row.info = "locals $(@($Res.locals | Where-Object { $_ }).Count), reference $(@($Ref.locals | Where-Object { $_ }).Count); screen actions $(@($Res.actions | Where-Object { $_ }).Count), reference $(@($Ref.actions | Where-Object { $_ }).Count)"
    [pscustomobject]$row
}

# ---------- main ----------

$valRef = Get-Validation $refPath
$valRes = Get-Validation $resPath
$namesRef = Get-Names $refPath
$namesRes = Get-Names $resPath

$refBlocks = @($namesRef.blocks | Where-Object { $_ -like $Pattern } | Sort-Object)
$refScreens = @($namesRef.screens | Sort-Object)
$resLookup = @{}
foreach ($n in @(@($namesRes.blocks) + @($namesRes.screens) | Where-Object { $_ })) { $resLookup[$n.ToLowerInvariant()] = $n }

$exRef = Get-Exports $refPath (@($refBlocks) + @($refScreens))
$wanted = @(@($refBlocks) + @($refScreens) | ForEach-Object { $resLookup[$_.ToLowerInvariant()] } | Where-Object { $_ })
$exRes = Get-Exports $resPath $wanted

$blockRows = @(foreach ($b in $refBlocks) { Compare-Block $b $exRef[$b.ToLowerInvariant()] $exRes[$b.ToLowerInvariant()] })
$screenRows = @(foreach ($s in $refScreens) { Compare-Screen $s $exRef[$s.ToLowerInvariant()] $exRes[$s.ToLowerInvariant()] })
$structRows = @(foreach ($s in @($namesRef.structures | Sort-Object)) {
    [pscustomobject]@{ structure = $s; present = [bool]($namesRes.structures | Where-Object { $_ -ieq $s }) }
})
$extraBlocks = @($namesRes.blocks | Where-Object { $_ -like $Pattern } | Where-Object { $n = $_; -not ($refBlocks | Where-Object { $_ -ieq $n }) })

function Sum($Rows, [string]$Field) { $t = 0; foreach ($r in @($Rows)) { $t += [int]$r.$Field }; $t }
$score = [ordered]@{
    blocks       = @{ found = @($blockRows | Where-Object present).Count; of = $blockRows.Count }
    inputs       = @{ found = (Sum $blockRows 'inputsTyped'); of = (Sum $blockRows 'inputs'); byNameOnly = (Sum $blockRows 'inputsFound') }
    events       = @{ found = (Sum $blockRows 'eventsFound'); of = (Sum $blockRows 'events') }
    placeholders = @{ found = (Sum $blockRows 'placeholdersFound'); of = (Sum $blockRows 'placeholders') }
    screens      = @{ found = @($screenRows | Where-Object present).Count; of = $screenRows.Count }
    screenBlocks = @{ found = (Sum $screenRows 'blocksUsedFound'); of = (Sum $screenRows 'blocksUsed') }
    structures   = @{ found = @($structRows | Where-Object present).Count; of = $structRows.Count }
}
$found = 0; $of = 0
foreach ($k in $score.Keys) { $found += $score[$k].found; $of += $score[$k].of }
$percent = if ($of) { [Math]::Round(100.0 * $found / $of, 1) } else { 0 }

Write-Host "Reference: $refPath"
Write-Host "Result:    $resPath"
Write-Host "Validation: result $($valRes.errors) error(s), $($valRes.warnings) warning(s); reference $($valRef.errors) error(s), $($valRef.warnings) warning(s)"
Write-Host ('Score: {0}/{1} reference items ({2}%)' -f $found, $of, $percent)
foreach ($k in $score.Keys) { Write-Host ('  {0,-13} {1}/{2}' -f $k, $score[$k].found, $score[$k].of) }
Write-Host ''
Write-Host ('{0,-16} {1,-7} {2,-9} {3,-7} {4,-12}' -f 'Block', 'Present', 'Inputs', 'Events', 'Placeholders')
foreach ($r in $blockRows) {
    Write-Host ('{0,-16} {1,-7} {2,-9} {3,-7} {4,-12}' -f $r.block, $(if ($r.present) { 'yes' } else { 'NO' }), "$($r.inputsTyped)/$($r.inputs)", "$($r.eventsFound)/$($r.events)", "$($r.placeholdersFound)/$($r.placeholders)")
    if ($r.present -and $r.missing.Count) { Write-Host ('    missing: ' + ($r.missing -join '; ')) }
    if ($r.differences.Count) { Write-Host ('    differs: ' + ($r.differences -join '; ')) }
    if ($r.extra.Count) { Write-Host ('    extra:   ' + ($r.extra -join '; ')) }
}
foreach ($r in $screenRows) {
    Write-Host ''
    Write-Host ("Screen {0}: {1}; uses {2}/{3} reference blocks" -f $r.screen, $(if ($r.present) { 'present' } else { 'MISSING' }), $r.blocksUsedFound, $r.blocksUsed)
    if ($r.present -and $r.missing.Count) { Write-Host ('    missing: ' + ($r.missing -join '; ')) }
    if ($r.differences.Count) { Write-Host ('    differs: ' + ($r.differences -join '; ')) }
    if ($r.info) { Write-Host ('    size:    ' + $r.info) }
}
foreach ($r in $structRows) { Write-Host ("Structure {0}: {1}" -f $r.structure, $(if ($r.present) { 'present' } else { 'MISSING' })) }
if ($extraBlocks.Count) { Write-Host ("Blocks matching '$Pattern' that the reference does not have: " + ($extraBlocks -join ', ')) }

if ($OutFile) {
    $report = [ordered]@{
        reference   = $refPath
        result      = $resPath
        pattern     = $Pattern
        validation  = [ordered]@{ result = $valRes; reference = $valRef }
        score       = [ordered]@{ found = $found; of = $of; percent = $percent; parts = $score }
        blocks      = $blockRows
        screens     = $screenRows
        structures  = $structRows
        extraBlocks = $extraBlocks
    }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutFile), ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}
exit 0
