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

Export-ModuleMember -Function Remove-RecordedSparsePackage, Should-PreserveSendToShortcutOnUpgrade, Invoke-RequiredCleanupStep
