[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory,
    [string]$CurrentFilesManifest,
    [string]$MigrationScriptPath,
    [ValidateSet('Upgrade', 'Uninstall')]
    [string]$Mode = 'Upgrade',
    [switch]$SparsePackageOnly,
    [string]$SparsePackageErrorLog,
    [switch]$KeepSendToOnUpgrade
)

$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$appPath = [IO.Path]::GetFullPath((Join-Path $installRoot 'UnityPackageExplorer.App.exe'))
$reg = [Microsoft.Win32.Registry]::CurrentUser
$classes = 'Software\Classes'
$classId = '{C24F7D7D-2C91-4AA9-969D-FADE6F9A17D2}'
$previewCategory = '{8895b1c6-b41f-4c1c-a562-0d564250836f}'
$expectedServer = [IO.Path]::GetFullPath((Join-Path $installRoot 'PreviewHandler\UnityPackageExplorer.PreviewHandler.comhost.dll'))
$expectedCommand = '"{0}" "%1"' -f $appPath
$sparsePackageName = 'UnityPackageExplorer.SparseContextMenu'
$safetyModule = Join-Path $PSScriptRoot 'LegacyInstallSafety.psm1'
if (-not (Test-Path -LiteralPath $safetyModule -PathType Leaf)) {
    throw "Legacy install safety module is missing: $safetyModule"
}
Import-Module -Name $safetyModule -Force -DisableNameChecking
$legacyMarkerPath = Join-Path $installRoot '.unitypackage-explorer-install.json'
$legacyAppPath = ''
$legacySendToPath = ''
$legacyStartMenuPath = ''
$legacyMarkerVerified = $false
$innoMarkerVerified = $false

function Write-SparsePackageFailure([string]$Message) {
    if ([string]::IsNullOrWhiteSpace($SparsePackageErrorLog)) { return }
    try { Set-Content -LiteralPath $SparsePackageErrorLog -Value $Message -Encoding UTF8 -Force }
    catch { Write-Warning "Could not save the sparse package failure details: $_" }
}

function Invoke-StrictSparsePackageRemoval {
    $state = $reg.OpenSubKey('Software\UnityPackageExplorer\Install')
    $storedName = if ($state) { [string]$state.GetValue('SparsePackageName') } else { '' }
    $storedPublisher = if ($state) { [string]$state.GetValue('SparsePackagePublisher') } else { '' }
    $storedFullName = if ($state) { [string]$state.GetValue('SparsePackageFullName') } else { '' }
    if ($state) { $state.Dispose() }

    try {
        $result = Remove-RecordedSparsePackage -PackageName $storedName -Publisher $storedPublisher -PackageFullName $storedFullName `
            -GetPackages { param($name) Get-AppxPackage -Name $name -ErrorAction Stop } `
            -RemovePackage { param($fullName) Remove-AppxPackage -Package $fullName -ErrorAction Stop }
    } catch {
        $failure = "Could not safely remove the recorded UnityPackage Explorer sparse package '$storedFullName': $($_.Exception.Message)"
        Write-SparsePackageFailure $failure
        throw $failure
    }

    if ($result.RecordMatched) {
        $state = $reg.OpenSubKey('Software\UnityPackageExplorer\Install', $true)
        if ($state) {
            foreach ($valueName in @(
                'SparsePackageName',
                'SparsePackagePublisher',
                'SparsePackageFullName',
                'SparseExternalLocation',
                'SparsePackageCertificateThumbprint'
            )) { $state.DeleteValue($valueName, $false) }
            $state.Dispose()
        }
        if ($result.Removed) { Write-Host "Removed the exact legacy sparse package: $storedFullName" }
    }
}

Invoke-StrictSparsePackageRemoval
if ($SparsePackageOnly) { return }

$legacyState = $reg.OpenSubKey('Software\UnityPackageExplorer\Install')
$legacyApplicationInstallDirectory = ''
$legacyOwnershipMarkerPath = ''
if ($legacyState) {
    $legacyAppPath = [string]$legacyState.GetValue('AppPath')
    $legacySendToPath = [string]$legacyState.GetValue('SendToShortcutPath')
    $legacyStartMenuPath = [string]$legacyState.GetValue('StartMenuShortcutPath')
    $legacyState.Dispose()
}
$legacyApplicationState = $reg.OpenSubKey('Software\UnityPackageExplorer\ApplicationInstall')
if ($legacyApplicationState) {
    $legacyApplicationInstallDirectory = [string]$legacyApplicationState.GetValue('InstallDirectory')
    $legacyOwnershipMarkerPath = [string]$legacyApplicationState.GetValue('OwnershipMarkerPath')
    $legacyApplicationState.Dispose()
}
if (Test-Path -LiteralPath $legacyMarkerPath -PathType Leaf) {
    try {
        $markerFile = Get-Item -LiteralPath $legacyMarkerPath -Force
        if ($markerFile.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Legacy marker is a reparse point.' }
        $marker = Get-Content -LiteralPath $legacyMarkerPath -Raw | ConvertFrom-Json -ErrorAction Stop
        $legacyMarkerVerified = $marker.Product -eq 'UnityPackageExplorer' -and
            [IO.Path]::GetFullPath([string]$marker.InstallDirectory).TrimEnd('\') -eq $installRoot -and
            $null -ne $marker.OwnedFiles -and $marker.OwnedFiles -isnot [string]
    } catch { }
}
$innoMarkerPath = Join-Path $installRoot '.unitypackage-explorer-inno-install.json'
if (Test-Path -LiteralPath $innoMarkerPath -PathType Leaf) {
    try {
        $markerFile = Get-Item -LiteralPath $innoMarkerPath -Force
        if (-not ($markerFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            $marker = Get-Content -LiteralPath $innoMarkerPath -Raw | ConvertFrom-Json -ErrorAction Stop
            $innoMarkerVerified = $marker.Product -eq 'UnityPackageExplorer' -and
                [IO.Path]::GetFullPath([string]$marker.InstallDirectory).TrimEnd('\') -eq $installRoot
        }
    } catch { }
}

if ($Mode -eq 'Uninstall' -and -not ($legacyMarkerVerified -or $innoMarkerVerified)) {
    throw 'The installation ownership marker could not be verified; uninstall cleanup was stopped to avoid leaving broken registrations.'
}

if ([string]::IsNullOrWhiteSpace($CurrentFilesManifest)) {
    $CurrentFilesManifest = Join-Path $installRoot 'Tools\CurrentInstallFiles.txt'
}
$migrationScript = if ([string]::IsNullOrWhiteSpace($MigrationScriptPath)) {
    Join-Path $installRoot 'Tools\Migrate-LegacyInstallFiles.ps1'
} else {
    [IO.Path]::GetFullPath($MigrationScriptPath)
}
$legacyFilesMigrated = $false
if (Test-Path -LiteralPath $legacyMarkerPath) {
    if (-not $legacyMarkerVerified) {
        throw "The legacy ownership marker is invalid; cleanup was stopped: $legacyMarkerPath"
    }
    if (-not (Test-Path -LiteralPath $migrationScript -PathType Leaf)) {
        throw "The required legacy migration helper is missing: $migrationScript"
    }
    if (-not (Test-Path -LiteralPath $CurrentFilesManifest -PathType Leaf)) {
        throw "The required current file manifest is missing: $CurrentFilesManifest"
    }
    try {
        Invoke-RequiredCleanupStep -StepName 'legacy-owned file migration' -Action {
            & $migrationScript -InstallDirectory $installRoot -CurrentFilesManifest $CurrentFilesManifest -Mode $Mode
        }
        $legacyFilesMigrated = $true
    } catch {
        throw "Legacy file migration did not complete; uninstall or upgrade cleanup was stopped: $($_.Exception.Message)"
    }
}
if ($legacyFilesMigrated -and $legacyMarkerVerified -and
    $legacyApplicationInstallDirectory -and $legacyOwnershipMarkerPath -and
    [IO.Path]::GetFullPath($legacyApplicationInstallDirectory).TrimEnd('\') -eq $installRoot -and
    [IO.Path]::GetFullPath($legacyOwnershipMarkerPath) -eq $legacyMarkerPath) {
    $legacyApplicationState = $reg.OpenSubKey('Software\UnityPackageExplorer\ApplicationInstall', $true)
    if ($legacyApplicationState) {
        $legacyApplicationState.DeleteValue('InstallDirectory', $false)
        $legacyApplicationState.DeleteValue('OwnershipMarkerPath', $false)
        $legacyApplicationState.Dispose()
    }
}

function Remove-Verified-Legacy-Shortcut([string]$RecordedPath, [string]$ExpectedPath, [string]$ExpectedTarget, [switch]$IsSendTo) {
    if (-not ($legacyMarkerVerified -or $innoMarkerVerified) -or [string]::IsNullOrWhiteSpace($RecordedPath) -or
        [string]::IsNullOrWhiteSpace($ExpectedTarget)) { return }
    try {
        if ([IO.Path]::GetFullPath($RecordedPath) -ne [IO.Path]::GetFullPath($ExpectedPath)) { return }
        if (-not (Test-Path -LiteralPath $ExpectedPath -PathType Leaf)) { return }
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($ExpectedPath)
        $target = if ($shortcut.TargetPath) { [IO.Path]::GetFullPath($shortcut.TargetPath) } else { '' }
        if (-not $target.Equals([IO.Path]::GetFullPath($ExpectedTarget), [StringComparison]::OrdinalIgnoreCase)) { return }

        $targetsCurrentInstall = $target.Equals($appPath, [StringComparison]::OrdinalIgnoreCase)
        if ($IsSendTo -and (Should-PreserveSendToShortcutOnUpgrade -Mode $Mode `
            -SendToTaskSelected ([bool]$KeepSendToOnUpgrade) -LinkTargetsCurrentInstall $targetsCurrentInstall)) { return }
        Remove-Item -LiteralPath $ExpectedPath -Force
    } catch {
        Write-Warning "Could not inspect legacy shortcut '$RecordedPath'; it was left untouched: $_"
    }
}

$legacyExpectedApp = Join-Path $installRoot 'UnityPackageExplorer.App.exe'
if (($legacyMarkerVerified -or $innoMarkerVerified) -and $legacyAppPath -and
    [IO.Path]::GetFullPath($legacyAppPath).Equals([IO.Path]::GetFullPath($legacyExpectedApp), [StringComparison]::OrdinalIgnoreCase)) {
    $expectedSendTo = Join-Path ([Environment]::GetFolderPath('SendTo')) 'UnityPackage Explorer.lnk'
    $expectedStartMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'UnityPackage Explorer.lnk'
    Remove-Verified-Legacy-Shortcut $legacySendToPath $expectedSendTo $legacyExpectedApp -IsSendTo
    Remove-Verified-Legacy-Shortcut $legacyStartMenuPath $expectedStartMenu $legacyExpectedApp

    if ($Mode -eq 'Upgrade') {
        $state = $reg.OpenSubKey('Software\UnityPackageExplorer\Install', $true)
        if ($state) {
            $state.DeleteValue('SendToShortcutPath', $false)
            $state.DeleteValue('StartMenuShortcutPath', $false)
            $state.Dispose()
        }
    }
}

function Remove-Key-If-Empty([string]$Path) {
    $key = $reg.OpenSubKey($Path)
    if (-not $key) { return }
    $isEmpty = $key.GetSubKeyNames().Count -eq 0 -and $key.GetValueNames().Count -eq 0
    $key.Dispose()
    if ($isEmpty) { $reg.DeleteSubKey($Path, $false) }
}

function Remove-Owned-Preview-Association([string]$Path) {
    $key = $reg.OpenSubKey($Path, $true)
    if (-not $key) { return }
    $registeredClass = [string]$key.GetValue('')
    if ($registeredClass -eq $classId) { $key.DeleteValue('', $false) }
    $key.Dispose()
    Remove-Key-If-Empty $Path
}

function Remove-Owned-Shell-Command([string]$VerbPath) {
    $commandPath = "$VerbPath\command"
    $command = $reg.OpenSubKey($commandPath)
    if (-not $command) { return }
    $registeredCommand = [string]$command.GetValue('')
    $canRemoveCommand = $registeredCommand -eq $expectedCommand -and $command.GetSubKeyNames().Count -eq 0
    $command.Dispose()
    if (-not $canRemoveCommand) { return }

    $reg.DeleteSubKey($commandPath, $false)
    $verb = $reg.OpenSubKey($VerbPath, $true)
    if (-not $verb) { return }
    $label = [string]$verb.GetValue('')
    if ($label -in @('UnityPackage Explorer で開く', 'UnityPackage Explorer でプレビュー', 'UnityPackage Explorerで開く')) {
        $verb.DeleteValue('', $false)
    }
    if ([string]$verb.GetValue('Icon') -eq ('"' + $appPath + '",0')) { $verb.DeleteValue('Icon', $false) }
    $verb.Dispose()
    Remove-Key-If-Empty $VerbPath
}

function Get-OldPreviewAssociation([string]$Extension) {
    foreach ($path in @(
        "$classes\$Extension\shellex\$previewCategory",
        "$classes\SystemFileAssociations\$Extension\shellex\$previewCategory"
    )) { Remove-Owned-Preview-Association $path }
}

Invoke-RequiredCleanupStep -StepName 'legacy PreviewHandler and shell registration cleanup' -Action {
    $classKeyPath = "$classes\CLSID\$classId"
    $serverKey = $reg.OpenSubKey("$classKeyPath\InProcServer32")
    $registeredServer = if ($serverKey) { [string]$serverKey.GetValue('') } else { '' }
    if ($serverKey) { $serverKey.Dispose() }
    $ownsPreviewHandler = $false
    if (-not [string]::IsNullOrWhiteSpace($registeredServer)) {
        try { $ownsPreviewHandler = [IO.Path]::GetFullPath($registeredServer) -eq $expectedServer }
        catch { throw "The registered PreviewHandler server path could not be verified: $registeredServer" }
    }

    if ($ownsPreviewHandler) {
        foreach ($extension in @('.unitypackage', '.prefab', '.zip')) { Get-OldPreviewAssociation $extension }

        $state = $reg.OpenSubKey('Software\UnityPackageExplorer\Install')
        if ($state) {
            foreach ($valueName in @('UnityPackageProgId', 'PrefabProgId', 'ZipProgId')) {
                $progId = [string]$state.GetValue($valueName)
                if ([string]::IsNullOrWhiteSpace($progId)) { continue }
                Remove-Owned-Preview-Association "$classes\$progId\shellex\$previewCategory"
                $progIdClassPath = "$classes\$progId\CLSID"
                $progIdClass = $reg.OpenSubKey($progIdClassPath)
                $registeredClass = if ($progIdClass) { [string]$progIdClass.GetValue('') } else { '' }
                if ($progIdClass) { $progIdClass.Dispose() }
                if ($registeredClass -eq $classId) {
                    $reg.DeleteSubKey($progIdClassPath, $false)
                    Remove-Key-If-Empty "$classes\$progId"
                }
            }
            $state.Dispose()
        }

        $previewList = $reg.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\PreviewHandlers', $true)
        if ($previewList) {
            if ([string]$previewList.GetValue($classId) -eq 'UnityPackage Explorer') { $previewList.DeleteValue($classId, $false) }
            $previewList.Dispose()
        }
        $reg.DeleteSubKeyTree($classKeyPath, $false)
    }

    $legacyVerbs = @(
        "$classes\*\shell\UnityPackageExplorerOpen",
        "$classes\SystemFileAssociations\.unitypackage\shell\UnityPackageInspect",
        "$classes\SystemFileAssociations\.unitypackage\shell\UnityPackageExtract",
        "$classes\SystemFileAssociations\.unitypackage\shell\UnityPackageOpen",
        "$classes\SystemFileAssociations\.unitypackage\shell\UnityPackageExplorerView",
        "$classes\SystemFileAssociations\.prefab\shell\UnityPackageExplorerView",
        "$classes\SystemFileAssociations\.zip\shell\UnityPackageExplorerView",
        "$classes\Folder\shell\UnityPackageExplorerZipPreview",
        "$classes\CompressedFolder\shell\UnityPackageExplorerZipPreview",
        "$classes\CompressedFolder\shell\UnityPackageExplorerPreview"
    )
    foreach ($verb in $legacyVerbs) { Remove-Owned-Shell-Command $verb }
}
