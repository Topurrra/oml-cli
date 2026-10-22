param([string]$Oml, [string]$Tmp)
# validate: `summary` comes first with the totals; every sample names its module and owner.
$ErrorActionPreference = "Stop"
$src = Join-Path $Tmp "validate-src.oml"; $out = Join-Path $Tmp "validate-out.oml"; $spec = Join-Path $Tmp "validate-spec.json"
& $Oml new $src --name ValSummary --kind CrossDevice | Out-Null
[IO.File]::WriteAllText($spec, '{ "serverActions": [ { "name": "NeverCalled" } ] }')
& $Oml apply $src $spec $out | Out-Null
$raw = & $Oml validate $out --warnings | Out-String
$r = $raw | ConvertFrom-Json
if ($raw.TrimStart() -notmatch '^\{"summary":\{') { throw "validate: summary is not the first field: $($raw.Substring(0, [Math]::Min(120, $raw.Length)))" }
if ($r.summary.errors -ne 0 -or $r.summary.warnings -lt 1 -or $r.summary.warnings -ne $r.warningCount -or $r.summary.module -ne 'ValSummary') { throw "validate: wrong summary: $($r.summary | ConvertTo-Json -Compress)" }
$samples = @($r.warnings | ForEach-Object { $_.samples })
$sample = $samples | Where-Object { $_.owner -like '*NeverCalled*' } | Select-Object -First 1
if ($samples.Where({ $_.module -ne 'ValSummary' }).Count -or -not $sample -or $sample.module -ne 'ValSummary') { throw "validate: sample has no module or owner: $($sample | ConvertTo-Json -Compress)" }
# Without --warnings the totals still count the warnings.
$plain = & $Oml validate $out | ConvertFrom-Json
if ($plain.summary.warnings -ne $r.summary.warnings -or $null -ne $plain.warnings) { throw "validate: totals differ without --warnings" }
Write-Host "ok   validate: summary first, module and owner in samples"
