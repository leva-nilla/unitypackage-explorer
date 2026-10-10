$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$migrator = Join-Path $repoRoot 'tools\release\Migrate-LegacyInstallFiles.ps1'
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
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $installRoot '.unitypackage-explorer-install.json'))) 'Legacy marker remained after successful migration.'

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

    Write-Output 'Legacy install migration tests passed.'
} finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
