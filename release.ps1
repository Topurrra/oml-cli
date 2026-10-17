#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Publish,
    [string]$OutDir = 'dist'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false

function Invoke-Checked {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed (exit $LASTEXITCODE): $($Arguments -join ' ')" }
}

function Assert-CleanTree {
    $changes = @(Invoke-Checked git @('status', '--porcelain=v1', '--untracked-files=all'))
    if ($changes.Count) { throw "Working tree must be clean. Commit or remove these changes first:`n$($changes -join "`n")" }
}

function Assert-TagAbsent {
    $local = @(Invoke-Checked git @('tag', '--list', $tag))
    if ($local.Count) { throw "Local tag $tag already exists." }
    $remote = @(Invoke-Checked git @('ls-remote', '--tags', 'origin', "refs/tags/$tag", "refs/tags/$tag^{}"))
    if ($remote.Count) { throw "Tag $tag already exists on origin." }
}

function Quote-PowerShell([string]$Value) { "'" + $Value.Replace("'", "''") + "'" }

Push-Location $PSScriptRoot
$work = $null
try {
    Get-Command git -ErrorAction Stop | Out-Null
    Assert-CleanTree
    $branch = & git symbolic-ref --quiet --short HEAD
    if ($LASTEXITCODE -ne 0 -or -not $branch) { throw 'Release from a branch; detached HEAD is not supported.' }
    if (-not $Version) {
        [xml]$props = Get-Content -Raw src/Directory.Build.props
        $Version = [string]$props.Project.PropertyGroup.Version
    }
    if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
        throw "Invalid release version: $Version (expected major.minor.patch with an optional prerelease suffix)."
    }
    $tag = "v$Version"
    $origin = & git config --get remote.origin.url
    if ($LASTEXITCODE -ne 0 -or -not $origin) { throw 'No origin remote configured. Configure origin before releasing so remote tags can be checked.' }
    Assert-TagAbsent

    $changelog = Get-Content -Raw CHANGELOG.md
    $section = [regex]::Match($changelog, '(?ms)^## ' + [regex]::Escape($Version) + '[ \t]*\r?\n.*?(?=^## |\z)')
    if (-not $section.Success) { throw "CHANGELOG.md needs a ## $Version section." }
    $ss = if ($env:OML_SERVICE_STUDIO) { $env:OML_SERVICE_STUDIO } else {
        'C:\Program Files\OutSystems\Service Studio 11\Service Studio'
    }
    $ss = (Resolve-Path -LiteralPath $ss).Path
    if (-not (Test-Path -LiteralPath (Join-Path $ss 'OutSystems.Model.V1.dll') -PathType Leaf)) {
        throw "Service Studio 11 not found at $ss. Set OML_SERVICE_STUDIO."
    }
    Get-Command dotnet -ErrorAction Stop | Out-Null
    Get-Command pwsh -ErrorAction Stop | Out-Null
    if ($Publish) { Get-Command gh -ErrorAction Stop | Out-Null }

    $out = [IO.Path]::GetFullPath($OutDir, $PSScriptRoot)
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    # Keep all temporary writes within this worktree, including extracted smoke-test files.
    $work = Join-Path $PSScriptRoot ('tmp/release-' + [guid]::NewGuid().ToString('N'))
    $stage = Join-Path $work 'package'
    $bin = Join-Path $stage 'bin'
    New-Item -ItemType Directory -Force -Path $bin | Out-Null

    $buildArgs = @('-c', 'Release', "-p:SS=$ss", "-p:Version=$Version")
    Invoke-Checked dotnet (@('clean', 'src') + $buildArgs)
    Invoke-Checked dotnet (@('build', 'src') + $buildArgs)
    Invoke-Checked pwsh @('-NoProfile', '-File', 'tests/regress.ps1')
    Invoke-Checked dotnet (@('publish', 'src/OmlCli.csproj') + $buildArgs + @(
        '-r', 'win-x64', '--self-contained', 'false', '-p:UseAppHost=true', '-o', $bin
    ))
    foreach ($required in @('oml.exe', 'oml.dll', 'oml.deps.json', 'oml.runtimeconfig.json', 'OmlCli.Core.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $bin $required) -PathType Leaf)) {
            throw "Publish output is missing $required."
        }
    }
    foreach ($item in @('skill', 'README.md', 'THIRD-PARTY-NOTICES.md', 'licenses', 'CHANGELOG.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $item) -Destination $stage -Recurse
    }
    if (Test-Path -LiteralPath LICENSE -PathType Leaf) { Copy-Item -LiteralPath LICENSE -Destination $stage }
    else { Write-Warning 'No LICENSE file in this repository; continuing without it.' }

    # Names catch dependency copies even if modified; hashes catch renamed copies anywhere in the package.
    $studioFiles = @(Get-ChildItem -LiteralPath $ss -File -Recurse -Force)
    $studioNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $studioFiles) { [void]$studioNames.Add($file.Name) }
    foreach ($file in Get-ChildItem -LiteralPath $bin -File -Recurse -Force) {
        if ($studioNames.Contains($file.Name)) { throw "Service Studio file in CLI output: $($file.FullName)" }
    }
    $packageFiles = @(Get-ChildItem -LiteralPath $stage -File -Recurse -Force)
    $lengths = [Collections.Generic.HashSet[long]]::new()
    foreach ($file in $packageFiles) { [void]$lengths.Add($file.Length) }
    $studioHashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $studioFiles) {
        if ($lengths.Contains($file.Length)) { [void]$studioHashes.Add((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) }
    }
    foreach ($file in $packageFiles) {
        if ($studioHashes.Contains((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)) {
            throw "File copied from Service Studio in package: $($file.FullName)"
        }
    }

    $zipName = "oml-$Version-win-x64.zip"
    $candidate = Join-Path $work $zipName
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $candidate)
    $smoke = Join-Path $work 'smoke'
    Expand-Archive -LiteralPath $candidate -DestinationPath $smoke
    Push-Location $smoke
    try {
        Invoke-Checked (Join-Path $smoke 'bin/oml.exe') @('version')
        Invoke-Checked (Join-Path $smoke 'bin/oml.exe') @('--help')
    }
    finally { Pop-Location }
    Write-Host 'Smoke test passed: extracted bin/oml.exe version and --help (exit 0).'

    $zip = Join-Path $out $zipName
    $checksum = "$zip.sha256"
    $notes = Join-Path $out 'release-notes.md'
    Move-Item -LiteralPath $candidate -Destination $zip -Force
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $checksum -Encoding utf8NoBOM -Value "$hash  $zipName"
    $install = @'
### Install

Requires Windows x64, Service Studio 11 and the .NET 8 runtime (or the newer runtime required by your Service Studio).
Unzip the archive and add its `bin` folder to PATH, keeping all CLI files together.

```powershell
oml version
```

Unsigned executables may be blocked by company policy. Teams can build from source instead; see README.md.
'@
    Set-Content -LiteralPath $notes -Encoding utf8NoBOM -Value ($section.Value.Trim() + "`n`n" + $install)
    Write-Host "Created $zip ($((Get-Item -LiteralPath $zip).Length) bytes), $checksum and $notes"
    Write-Host "Next commands (run from $(Quote-PowerShell $PSScriptRoot)):"
    Write-Host "git tag $tag"
    Write-Host "git push origin $tag"
    Write-Host "gh release create $tag $(Quote-PowerShell $zip) $(Quote-PowerShell $checksum) --title `"oml $Version`" --notes-file $(Quote-PowerShell $notes)"

    if ($Publish) {
        $repoJson = Invoke-Checked gh @('repo', 'view', '--json', 'nameWithOwner')
        $repo = ($repoJson | ConvertFrom-Json).nameWithOwner
        $answer = Read-Host "Create and push tag $tag and publish to $repo with files $zip and $checksum, using notes $notes? Type $tag to confirm"
        if ($answer -cne $tag) { throw 'Publishing cancelled; local artifacts are ready.' }
        Assert-CleanTree
        Assert-TagAbsent
        Invoke-Checked git @('tag', $tag)
        Invoke-Checked git @('push', 'origin', $tag)
        Invoke-Checked gh @('release', 'create', $tag, $zip, $checksum, '--title', "oml $Version", '--notes-file', $notes)
    }
}
finally {
    if ($work -and (Test-Path -LiteralPath $work)) {
        $tempRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'tmp')) + [IO.Path]::DirectorySeparatorChar
        if (-not ([IO.Path]::GetFullPath($work).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase))) {
            throw "Refusing to remove temporary folder outside $tempRoot"
        }
        Remove-Item -LiteralPath $work -Recurse -Force
    }
    Pop-Location
}
