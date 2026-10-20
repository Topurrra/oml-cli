param([string]$Oml, [string]$Tmp)
# oml pull needs a live Service Studio; only the parts that run before connecting are tested here.
$ErrorActionPreference = "Stop"
$existing = Join-Path $Tmp "pull-existing.oml"
Set-Content $existing "x"
$out = & $Oml pull --out $existing 2>&1 | Out-String
if ($LASTEXITCODE -ne 2 -or $out -notmatch "already exists" -or $out -match "Save it manually") { throw "pull did not refuse an existing output cleanly: $out" }
Remove-Item $existing
Write-Host "ok   pull: refuses an existing output before contacting Service Studio"
