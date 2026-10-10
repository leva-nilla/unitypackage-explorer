$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Import-Module (Join-Path $repoRoot 'tools\release\LegacyInstallSafety.psm1') -Force -DisableNameChecking

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$mockState = [pscustomobject]@{
  Package = [pscustomobject]@{
    Name = 'UnityPackageExplorer.SparseContextMenu'
    Publisher = 'CN=UnityPackage Explorer'
    PackageFullName = 'UnityPackageExplorer.SparseContextMenu_1.0.0.0_x64__abc'
    Installed = $true
  }
  RemoveCalls = 0
  OtherPackage = $null
}
$package = $mockState.Package
$getPackages = { param($name) if ($mockState.Package.Installed -and $mockState.Package.Name -eq $name) { $mockState.Package } }
$removePackage = { param($fullName) if ($fullName -ne $mockState.Package.PackageFullName) { throw 'Wrong package identity.' }; $mockState.RemoveCalls++; $mockState.Package.Installed = $false }

$result = Remove-RecordedSparsePackage -PackageName $package.Name -Publisher $package.Publisher `
    -PackageFullName $package.PackageFullName -GetPackages $getPackages -RemovePackage $removePackage
Assert-True $result.RecordMatched 'The exact saved identity was not recognized.'
Assert-True $result.Removed 'The exact saved package was not removed.'
Assert-True ($mockState.RemoveCalls -eq 1) 'The remove callback was not called exactly once.'

$mockState.OtherPackage = [pscustomobject]@{ Name = $package.Name; Publisher = 'CN=Other'; PackageFullName = $package.PackageFullName }
$wrongPublisherGet = { param($name) $mockState.OtherPackage }
$wrongPublisherRemove = { param($fullName) throw 'Must not remove a package with a different publisher.' }
$identityMismatchRejected = $false
try {
    Remove-RecordedSparsePackage -PackageName $package.Name -Publisher $package.Publisher `
        -PackageFullName $package.PackageFullName -GetPackages $wrongPublisherGet -RemovePackage $wrongPublisherRemove
} catch { $identityMismatchRejected = $_.Exception.Message -match 'different publisher or full name' }
Assert-True $identityMismatchRejected 'A same-name package with another identity was not rejected.'

$removalFailed = $false
try {
    Remove-RecordedSparsePackage -PackageName $package.Name -Publisher $package.Publisher `
        -PackageFullName $package.PackageFullName -GetPackages { param($name) $mockState.Package } `
        -RemovePackage { param($fullName) throw 'simulated removal denial' }
} catch { $removalFailed = $_.Exception.Message -match 'simulated removal denial' }
Assert-True $removalFailed 'A sparse package removal failure was swallowed.'

Assert-True (Should-PreserveSendToShortcutOnUpgrade -Mode Upgrade -SendToTaskSelected $true -LinkTargetsCurrentInstall $true) 'A selected Send To task should retain the newly installed owned shortcut.'
Assert-True (-not (Should-PreserveSendToShortcutOnUpgrade -Mode Upgrade -SendToTaskSelected $false -LinkTargetsCurrentInstall $true)) 'An unselected Send To task must remove the old owned shortcut.'
Assert-True (-not (Should-PreserveSendToShortcutOnUpgrade -Mode Uninstall -SendToTaskSelected $true -LinkTargetsCurrentInstall $true)) 'Uninstall must not retain the shortcut.'
Assert-True (-not (Should-PreserveSendToShortcutOnUpgrade -Mode Upgrade -SendToTaskSelected $true -LinkTargetsCurrentInstall $false)) 'A shortcut to another target must not be retained as ours.'

$requiredCleanupFailed = $false
try { Invoke-RequiredCleanupStep -StepName 'legacy migration' -Action { throw 'simulated migration failure' } }
catch { $requiredCleanupFailed = $_.Exception.Message -match 'legacy migration.*simulated migration failure' }
Assert-True $requiredCleanupFailed 'A required cleanup failure was swallowed.'

$registrationCleanupFailed = $false
try {
    Invoke-RequiredCleanupStep -StepName 'legacy PreviewHandler and shell registration cleanup' -Action {
        throw 'simulated registry access denial'
    }
} catch {
    $registrationCleanupFailed = $_.Exception.Message -match 'legacy PreviewHandler and shell registration cleanup.*simulated registry access denial'
}
Assert-True $registrationCleanupFailed 'A required legacy registration cleanup failure was swallowed.'

Write-Output 'Legacy install safety tests passed.'
