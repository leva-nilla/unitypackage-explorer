Set-StrictMode -Version Latest

function Remove-RecordedSparsePackage {
    [CmdletBinding()]
    param(
        [string]$PackageName,
        [string]$Publisher,
        [string]$PackageFullName,
        [Parameter(Mandatory = $true)]
        [scriptblock]$GetPackages,
        [Parameter(Mandatory = $true)]
        [scriptblock]$RemovePackage
    )

    $expectedName = 'UnityPackageExplorer.SparseContextMenu'
    if ([string]::IsNullOrWhiteSpace($PackageName) -or $PackageName -ne $expectedName) {
        return [pscustomobject]@{ RecordMatched = $false; Removed = $false }
    }
    if ([string]::IsNullOrWhiteSpace($Publisher) -or [string]::IsNullOrWhiteSpace($PackageFullName)) {
        throw 'The saved legacy sparse package identity is incomplete; no package was removed.'
    }

    $installedPackages = @(& $GetPackages $PackageName)
    $matches = @($installedPackages | Where-Object {
        $_.Name -eq $PackageName -and $_.Publisher -eq $Publisher -and $_.PackageFullName -eq $PackageFullName
    })
    if ($matches.Count -eq 0 -and $installedPackages.Count -gt 0) {
        throw 'An installed package has the recorded package name but a different publisher or full name; it was left untouched.'
    }
    if ($matches.Count -gt 1) {
        throw 'The saved legacy sparse package identity matched multiple packages; none were removed.'
    }

    $removed = $false
    if ($matches.Count -eq 1) {
        & $RemovePackage $PackageFullName
        $remaining = @(& $GetPackages $PackageName | Where-Object {
            $_.Name -eq $PackageName -and $_.Publisher -eq $Publisher -and $_.PackageFullName -eq $PackageFullName
        })
        if ($remaining.Count -gt 0) {
            throw "The legacy sparse package remains installed after removal: $PackageFullName"
        }
        $removed = $true
    }

    return [pscustomobject]@{ RecordMatched = $true; Removed = $removed }
}

function Should-PreserveSendToShortcutOnUpgrade {
    [CmdletBinding()]
    param(
        [ValidateSet('Upgrade', 'Uninstall')]
        [string]$Mode,
        [bool]$SendToTaskSelected,
        [bool]$LinkTargetsCurrentInstall
    )

    return $Mode -eq 'Upgrade' -and $SendToTaskSelected -and $LinkTargetsCurrentInstall
}

function Invoke-RequiredCleanupStep {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$StepName,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    try { & $Action }
    catch { throw "Required cleanup step '$StepName' failed: $($_.Exception.Message)" }
}

function Remove-VerifiedOwnedShortcut {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [scriptblock]$RemoveFile = { param($shortcutPath) Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction Stop }
    )

    Invoke-RequiredCleanupStep -StepName 'verified legacy shortcut removal' -Action {
        & $RemoveFile $Path
    }
}

function Get-OwnedShellCommandCleanupPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string]$RegisteredCommand,
        [Parameter(Mandatory = $true)] [string]$ExpectedCommand,
        [Parameter(Mandatory = $true)] [AllowEmptyString()] [string[]]$ValueNames,
        [Parameter(Mandatory = $true)] [int]$SubKeyCount
    )

    if ($RegisteredCommand -ne $ExpectedCommand -or $ValueNames -notcontains '') { return 'Preserve' }
    if ($SubKeyCount -gt 0 -or @($ValueNames | Where-Object { $_ -ne '' }).Count -gt 0) {
        return 'RemoveDefaultValue'
    }
    return 'RemoveKey'
}

function Should-SkipOwnedFileMigrationForUninstall {
    [CmdletBinding()]
    param(
        [ValidateSet('Upgrade', 'Uninstall')]
        [string]$Mode,
        [bool]$InnoMarkerVerified,
        [bool]$PreviousInnoManifestExists,
        [bool]$LegacyMarkerVerified
    )

    return $Mode -eq 'Uninstall' -and $InnoMarkerVerified -and -not $PreviousInnoManifestExists -and -not $LegacyMarkerVerified
}

function Should-RunLegacyOnlyUninstallMigration {
    [CmdletBinding()]
    param(
        [ValidateSet('Upgrade', 'Uninstall')]
        [string]$Mode,
        [bool]$InnoMarkerVerified,
        [bool]$PreviousInnoManifestExists,
        [bool]$LegacyMarkerVerified
    )

    return $Mode -eq 'Uninstall' -and $InnoMarkerVerified -and -not $PreviousInnoManifestExists -and $LegacyMarkerVerified
}

function Remove-VerifiedLegacyOwnershipMarker {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallDirectory
    )

    $installRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    $markerPath = Join-Path $installRoot '.unitypackage-explorer-install.json'
    if (-not (Test-Path -LiteralPath $installRoot -PathType Container)) { throw "Install directory is missing: $installRoot" }
    $rootItem = Get-Item -LiteralPath $installRoot -Force
    if ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Install directory is a reparse point: $installRoot" }
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { throw "Legacy ownership marker is missing: $markerPath" }
    $markerItem = Get-Item -LiteralPath $markerPath -Force
    if ($markerItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Legacy ownership marker is a reparse point: $markerPath" }

    try { $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "Legacy ownership marker is invalid: $markerPath" }
    if ($marker.Product -ne 'UnityPackageExplorer' -or
        [IO.Path]::GetFullPath([string]$marker.InstallDirectory).TrimEnd('\') -ne $installRoot -or
        $null -eq $marker.OwnedFiles -or $marker.OwnedFiles -is [string]) {
        throw "Legacy ownership marker does not match this installation: $markerPath"
    }

    Remove-Item -LiteralPath $markerPath -Force
}

function Assert-NoReparsePathUnderRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $fullPath = [IO.Path]::GetFullPath($Path)
    $rootPrefix = $rootPath + '\'
    if (-not $fullPath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes the managed root: $Path"
    }
    if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) { throw "Managed root is missing: $rootPath" }
    $rootItem = Get-Item -LiteralPath $rootPath -Force
    if ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Managed root is a reparse point: $rootPath" }

    $relative = if ($fullPath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase)) { '' } else { $fullPath.Substring($rootPrefix.Length) }
    $current = $rootPath
    foreach ($segment in @($relative -split '[\\/]')) {
        if ([string]::IsNullOrWhiteSpace($segment)) { continue }
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current)) { continue }
        $item = Get-Item -LiteralPath $current -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse points are not allowed in managed paths: $current" }
    }
}

function Assert-NoReparseTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $rootPath -PathType Container) -or
        (Get-Item -LiteralPath $rootPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Install tree root is missing or a reparse point: $rootPath"
    }

    $pendingDirectories = [Collections.Generic.Stack[string]]::new()
    $pendingDirectories.Push($rootPath)
    while ($pendingDirectories.Count -gt 0) {
        $directory = $pendingDirectories.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse points are not allowed in the install tree: $($item.FullName)"
            }
            if ($item.PSIsContainer) { $pendingDirectories.Push($item.FullName) }
        }
    }
}

Export-ModuleMember -Function Remove-RecordedSparsePackage, Should-PreserveSendToShortcutOnUpgrade, Invoke-RequiredCleanupStep, Remove-VerifiedOwnedShortcut, Get-OwnedShellCommandCleanupPlan, Should-SkipOwnedFileMigrationForUninstall, Should-RunLegacyOnlyUninstallMigration, Remove-VerifiedLegacyOwnershipMarker, Assert-NoReparsePathUnderRoot, Assert-NoReparseTree
