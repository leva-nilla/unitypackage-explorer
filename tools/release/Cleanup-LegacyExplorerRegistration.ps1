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

function Assert-NoReparsePathUnderInstall([string]$Path) {
    Assert-NoReparsePathUnderRoot -Root $installRoot -Path $Path
}

function Assert-VerifiedInnoUninstallerFiles {
    $pairs = @()
    foreach ($executable in @(Get-ChildItem -LiteralPath $installRoot -File -Force | Where-Object { $_.Name -match '^unins\d+\.exe$' })) {
        $dataPath = Join-Path $installRoot ($executable.BaseName + '.dat')
        if (Test-Path -LiteralPath $dataPath -PathType Leaf) {
            Assert-NoReparsePathUnderInstall $executable.FullName
            Assert-NoReparsePathUnderInstall $dataPath
            $pairs += [pscustomobject]@{ Executable = $executable.FullName; Data = $dataPath }
        }
    }
    if ($pairs.Count -ne 1) {
        throw "Expected one verified Inno uninstaller executable/data pair in '$installRoot'; found $($pairs.Count)."
    }
}

function New-EmptyRecoveryManifest {
    $path = Join-Path ([IO.Path]::GetTempPath()) ('UnityPackageExplorer-empty-manifest-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $stream.Dispose()
    return $path
}

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
$legacyMarkerExists = Test-Path -LiteralPath $legacyMarkerPath
$innoMarkerExists = Test-Path -LiteralPath $innoMarkerPath
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
if ((Test-Path -LiteralPath $legacyMarkerPath) -or $innoMarkerExists) {
    if ($legacyMarkerExists -and -not $legacyMarkerVerified) {
        throw "The legacy ownership marker is invalid; cleanup was stopped: $legacyMarkerPath"
    }
    if ($innoMarkerExists -and -not $innoMarkerVerified) {
        throw "The Inno ownership marker is invalid; cleanup was stopped: $innoMarkerPath"
    }
    $previousInnoManifestPath = Join-Path $installRoot 'Tools\CurrentInstallFiles.txt'
    $previousInnoManifestExists = Test-Path -LiteralPath $previousInnoManifestPath
    $legacyOnlyUninstallMigration = Should-RunLegacyOnlyUninstallMigration -Mode $Mode `
        -InnoMarkerVerified $innoMarkerVerified `
        -PreviousInnoManifestExists $previousInnoManifestExists `
        -LegacyMarkerVerified $legacyMarkerVerified
    $skipFileMigration = Should-SkipOwnedFileMigrationForUninstall -Mode $Mode `
        -InnoMarkerVerified $innoMarkerVerified `
        -PreviousInnoManifestExists $previousInnoManifestExists `
        -LegacyMarkerVerified $legacyMarkerVerified
    if ($legacyOnlyUninstallMigration) {
        Assert-NoReparseTree -Root $installRoot
        Assert-NoReparsePathUnderInstall $installRoot
        Assert-NoReparsePathUnderInstall $innoMarkerPath
        Assert-NoReparsePathUnderInstall $legacyMarkerPath
        Assert-NoReparsePathUnderInstall (Join-Path $installRoot 'Tools')
        Assert-NoReparsePathUnderInstall $appPath
        Assert-VerifiedInnoUninstallerFiles
        $emptyManifest = New-EmptyRecoveryManifest
        try {
            Invoke-RequiredCleanupStep -StepName 'legacy PowerShell-owned file uninstall' -Action {
                & $migrationScript -InstallDirectory $installRoot -CurrentFilesManifest $emptyManifest -Mode Uninstall -LegacyOnlyUninstall
            }
            $legacyFilesMigrated = $true
        } catch {
            throw "Legacy PowerShell-owned files could not be removed safely; Inno uninstall cleanup was stopped: $($_.Exception.Message)"
        } finally {
            if (Test-Path -LiteralPath $emptyManifest) { Remove-Item -LiteralPath $emptyManifest -Force }
        }
    } elseif ($skipFileMigration) {
        # Inno may delete targets recorded anywhere in its uninstall log. Scan the
        # complete tree before handing control to it, without traversing links.
        Assert-NoReparseTree -Root $installRoot
        Assert-NoReparsePathUnderInstall $installRoot
        Assert-NoReparsePathUnderInstall $innoMarkerPath
        Assert-NoReparsePathUnderInstall $legacyMarkerPath
        Assert-NoReparsePathUnderInstall (Join-Path $installRoot 'Tools')
        Assert-NoReparsePathUnderInstall $appPath
        Assert-VerifiedInnoUninstallerFiles
        Write-Warning 'The verified Inno installation has no previous file manifest. Custom file migration was skipped; Inno will remove only files in its uninstall log. Any legacy PowerShell ownership marker is retained so a later setup can safely reconcile its recorded files.'
    } else {
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
    $targetsCurrentInstall = $false
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
        $targetsCurrentInstall = $target.Equals($appPath, [StringComparison]::OrdinalIgnoreCase)
    } catch {
        Write-Warning "Could not inspect legacy shortcut '$RecordedPath'; it was left untouched: $_"
        return
    }

    if ($targetsCurrentInstall) {
        Remove-VerifiedOwnedShortcut -Path $ExpectedPath
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
    $cleanupPlan = Get-OwnedShellCommandCleanupPlan -RegisteredCommand $registeredCommand -ExpectedCommand $expectedCommand `
        -ValueNames $command.GetValueNames() -SubKeyCount $command.GetSubKeyNames().Count
    $command.Dispose()
    if ($cleanupPlan -eq 'Preserve') { return }

    if ($cleanupPlan -eq 'RemoveKey') {
        $reg.DeleteSubKey($commandPath, $false)
    } elseif ($cleanupPlan -eq 'RemoveDefaultValue') {
        $command = $reg.OpenSubKey($commandPath, $true)
        if (-not $command) { throw "The owned shell command disappeared during cleanup: $commandPath" }
        try { $command.DeleteValue('', $false) } finally { $command.Dispose() }
    }
    $verb = $reg.OpenSubKey($VerbPath, $true)
    if (-not $verb) { return }
    $label = [string]$verb.GetValue('')
    if ($label -in @('UnityPackage Explorer で開く', 'UnityPackage Explorer でプレビュー', 'UnityPackage Explorerで開く')) {
        $verb.DeleteValue('', $false)
    }
    $registeredIcon = [string]$verb.GetValue('Icon')
    if ($registeredIcon -eq ($appPath + ',0') -or $registeredIcon -eq ('"' + $appPath + '",0')) {
        $verb.DeleteValue('Icon', $false)
    }
    $verb.Dispose()
    Remove-Key-If-Empty $VerbPath
}

function Get-RecordedProgIdVerbCount {
    $statePath = 'Software\UnityPackageExplorer\Install'
    $state = $reg.OpenSubKey($statePath)
    if (-not $state) { return -1 }
    try {
        $countValue = $state.GetValue('ProgIdVerbCount', $null)
        if ($null -eq $countValue) { return -1 }
        $count = [int]$countValue
        if ($count -lt 0 -or $count -gt 3) { throw "Invalid recorded ProgID verb count: $count." }

        for ($index = 0; $index -lt $count; $index++) {
            $progId = [string]$state.GetValue("ProgIdVerb${index}ProgId", '')
            $recordedCommand = [string]$state.GetValue("ProgIdVerb${index}Command", '')
            if ($progId -notmatch '^[A-Za-z0-9._-]+(\\[A-Za-z0-9._-]+)*$' -or $progId.Split('\') -contains '.' -or $progId.Split('\') -contains '..') {
                throw "Invalid recorded ProgID at index $index."
            }
            if ($recordedCommand -cne $expectedCommand) { throw "Recorded ProgID command does not match this install's application path." }
        }
    } finally { $state.Dispose() }
    return $count
}

function Remove-RecordedProgIdVerbs([int]$Count) {
    if ($Count -lt 0) { return }
    $state = $reg.OpenSubKey('Software\UnityPackageExplorer\Install')
    if (-not $state) { throw 'The install state disappeared before ProgID ownership could be read.' }
    try {
        for ($index = 0; $index -lt $Count; $index++) {
            $progId = [string]$state.GetValue("ProgIdVerb${index}ProgId", '')
            $recordedCommand = [string]$state.GetValue("ProgIdVerb${index}Command", '')
            if ($progId -notmatch '^[A-Za-z0-9._-]+(\\[A-Za-z0-9._-]+)*$' -or
                $progId.Split('\') -contains '.' -or $progId.Split('\') -contains '..' -or
                $recordedCommand -cne $expectedCommand) {
                throw "The recorded ProgID ownership changed during cleanup at index $index."
            }
            Remove-Owned-Shell-Command "$classes\$progId\shell\UnityPackageExplorer.Open"
        }
    } finally { $state.Dispose() }
}

function Clear-RecordedProgIdVerbState([int]$Count) {
    if ($Count -lt 0) { return }
    $statePath = 'Software\UnityPackageExplorer\Install'
    $state = $reg.OpenSubKey($statePath, $true)
    if (-not $state) { throw 'The install state disappeared before ProgID ownership could be cleared.' }
    try {
        $state.DeleteValue('ProgIdVerbCount', $false)
        for ($index = 0; $index -lt $count; $index++) {
            $state.DeleteValue("ProgIdVerb${index}ProgId", $false)
            $state.DeleteValue("ProgIdVerb${index}Command", $false)
        }
    } finally { $state.Dispose() }
}

function Get-OldPreviewAssociation([string]$Extension) {
    foreach ($path in @(
        "$classes\$Extension\shellex\$previewCategory",
        "$classes\SystemFileAssociations\$Extension\shellex\$previewCategory"
    )) { Remove-Owned-Preview-Association $path }
}

Invoke-RequiredCleanupStep -StepName 'legacy PreviewHandler and shell registration cleanup' -Action {
    $progIdVerbCount = Get-RecordedProgIdVerbCount
    Remove-RecordedProgIdVerbs $progIdVerbCount

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
    Clear-RecordedProgIdVerbState $progIdVerbCount
}

if ($legacyFilesMigrated -and $legacyMarkerVerified) {
    Invoke-RequiredCleanupStep -StepName 'legacy ownership marker finalization' -Action {
        Remove-VerifiedLegacyOwnershipMarker -InstallDirectory $installRoot
    }
}
