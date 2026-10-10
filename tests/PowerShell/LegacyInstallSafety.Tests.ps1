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

$expectedShellCommand = '"C:\Apps\UnityPackageExplorer.App.exe" "%1"'
Assert-True ((Get-OwnedShellCommandCleanupPlan -RegisteredCommand $expectedShellCommand -ExpectedCommand $expectedShellCommand -ValueNames @('') -SubKeyCount 0) -eq 'RemoveKey') 'A product-only command key should be removed as a whole.'
Assert-True ((Get-OwnedShellCommandCleanupPlan -RegisteredCommand $expectedShellCommand -ExpectedCommand $expectedShellCommand -ValueNames @('', 'DelegateExecute') -SubKeyCount 0) -eq 'RemoveDefaultValue') 'An owned default command should be removed while preserving DelegateExecute.'
Assert-True ((Get-OwnedShellCommandCleanupPlan -RegisteredCommand $expectedShellCommand -ExpectedCommand $expectedShellCommand -ValueNames @('') -SubKeyCount 1) -eq 'RemoveDefaultValue') 'An owned default command should be removed while preserving command subkeys.'
Assert-True ((Get-OwnedShellCommandCleanupPlan -RegisteredCommand '"C:\Other\Other.exe" "%1"' -ExpectedCommand $expectedShellCommand -ValueNames @('', 'DelegateExecute') -SubKeyCount 0) -eq 'Preserve') 'A changed third-party command must be preserved in full.'

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

$verifiedShortcutRemovalFailed = $false
try {
    Remove-VerifiedOwnedShortcut -Path 'mocked-owned-shortcut.lnk' -RemoveFile { param($path) throw "simulated shortcut deletion denial: $path" }
} catch {
    $verifiedShortcutRemovalFailed = $_.Exception.Message -match 'verified legacy shortcut removal.*simulated shortcut deletion denial.*mocked-owned-shortcut.lnk'
}
Assert-True $verifiedShortcutRemovalFailed 'A verified owned shortcut deletion failure was swallowed.'

Assert-True (-not (Should-SkipOwnedFileMigrationForUninstall -Mode Uninstall -InnoMarkerVerified $true -PreviousInnoManifestExists $false -LegacyMarkerVerified $true)) 'Mixed-marker uninstall must not skip the validated legacy-owned files.'
Assert-True (Should-SkipOwnedFileMigrationForUninstall -Mode Uninstall -InnoMarkerVerified $true -PreviousInnoManifestExists $false -LegacyMarkerVerified $false) 'Uninstall without a legacy marker must delegate file removal to Inno.'
Assert-True (-not (Should-SkipOwnedFileMigrationForUninstall -Mode Upgrade -InnoMarkerVerified $true -PreviousInnoManifestExists $false -LegacyMarkerVerified $false)) 'Upgrade must fail closed when the verified Inno file manifest is missing.'
Assert-True (-not (Should-SkipOwnedFileMigrationForUninstall -Mode Uninstall -InnoMarkerVerified $true -PreviousInnoManifestExists $true -LegacyMarkerVerified $false)) 'Uninstall must use the migration when its paired Inno file manifest exists.'
Assert-True (-not (Should-SkipOwnedFileMigrationForUninstall -Mode Uninstall -InnoMarkerVerified $false -PreviousInnoManifestExists $false -LegacyMarkerVerified $false)) 'An unverified install must not skip ownership validation.'
Assert-True (Should-RunLegacyOnlyUninstallMigration -Mode Uninstall -InnoMarkerVerified $true -PreviousInnoManifestExists $false -LegacyMarkerVerified $true) 'Mixed-marker uninstall must process only validated legacy PowerShell-owned paths.'
Assert-True (-not (Should-RunLegacyOnlyUninstallMigration -Mode Upgrade -InnoMarkerVerified $true -PreviousInnoManifestExists $false -LegacyMarkerVerified $true)) 'Legacy-only migration must not run during upgrade.'

$reparseRoot = Join-Path $env:TEMP ('UnityPackageExplorer-reparse-check-' + [Guid]::NewGuid().ToString('N'))
$reparseTarget = Join-Path $env:TEMP ('UnityPackageExplorer-reparse-target-' + [Guid]::NewGuid().ToString('N'))
$previewHandlerRoot = Join-Path $env:TEMP ('UnityPackageExplorer-preview-reparse-check-' + [Guid]::NewGuid().ToString('N'))
$previewHandlerTarget = Join-Path $env:TEMP ('UnityPackageExplorer-preview-reparse-target-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $reparseRoot, $reparseTarget, $previewHandlerRoot, $previewHandlerTarget | Out-Null
try {
    New-Item -ItemType Junction -Path (Join-Path $reparseRoot 'linked') -Target $reparseTarget | Out-Null
    $reparseRejected = $false
    try { Assert-NoReparsePathUnderRoot -Root $reparseRoot -Path (Join-Path $reparseRoot 'linked\unins000.exe') }
    catch { $reparseRejected = $_.Exception.Message -match 'Reparse point' }
    Assert-True $reparseRejected 'The uninstall recovery path accepted a reparse-point component.'

    $previewHandler = Join-Path $previewHandlerRoot 'PreviewHandler'
    New-Item -ItemType Directory -Path $previewHandler | Out-Null
    New-Item -ItemType Junction -Path (Join-Path $previewHandler 'linked') -Target $previewHandlerTarget | Out-Null
    $treeReparseRejected = $false
    try { Assert-NoReparseTree -Root $previewHandlerRoot }
    catch { $treeReparseRejected = $_.Exception.Message -match 'Reparse points are not allowed in the install tree' }
    Assert-True $treeReparseRejected 'The missing-manifest recovery scan accepted a nested PreviewHandler junction.'
} finally {
    if (Test-Path -LiteralPath $reparseRoot) { Remove-Item -LiteralPath $reparseRoot -Recurse -Force }
    if (Test-Path -LiteralPath $reparseTarget) { Remove-Item -LiteralPath $reparseTarget -Recurse -Force }
    if (Test-Path -LiteralPath $previewHandlerRoot) { Remove-Item -LiteralPath $previewHandlerRoot -Recurse -Force }
    if (Test-Path -LiteralPath $previewHandlerTarget) { Remove-Item -LiteralPath $previewHandlerTarget -Recurse -Force }
}

Write-Output 'Legacy install safety tests passed.'
