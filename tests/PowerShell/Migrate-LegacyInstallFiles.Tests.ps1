$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$migrator = Join-Path $repoRoot 'tools\release\Migrate-LegacyInstallFiles.ps1'
Import-Module (Join-Path $repoRoot 'tools\release\LegacyInstallSafety.psm1') -Force -DisableNameChecking
$testRoot = Join-Path $env:TEMP ('UnityPackageExplorer-migration-test-' + [Guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $testRoot 'Install'
$outsideFile = Join-Path $testRoot 'outside.txt'
$currentFilesManifest = Join-Path $testRoot 'CurrentInstallFiles.txt'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Write-LegacyMarker([string]$Root, [string[]]$OwnedFiles) {
    $marker = @{
        Product = 'UnityPackageExplorer'
        InstallDirectory = $Root
        OwnedFiles = $OwnedFiles
    } | ConvertTo-Json -Depth 3
    Set-Content -LiteralPath (Join-Path $Root '.unitypackage-explorer-install.json') -Value $marker -Encoding UTF8
}

function Write-InnoMarker([string]$Root) {
    $marker = @{
        Product = 'UnityPackageExplorer'
        InstallDirectory = $Root
    } | ConvertTo-Json
    Set-Content -LiteralPath (Join-Path $Root '.unitypackage-explorer-inno-install.json') -Value $marker -Encoding UTF8
}

try {
    New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $installRoot 'PreviewHandler') | Out-Null
    Set-Content -LiteralPath (Join-Path $installRoot 'App.exe') -Value 'old owned app'
    Set-Content -LiteralPath (Join-Path $installRoot 'PreviewHandler\obsolete.dll') -Value 'old owned handler'
    Set-Content -LiteralPath (Join-Path $installRoot 'user-not-owned.txt') -Value 'preserve me'
    Set-Content -LiteralPath $currentFilesManifest -Value 'App.exe'
    Write-LegacyMarker $installRoot @('App.exe', 'PreviewHandler\obsolete.dll')

    & $migrator -InstallDirectory $installRoot -CurrentFilesManifest $currentFilesManifest -Mode Upgrade
    Assert-True (Test-Path -LiteralPath (Join-Path $installRoot 'App.exe')) 'Current app file was removed.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $installRoot 'PreviewHandler\obsolete.dll'))) 'Obsolete owned file remained.'
    Assert-True (Test-Path -LiteralPath (Join-Path $installRoot 'user-not-owned.txt')) 'Unlisted user file was removed.'
    $legacyMarkerPath = Join-Path $installRoot '.unitypackage-explorer-install.json'
    Assert-True (Test-Path -LiteralPath $legacyMarkerPath) 'The migration step removed the legacy marker before registration cleanup completed.'

    $simulatedCleanupFailure = $false
    try {
        Invoke-RequiredCleanupStep -StepName 'registration cleanup retry test' -Action { throw 'simulated registry failure' }
    } catch { $simulatedCleanupFailure = $true }
    Assert-True $simulatedCleanupFailure 'The required cleanup failure was not reported.'
    Assert-True (Test-Path -LiteralPath $legacyMarkerPath) 'The legacy marker was lost after a required cleanup failure.'

    & $migrator -InstallDirectory $installRoot -CurrentFilesManifest $currentFilesManifest -Mode Upgrade
    Invoke-RequiredCleanupStep -StepName 'legacy marker finalization test' -Action {
        Remove-VerifiedLegacyOwnershipMarker -InstallDirectory $installRoot
    }
    Assert-True (-not (Test-Path -LiteralPath $legacyMarkerPath)) 'The verified legacy marker remained after required cleanup succeeded.'

    Set-Content -LiteralPath $outsideFile -Value 'keep outside'
    Write-LegacyMarker $installRoot @('..\outside.txt')
    $rejectedTraversal = $false
    try {
        & $migrator -InstallDirectory $installRoot -CurrentFilesManifest $currentFilesManifest -Mode Upgrade
    } catch { $rejectedTraversal = $true }
    Assert-True $rejectedTraversal 'Traversal in the old ownership manifest was not rejected.'
    Assert-True ((Get-Content -LiteralPath $outsideFile -Raw).Trim() -eq 'keep outside') 'Traversal changed a file outside the install directory.'

    $conflictingManifest = Join-Path $testRoot 'ConflictingCurrentFiles.txt'
    Set-Content -LiteralPath $conflictingManifest -Value 'user-not-owned.txt'
    Write-LegacyMarker $installRoot @('App.exe')
    $rejectedUnknownOverwrite = $false
    try {
        & $migrator -InstallDirectory $installRoot -CurrentFilesManifest $conflictingManifest -Mode ValidateOnly
    } catch { $rejectedUnknownOverwrite = $true }
    Assert-True $rejectedUnknownOverwrite 'A current setup file colliding with an unowned file was not rejected.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $installRoot 'user-not-owned.txt') -Raw).Trim() -eq 'preserve me') 'Preflight modified an unowned file.'

    $junctionTarget = Join-Path $testRoot 'JunctionTarget'
    New-Item -ItemType Directory -Force -Path $junctionTarget | Out-Null
    Set-Content -LiteralPath (Join-Path $junctionTarget 'protected.txt') -Value 'keep junction target'
    New-Item -ItemType Junction -Path (Join-Path $installRoot 'linked') -Target $junctionTarget | Out-Null
    Write-LegacyMarker $installRoot @('linked\protected.txt')
    $rejectedReparse = $false
    try {
        & $migrator -InstallDirectory $installRoot -CurrentFilesManifest $currentFilesManifest -Mode Upgrade
    } catch { $rejectedReparse = $true }
    Assert-True $rejectedReparse 'A reparse point in an owned path was not rejected.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $junctionTarget 'protected.txt') -Raw).Trim() -eq 'keep junction target') 'A file reached through a junction was changed.'

    $innoRoot = Join-Path $testRoot 'InnoInstall'
    $innoTools = Join-Path $innoRoot 'Tools'
    New-Item -ItemType Directory -Force -Path $innoTools | Out-Null
    $innoOwnedRuntime = 'mscordaccore_amd64_amd64_10.0.1226.42308.dll'
    Set-Content -LiteralPath (Join-Path $innoRoot $innoOwnedRuntime) -Value 'owned by previous Inno setup'
    Set-Content -LiteralPath (Join-Path $innoRoot 'obsolete-previous-runtime.dll') -Value 'obsolete owned file'
    Set-Content -LiteralPath (Join-Path $innoRoot 'user-file.txt') -Value 'preserve unknown file'
    Set-Content -LiteralPath (Join-Path $innoTools 'CurrentInstallFiles.txt') -Value @(
        $innoOwnedRuntime,
        'obsolete-previous-runtime.dll',
        'Tools\CurrentInstallFiles.txt'
    )
    Write-InnoMarker $innoRoot
    $innoCurrentManifest = Join-Path $testRoot 'InnoCurrentFiles.txt'
    Set-Content -LiteralPath $innoCurrentManifest -Value @(
        $innoOwnedRuntime,
        'UnityPackageExplorer.App.exe',
        'Tools\CurrentInstallFiles.txt'
    )

    & $migrator -InstallDirectory $innoRoot -CurrentFilesManifest $innoCurrentManifest -Mode ValidateOnly
    Assert-True (Test-Path -LiteralPath (Join-Path $innoRoot $innoOwnedRuntime)) 'A file listed in the verified previous Inno manifest did not pass update preflight.'

    $innoUnknownConflictManifest = Join-Path $testRoot 'InnoUnknownConflict.txt'
    Set-Content -LiteralPath $innoUnknownConflictManifest -Value 'user-file.txt'
    $rejectedInnoUnknownOverwrite = $false
    try {
        & $migrator -InstallDirectory $innoRoot -CurrentFilesManifest $innoUnknownConflictManifest -Mode ValidateOnly
    } catch { $rejectedInnoUnknownOverwrite = $true }
    Assert-True $rejectedInnoUnknownOverwrite 'An unrecorded file was treated as Inno-owned.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $innoRoot 'user-file.txt') -Raw).Trim() -eq 'preserve unknown file') 'Inno preflight modified an unrecorded file.'

    & $migrator -InstallDirectory $innoRoot -CurrentFilesManifest $innoCurrentManifest -Mode Upgrade
    Assert-True (Test-Path -LiteralPath (Join-Path $innoRoot $innoOwnedRuntime)) 'A currently required Inno-owned runtime file was removed.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $innoRoot 'obsolete-previous-runtime.dll'))) 'An obsolete file from the verified Inno manifest remained.'
    Assert-True (Test-Path -LiteralPath (Join-Path $innoRoot 'user-file.txt')) 'An unrecorded user file was removed during Inno migration.'
    Assert-True (Test-Path -LiteralPath (Join-Path $innoRoot '.unitypackage-explorer-inno-install.json')) 'The Inno ownership marker was removed during upgrade.'

    $wrongInnoMarker = @{
        Product = 'UnityPackageExplorer'
        InstallDirectory = $testRoot
    } | ConvertTo-Json
    Set-Content -LiteralPath (Join-Path $innoRoot '.unitypackage-explorer-inno-install.json') -Value $wrongInnoMarker -Encoding UTF8
    $rejectedInnoMarkerMismatch = $false
    try {
        & $migrator -InstallDirectory $innoRoot -CurrentFilesManifest $innoCurrentManifest -Mode ValidateOnly
    } catch { $rejectedInnoMarkerMismatch = $true }
    Assert-True $rejectedInnoMarkerMismatch 'An Inno marker for another install path was accepted.'
    Write-InnoMarker $innoRoot

    $innoTraversalManifest = Join-Path $innoTools 'CurrentInstallFiles.txt'
    Set-Content -LiteralPath $innoTraversalManifest -Value '..\outside.txt'
    $rejectedInnoTraversal = $false
    try {
        & $migrator -InstallDirectory $innoRoot -CurrentFilesManifest $innoCurrentManifest -Mode Upgrade
    } catch { $rejectedInnoTraversal = $true }
    Assert-True $rejectedInnoTraversal 'Traversal in the previous Inno manifest was not rejected.'
    Assert-True ((Get-Content -LiteralPath $outsideFile -Raw).Trim() -eq 'keep outside') 'The previous Inno manifest traversal changed a file outside the install directory.'

    $missingManifestRoot = Join-Path $testRoot 'InnoInstallWithoutManifest'
    New-Item -ItemType Directory -Force -Path (Join-Path $missingManifestRoot 'Tools') | Out-Null
    Set-Content -LiteralPath (Join-Path $missingManifestRoot 'runtime.dll') -Value 'must remain untouched'
    Set-Content -LiteralPath (Join-Path $missingManifestRoot 'legacy-owned.dll') -Value 'PS-owned file must remain for recovery'
    Write-LegacyMarker $missingManifestRoot @('legacy-owned.dll')
    Write-InnoMarker $missingManifestRoot
    $rejectedMissingInnoManifest = $false
    try {
        & $migrator -InstallDirectory $missingManifestRoot -CurrentFilesManifest $innoCurrentManifest -Mode ValidateOnly
    } catch { $rejectedMissingInnoManifest = $_.Exception.Message -match 'manifest paired with the verified Inno installation marker is missing' }
    Assert-True $rejectedMissingInnoManifest 'An Inno installation without its paired previous manifest was not rejected explicitly.'
    Assert-True ((Get-Content -LiteralPath (Join-Path $missingManifestRoot 'runtime.dll') -Raw).Trim() -eq 'must remain untouched') 'A file was modified when the Inno ownership manifest was missing.'
    Assert-True (Test-Path -LiteralPath (Join-Path $missingManifestRoot 'legacy-owned.dll')) 'A PS-owned file was removed without the missing Inno ownership manifest.'
    Assert-True (Test-Path -LiteralPath (Join-Path $missingManifestRoot '.unitypackage-explorer-install.json')) 'The PS ownership marker was removed from the mixed-marker recovery state.'

    $mixedUninstallRoot = Join-Path $testRoot 'MixedInnoAndLegacyUninstall'
    New-Item -ItemType Directory -Force -Path (Join-Path $mixedUninstallRoot 'Tools') | Out-Null
    Set-Content -LiteralPath (Join-Path $mixedUninstallRoot 'legacy-ps-only.dll') -Value 'recorded by PowerShell installer'
    Set-Content -LiteralPath (Join-Path $mixedUninstallRoot 'inno-owned-only.dll') -Value 'left to Inno uninstall log'
    Set-Content -LiteralPath (Join-Path $mixedUninstallRoot 'user-file.txt') -Value 'never touched'
    Write-LegacyMarker $mixedUninstallRoot @('legacy-ps-only.dll')
    Write-InnoMarker $mixedUninstallRoot
    $emptyUninstallManifest = Join-Path $testRoot 'EmptyUninstallManifest.txt'
    Set-Content -LiteralPath $emptyUninstallManifest -Value ''

    & $migrator -InstallDirectory $mixedUninstallRoot -CurrentFilesManifest $emptyUninstallManifest -Mode Uninstall -LegacyOnlyUninstall
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $mixedUninstallRoot 'legacy-ps-only.dll'))) 'Mixed uninstall did not remove the validated PS-owned file.'
    Assert-True (Test-Path -LiteralPath (Join-Path $mixedUninstallRoot 'inno-owned-only.dll')) 'Mixed uninstall removed a file delegated to the Inno log.'
    Assert-True (Test-Path -LiteralPath (Join-Path $mixedUninstallRoot 'user-file.txt')) 'Mixed uninstall removed an unrecorded user file.'
    $mixedLegacyMarkerPath = Join-Path $mixedUninstallRoot '.unitypackage-explorer-install.json'
    Assert-True (Test-Path -LiteralPath $mixedLegacyMarkerPath) 'Mixed uninstall removed the PS marker before registration cleanup succeeded.'

    $mixedCleanupFailure = $false
    try {
        Invoke-RequiredCleanupStep -StepName 'mixed uninstall registration cleanup' -Action { throw 'simulated registration failure' }
    } catch { $mixedCleanupFailure = $true }
    Assert-True $mixedCleanupFailure 'The simulated mixed uninstall cleanup failure was not propagated.'
    Assert-True (Test-Path -LiteralPath $mixedLegacyMarkerPath) 'The mixed uninstall lost its PS marker after a cleanup failure.'

    & $migrator -InstallDirectory $mixedUninstallRoot -CurrentFilesManifest $emptyUninstallManifest -Mode Uninstall -LegacyOnlyUninstall
    Invoke-RequiredCleanupStep -StepName 'mixed uninstall marker finalization' -Action {
        Remove-VerifiedLegacyOwnershipMarker -InstallDirectory $mixedUninstallRoot
    }
    Assert-True (-not (Test-Path -LiteralPath $mixedLegacyMarkerPath)) 'The mixed uninstall marker remained after registration cleanup succeeded.'
    Assert-True (Test-Path -LiteralPath (Join-Path $mixedUninstallRoot 'inno-owned-only.dll')) 'Legacy marker finalization removed an Inno-owned file.'
    Assert-True (Test-Path -LiteralPath (Join-Path $mixedUninstallRoot 'user-file.txt')) 'Legacy marker finalization removed an unrecorded file.'

    $reparseMarkerRoot = Join-Path $testRoot 'InnoMarkerReparse'
    $reparseMarkerTarget = Join-Path $testRoot 'InnoMarkerTarget'
    New-Item -ItemType Directory -Force -Path (Join-Path $reparseMarkerRoot 'Tools'), $reparseMarkerTarget | Out-Null
    New-Item -ItemType Junction -Path (Join-Path $reparseMarkerRoot '.unitypackage-explorer-inno-install.json') -Target $reparseMarkerTarget | Out-Null
    $rejectedInnoMarkerReparse = $false
    try {
        & $migrator -InstallDirectory $reparseMarkerRoot -CurrentFilesManifest $innoCurrentManifest -Mode ValidateOnly
    } catch { $rejectedInnoMarkerReparse = $true }
    Assert-True $rejectedInnoMarkerReparse 'An Inno ownership marker reparse point was accepted.'

    Write-Output 'Legacy install migration tests passed.'
} finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
