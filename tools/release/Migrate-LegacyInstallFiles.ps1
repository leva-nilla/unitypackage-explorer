[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory,
    [Parameter(Mandatory = $true)]
    [string]$CurrentFilesManifest,
    [ValidateSet('ValidateOnly', 'Upgrade', 'Uninstall')]
    [string]$Mode = 'ValidateOnly',
    [switch]$LegacyOnlyUninstall
)

$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$legacyMarkerPath = Join-Path $installRoot '.unitypackage-explorer-install.json'
$innoMarkerPath = Join-Path $installRoot '.unitypackage-explorer-inno-install.json'
$previousInnoFilesManifest = Join-Path $installRoot 'Tools\CurrentInstallFiles.txt'

function Assert-NotReparsePath([string]$Path, [switch]$AllowMissingLeaf) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $rootPrefix = $installRoot.TrimEnd('\') + '\'
    if (-not $fullPath.Equals($installRoot, [StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes the managed install directory: $Path"
    }

    if (-not (Test-Path -LiteralPath $installRoot -PathType Container)) { return }
    $root = Get-Item -LiteralPath $installRoot -Force
    if ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Install root is a reparse point: $installRoot" }

    $relative = if ($fullPath.Equals($installRoot, [StringComparison]::OrdinalIgnoreCase)) { '' } else { $fullPath.Substring($rootPrefix.Length) }
    $current = $installRoot
    $parts = @($relative -split '[\\/]')
    for ($index = 0; $index -lt $parts.Count; $index++) {
        if ([string]::IsNullOrWhiteSpace($parts[$index])) { continue }
        $current = Join-Path $current $parts[$index]
        if (-not (Test-Path -LiteralPath $current)) {
            if ($AllowMissingLeaf) { return }
            continue
        }
        $item = Get-Item -LiteralPath $current -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse points are not allowed in managed paths: $current" }
        if ($index -lt ($parts.Count - 1) -and -not $item.PSIsContainer) { throw "Install path component is not a directory: $current" }
    }
}

function ConvertTo-SafeRelativePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Owned file path is empty.' }
    $relative = $Path.Replace('/', '\')
    if ($relative.StartsWith('\') -or $relative.Contains(':') -or [IO.Path]::IsPathRooted($relative)) {
        throw "Owned file path must be relative: $Path"
    }
    $segments = @($relative -split '\\')
    if ($segments.Count -eq 0) {
        throw "Owned file path contains an unsafe path segment: $Path"
    }
    foreach ($segment in $segments) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -in @('.', '..')) {
            throw "Owned file path contains an unsafe path segment: $Path"
        }
        if ($segment.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw "Owned file path contains an invalid name: $Path" }
    }
    return ($segments -join '\')
}

if (-not (Test-Path -LiteralPath $installRoot -PathType Container)) { return }
Assert-NotReparsePath $installRoot
$legacyMarkerExists = Test-Path -LiteralPath $legacyMarkerPath
$innoMarkerExists = Test-Path -LiteralPath $innoMarkerPath
if (-not ($legacyMarkerExists -or $innoMarkerExists)) { return }
if ($LegacyOnlyUninstall -and $Mode -ne 'Uninstall') { throw 'LegacyOnlyUninstall is valid only in Uninstall mode.' }

$legacyMarker = $null
if ($legacyMarkerExists) {
    if (-not (Test-Path -LiteralPath $legacyMarkerPath -PathType Leaf)) { throw "Legacy installation marker is not a regular file: $legacyMarkerPath" }
    Assert-NotReparsePath $legacyMarkerPath
    try { $legacyMarker = Get-Content -LiteralPath $legacyMarkerPath -Raw | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "Legacy installation marker is invalid; legacy files were left untouched: $legacyMarkerPath" }
    if ($legacyMarker.Product -ne 'UnityPackageExplorer' -or
        [IO.Path]::GetFullPath([string]$legacyMarker.InstallDirectory).TrimEnd('\') -ne $installRoot) {
        throw "Legacy installation marker does not match this product and install directory: $legacyMarkerPath"
    }
    if ($null -eq $legacyMarker.OwnedFiles -or $legacyMarker.OwnedFiles -is [string]) {
        throw "Legacy installation marker has no valid OwnedFiles list: $legacyMarkerPath"
    }
}

$innoMarker = $null
$usePreviousInnoManifest = $false
if ($innoMarkerExists) {
    if (-not (Test-Path -LiteralPath $innoMarkerPath -PathType Leaf)) { throw "Inno installation marker is not a regular file: $innoMarkerPath" }
    Assert-NotReparsePath $innoMarkerPath
    try { $innoMarker = Get-Content -LiteralPath $innoMarkerPath -Raw | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "Inno installation marker is invalid; installed files were left untouched: $innoMarkerPath" }
    if ($innoMarker.Product -ne 'UnityPackageExplorer' -or
        [IO.Path]::GetFullPath([string]$innoMarker.InstallDirectory).TrimEnd('\') -ne $installRoot) {
        throw "Inno installation marker does not match this product and install directory: $innoMarkerPath"
    }
    if (-not (Test-Path -LiteralPath $previousInnoFilesManifest -PathType Leaf)) {
        if (Test-Path -LiteralPath $previousInnoFilesManifest) {
            Assert-NotReparsePath $previousInnoFilesManifest
            throw "The manifest paired with the Inno installation marker is not a regular file: $previousInnoFilesManifest"
        }
        if (-not $LegacyOnlyUninstall -or $Mode -ne 'Uninstall' -or -not $legacyMarker) {
            throw "The manifest paired with the verified Inno installation marker is missing: $previousInnoFilesManifest"
        }
    } else {
        Assert-NotReparsePath $previousInnoFilesManifest
        $previousManifestItem = Get-Item -LiteralPath $previousInnoFilesManifest -Force
        if ($previousManifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "The manifest paired with the Inno installation marker is a reparse point: $previousInnoFilesManifest"
        }
        $usePreviousInnoManifest = $true
    }
} elseif ($LegacyOnlyUninstall) {
    throw 'LegacyOnlyUninstall requires a verified Inno marker and a verified legacy PowerShell marker.'
}

$manifest = [IO.Path]::GetFullPath($CurrentFilesManifest)
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw "Current install file list is missing: $manifest" }
$manifestItem = Get-Item -LiteralPath $manifest -Force
if ($manifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Current install file list is a reparse point: $manifest" }
$currentFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in Get-Content -LiteralPath $manifest) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    [void]$currentFiles.Add((ConvertTo-SafeRelativePath $line))
}

$ownedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if ($legacyMarker) {
    foreach ($line in $legacyMarker.OwnedFiles) {
        [void]$ownedFiles.Add((ConvertTo-SafeRelativePath ([string]$line)))
    }
}
if ($innoMarker -and $usePreviousInnoManifest) {
    foreach ($line in Get-Content -LiteralPath $previousInnoFilesManifest) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        [void]$ownedFiles.Add((ConvertTo-SafeRelativePath ([string]$line)))
    }
}

# Preflight every old owned path and every destination that the current installer
# will write before removing anything. Unlisted files are never considered owned.
foreach ($relative in $ownedFiles) {
    $path = [IO.Path]::GetFullPath((Join-Path $installRoot $relative))
    Assert-NotReparsePath $path -AllowMissingLeaf
    if (Test-Path -LiteralPath $path) {
        $item = Get-Item -LiteralPath $path -Force
        if ($item.PSIsContainer) { throw "A legacy-owned file path is now a directory; it was left untouched: $path" }
    }
}
foreach ($relative in $currentFiles) {
    $path = [IO.Path]::GetFullPath((Join-Path $installRoot $relative))
    Assert-NotReparsePath $path -AllowMissingLeaf
    if ((Test-Path -LiteralPath $path) -and -not $ownedFiles.Contains($relative)) {
        throw "The setup would overwrite an existing file not listed as legacy-owned; it was left untouched: $path"
    }
    if (Test-Path -LiteralPath $path -PathType Container) { throw "The setup would overwrite an existing directory; it was left untouched: $path" }
}

if ($Mode -eq 'ValidateOnly') {
    Write-Host 'Legacy install ownership and all current file destinations passed preflight.'
    return
}

$removedFileCount = 0
foreach ($relative in $ownedFiles) {
    if ($currentFiles.Contains($relative)) { continue }
    $path = [IO.Path]::GetFullPath((Join-Path $installRoot $relative))
    Assert-NotReparsePath $path
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $item = Get-Item -LiteralPath $path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to remove a non-regular legacy-owned file: $path"
    }
    Remove-Item -LiteralPath $path -Force
    $removedFileCount++
    $parent = Split-Path -Parent $path
    while ($parent -and $parent.StartsWith($installRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        Assert-NotReparsePath $parent
        if (@(Get-ChildItem -LiteralPath $parent -Force).Count -gt 0) { break }
        $nextParent = Split-Path -Parent $parent
        Remove-Item -LiteralPath $parent -Force
        $parent = $nextParent
    }
}

Write-Host "Removed $removedFileCount obsolete files recorded by verified installation manifests; unlisted files were preserved."
