# Shared helpers for the check scripts. Dot-source it: . (Join-Path $PSScriptRoot 'lib.ps1')

# Removes a folder tree without ever following a link out of it. A directory junction or symlink is deleted as a link
# (its target stays); Remove-Item -Recurse fails on a junction ("cannot be removed because it is not empty") or
# deletes into the target. Safe on a path that does not exist, on a file, and on a link itself.
function Remove-TestFolder([string]$Path) {
  if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return }
  $item = Get-Item -LiteralPath $Path -Force
  if ($item.LinkType -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    # The link itself: a directory link needs Directory.Delete (non-recursive); a file link File.Delete.
    try { if ($item.PSIsContainer) { [IO.Directory]::Delete($item.FullName) } else { [IO.File]::Delete($item.FullName) } }
    catch { & cmd /c rmdir "`"$($item.FullName)`"" 2>&1 | Out-Null }
    return
  }
  if (-not $item.PSIsContainer) { Remove-Item -LiteralPath $item.FullName -Force -ErrorAction SilentlyContinue; return }
  # Links first, deepest first, so the tree below holds none when it is removed.
  foreach ($link in @(Get-ChildItem -LiteralPath $item.FullName -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue | Sort-Object { $_.FullName.Length } -Descending)) {
    Remove-TestFolder $link.FullName
  }
  for ($i = 0; $i -lt 3 -and (Test-Path -LiteralPath $item.FullName); $i++) {
    Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $item.FullName) { Start-Sleep -Milliseconds 300 }
  }
}

# Empties a folder (keeps the folder itself), treating links the same way.
function Clear-TestFolder([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return }
  foreach ($child in @(Get-ChildItem -LiteralPath $Path -Force)) { Remove-TestFolder $child.FullName }
}

# A fresh, empty subfolder of $Parent for one check run, named with a GUID. Returns its full path.
function New-TestFolder([string]$Parent, [string]$Name = 'check') {
  New-Item -ItemType Directory -Force ([IO.Path]::GetFullPath($Parent)) | Out-Null
  $path = Join-Path ([IO.Path]::GetFullPath($Parent)) ("$Name-" + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force $path | Out-Null
  return $path
}

# Removes the folder a check worked in. Set OML_KEEP_TEST_TMP=1 to keep it (screenshots, reports) for a look.
function Remove-TestWorkFolder([string]$Path) {
  if ($env:OML_KEEP_TEST_TMP) { return }
  Remove-TestFolder $Path
}

# A file a check needs (a fixture module such as r0.oml) must exist: fail with a plain message, not an apply error.
function Require-TestFile([string]$Path, [string]$What = 'fixture file') {
  if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Missing $What '$Path'. Run it through tests\regress.ps1, which builds the fixture modules, or build it with 'oml new'."
  }
}
