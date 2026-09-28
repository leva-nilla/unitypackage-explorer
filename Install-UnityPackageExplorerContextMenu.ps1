[CmdletBinding()]
param(
    [string]$Root = $PSScriptRoot,
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\UnityPackageExplorer'),
    [string]$ContextMenuCertificateThumbprint,
    [string]$TrustCertificatePath,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$rootPath = [IO.Path]::GetFullPath($Root)
$programsRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs')).TrimEnd('\')
$installDirectory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if (-not $installDirectory.StartsWith($programsRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "The per-user application must be installed under $programsRoot."
}
$appInstallStatePath = 'Software\UnityPackageExplorer\ApplicationInstall'
$statePath = 'Software\UnityPackageExplorer\Install'
$reg = [Microsoft.Win32.Registry]::CurrentUser
$classes = 'Software\Classes'
$classesRoot = [Microsoft.Win32.Registry]::ClassesRoot
$installMarkerPath = Join-Path $installDirectory '.unitypackage-explorer-install.json'
$appPath = Join-Path $installDirectory 'UnityPackageExplorer.App.exe'
$previousRepoAppPath = Join-Path $rootPath 'src\UnityPackageExplorer.App\bin\Release\net9.0-windows\UnityPackageExplorer.App.exe'
$shellDll = Join-Path $installDirectory 'UnityPackageExplorer.ShellExtension.dll'
$comHost = Join-Path $installDirectory 'PreviewHandler\UnityPackageExplorer.PreviewHandler.comhost.dll'
$sendToShortcutPath = Join-Path ([Environment]::GetFolderPath('SendTo')) 'UnityPackage Explorer.lnk'
$startMenuShortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'UnityPackage Explorer.lnk'
$appCommandLine = '"{0}" "%1"' -f $appPath
$buildRoot = Join-Path $env:TEMP ('UnityPackageExplorer-build-' + [Guid]::NewGuid().ToString('N'))
$publishRoot = Join-Path $buildRoot 'publish'
$baseOutputPath = (Join-Path $buildRoot 'bin') + [IO.Path]::DirectorySeparatorChar
$nativeIntermediatePath = (Join-Path $buildRoot 'native-obj') + [IO.Path]::DirectorySeparatorChar
$previewBaseOutputPath = (Join-Path $buildRoot 'preview-bin') + [IO.Path]::DirectorySeparatorChar
$previewBuildOutput = Join-Path $buildRoot 'preview-bin\Release\net9.0-windows'
$contextMenuClsid = '1C490A47-4D85-4AA0-A2A3-4A7827B5DA79'
$contextMenuVerbPath = "$classes\*\shell\UnityPackageExplorerOpen"

$previousAppState = $reg.OpenSubKey($appInstallStatePath)
$previousInstallDirectory = if ($previousAppState) { [string]$previousAppState.GetValue('InstallDirectory') } else { '' }
if ($previousAppState) { $previousAppState.Dispose() }
$previousState = $reg.OpenSubKey($statePath)
$previousAppPath = if ($previousState) { [string]$previousState.GetValue('AppPath') } else { '' }
if ($previousState) { $previousState.Dispose() }

$installIsOwned = $false
$previouslyOwnedFiles = @()
if (Test-Path -LiteralPath $installDirectory) {
    $installItem = Get-Item -LiteralPath $installDirectory -Force
    if (-not $installItem.PSIsContainer -or ($installItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Install destination is not a normal directory: $installDirectory"
    }
    $existingItems = @(Get-ChildItem -LiteralPath $installDirectory -Force)
    if ($existingItems.Count -gt 0) {
        if ([IO.Path]::GetFullPath($previousInstallDirectory).TrimEnd('\') -ne $installDirectory -or
            -not (Test-Path -LiteralPath $installMarkerPath -PathType Leaf)) {
            throw "Install destination is non-empty and is not a verified UnityPackage Explorer installation: $installDirectory"
        }
        try { $installMarker = Get-Content -LiteralPath $installMarkerPath -Raw | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "Installation ownership marker is invalid; destination was left untouched: $installMarkerPath" }
        if ($installMarker.Product -ne 'UnityPackageExplorer' -or
            [IO.Path]::GetFullPath([string]$installMarker.InstallDirectory).TrimEnd('\') -ne $installDirectory) {
            throw "Installation ownership marker does not match this product and path: $installMarkerPath"
        }
        $installIsOwned = $true
        $previouslyOwnedFiles = @($installMarker.OwnedFiles | ForEach-Object { ([string]$_).Replace('/', '\') })
        foreach ($item in Get-ChildItem -LiteralPath $installDirectory -Force -Recurse) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse points are not allowed inside the managed application directory: $($item.FullName)"
            }
        }
    }
}

function Assert-ShortcutCanBeUpdated([string]$shortcutPath, [string]$newTarget, [string]$oldTarget) {
    if (-not (Test-Path -LiteralPath $shortcutPath)) { return }
    $shortcutShell = New-Object -ComObject WScript.Shell
    $existingShortcut = $shortcutShell.CreateShortcut($shortcutPath)
    $existingTarget = if ($existingShortcut.TargetPath) { [IO.Path]::GetFullPath($existingShortcut.TargetPath) } else { '' }
    if ($existingTarget -and
        -not $existingTarget.Equals([IO.Path]::GetFullPath($newTarget), [StringComparison]::OrdinalIgnoreCase) -and
        -not ($oldTarget -and $existingTarget.Equals([IO.Path]::GetFullPath($oldTarget), [StringComparison]::OrdinalIgnoreCase)) -and
        -not $existingTarget.Equals([IO.Path]::GetFullPath($previousRepoAppPath), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Existing shortcut is not owned by this installation and will not be overwritten: $shortcutPath"
    }
}

Assert-ShortcutCanBeUpdated $sendToShortcutPath $appPath $previousAppPath
Assert-ShortcutCanBeUpdated $startMenuShortcutPath $appPath $previousAppPath

if ($SkipBuild) {
    if (-not $installIsOwned) { throw '-SkipBuild requires an existing verified installation in -InstallDirectory.' }
    $publishRoot = $installDirectory
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'UnityPackageExplorer.App.exe'))) { throw "Installed app executable is missing: $appPath" }
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'UnityPackageExplorer.ShellExtension.dll'))) { throw "Installed Explorer command DLL is missing: $shellDll" }
    if (-not (Test-Path -LiteralPath $comHost)) { throw "Installed preview handler COM host is missing: $comHost" }
    Write-Host 'Skipping publish/build and reusing the verified installed files.'
} else {
    New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
    $appProject = Join-Path $rootPath 'src\UnityPackageExplorer.App\UnityPackageExplorer.App.csproj'
    & dotnet publish $appProject -c Release -r win-x64 --self-contained true `
        "-p:BaseOutputPath=$baseOutputPath" `
        "-p:PublishDir=$publishRoot" `
        -p:UseAppHost=true
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained win-x64 app publish failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'UnityPackageExplorer.App.exe'))) { throw 'Publish did not produce UnityPackageExplorer.App.exe.' }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer vswhere.exe was not found.' }
    $vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vsPath) { throw 'A Visual Studio C++ x64 toolchain is required.' }
    $msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
    $nativeProject = Join-Path $rootPath 'src\UnityPackageExplorer.ShellExtension\UnityPackageExplorer.ShellExtension.vcxproj'
    & $msbuild $nativeProject /m /v:minimal /p:Configuration=Release /p:Platform=x64 `
        "/p:ShellOutputDirectory=$publishRoot" "/p:IntDir=$nativeIntermediatePath"
    if ($LASTEXITCODE -ne 0) { throw 'Native Explorer command build failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'UnityPackageExplorer.ShellExtension.dll'))) { throw 'Native Explorer command DLL was not produced.' }

    $previewProject = Join-Path $rootPath 'src\UnityPackageExplorer.PreviewHandler\UnityPackageExplorer.PreviewHandler.csproj'
    & dotnet build $previewProject -c Release "-p:BaseOutputPath=$previewBaseOutputPath"
    if ($LASTEXITCODE -ne 0) { throw 'Preview-handler build failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $previewBuildOutput 'UnityPackageExplorer.PreviewHandler.comhost.dll'))) { throw 'Preview-handler COM host was not produced.' }
    $previewInstallRoot = Join-Path $publishRoot 'PreviewHandler'
    New-Item -ItemType Directory -Force -Path $previewInstallRoot | Out-Null
    Copy-Item -Path (Join-Path $previewBuildOutput '*') -Destination $previewInstallRoot -Recurse -Force

    $assetSource = Join-Path $rootPath 'tools\context-menu\Assets'
    $assetDestination = Join-Path $publishRoot 'Assets'
    New-Item -ItemType Directory -Force -Path $assetDestination | Out-Null
    Copy-Item -LiteralPath (Join-Path $assetSource 'Square150x150Logo.png') -Destination $assetDestination -Force
    Copy-Item -LiteralPath (Join-Path $assetSource 'Square44x44Logo.png') -Destination $assetDestination -Force

    $stagedFiles = @(Get-ChildItem -LiteralPath $publishRoot -File -Recurse -Force)
    if ($stagedFiles.Count -eq 0) { throw 'The staged application output is empty.' }
    $relativeFiles = @($stagedFiles | ForEach-Object { $_.FullName.Substring($publishRoot.TrimEnd('\').Length + 1).Replace('/', '\') })
    $ownedFileSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relativeFile in $previouslyOwnedFiles) { [void]$ownedFileSet.Add($relativeFile) }
    foreach ($relativeFile in $relativeFiles) {
        $destinationFile = Join-Path $installDirectory $relativeFile
        $destinationParent = Split-Path -Parent $destinationFile
        while ($destinationParent -and $destinationParent.StartsWith($installDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
            if (Test-Path -LiteralPath $destinationParent -PathType Leaf) { throw "Install path component is a file: $destinationParent" }
            $destinationParent = Split-Path -Parent $destinationParent
        }
        if (Test-Path -LiteralPath $destinationFile) {
            $item = Get-Item -LiteralPath $destinationFile -Force
            if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or -not $ownedFileSet.Contains($relativeFile)) {
                throw "Install would overwrite a non-owned file or directory: $destinationFile"
            }
            try {
                $stream = [IO.File]::Open($destinationFile, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $stream.Dispose()
            } catch { throw "An installed file is in use or inaccessible; close UnityPackage Explorer and retry: $destinationFile" }
        }
        [void]$ownedFileSet.Add($relativeFile)
    }

    New-Item -ItemType Directory -Force -Path $installDirectory | Out-Null
    $markerData = @{
        Product = 'UnityPackageExplorer'
        InstallDirectory = $installDirectory
        OwnedFiles = @($ownedFileSet | Sort-Object)
    } | ConvertTo-Json -Depth 3
    $appInstallState = $reg.CreateSubKey($appInstallStatePath)
    $appInstallState.SetValue('InstallDirectory', $installDirectory, [Microsoft.Win32.RegistryValueKind]::String)
    $appInstallState.SetValue('OwnershipMarkerPath', $installMarkerPath, [Microsoft.Win32.RegistryValueKind]::String)
    $appInstallState.Dispose()

    # Keep transient marker data outside the destination. If interrupted before
    # the atomic move, the destination remains empty/retryable; once the marker
    # appears, the registry ownership record is already present.
    $markerTempPath = Join-Path $buildRoot 'install-marker.json'
    [IO.File]::WriteAllText($markerTempPath, $markerData, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $markerTempPath -Destination $installMarkerPath -Force

    foreach ($stagedFile in $stagedFiles) {
        $relativeFile = $stagedFile.FullName.Substring($publishRoot.TrimEnd('\').Length + 1)
        $destinationFile = Join-Path $installDirectory $relativeFile
        $destinationParent = Split-Path -Parent $destinationFile
        if (Test-Path -LiteralPath $destinationParent -PathType Leaf) { throw "Install path component is a file: $destinationParent" }
        New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
        Copy-Item -LiteralPath $stagedFile.FullName -Destination $destinationFile -Force
    }
}
if (-not (Test-Path -LiteralPath $appPath)) { throw "Installed app executable is missing: $appPath" }
if (-not (Test-Path -LiteralPath $shellDll)) { throw "Installed native Explorer command is missing: $shellDll" }
if (-not (Test-Path -LiteralPath $comHost)) { throw "Installed preview-handler COM host is missing: $comHost" }

$classId = 'C24F7D7D-2C91-4AA9-969D-FADE6F9A17D2'
$appId = 'A643A2EF-8C1B-4A1B-83B1-7855A66E7CB3'
$previewProgId = 'UnityPackageExplorer.PreviewHandler'
$previewCategory = '{8895b1c6-b41f-4c1c-a562-0d564250836f}'
function Set-DefaultValue([string]$keyPath, [string]$value) {
    $key = $reg.CreateSubKey($keyPath)
    try { $key.SetValue('', $value, [Microsoft.Win32.RegistryValueKind]::String) }
    finally { $key.Dispose() }
}

function Add-PreviewAssociation([string]$extension, [string]$progId) {
    $associationKeys = [System.Collections.Generic.List[string]]::new()
    $associationKeys.Add("$classes\$extension\shellex\$previewCategory")
    $associationKeys.Add("$classes\SystemFileAssociations\$extension\shellex\$previewCategory")
    if (-not [string]::IsNullOrWhiteSpace($progId)) {
        $associationKeys.Add("$classes\$progId\shellex\$previewCategory")
    }
    foreach ($keyPath in $associationKeys) {
        $existing = $reg.OpenSubKey($keyPath)
        if ($existing) {
            $current = [string]$existing.GetValue('')
            $existing.Dispose()
            if ($current -and $current -ne "{$classId}") { throw "既存のプレビューハンドラーを上書きしません: $keyPath ($current)" }
        }
        Set-DefaultValue $keyPath "{$classId}"
    }
}

function Get-RegisteredPreviewHandler([string]$keyPath) {
    $relativePath = $keyPath.Substring($classes.Length + 1)
    $key = $classesRoot.OpenSubKey($relativePath)
    if (-not $key) { return '' }
    try { return [string]$key.GetValue('') }
    finally { $key.Dispose() }
}

$existingContextVerb = $reg.OpenSubKey($contextMenuVerbPath)
$existingContextHandler = if ($existingContextVerb) { [string]$existingContextVerb.GetValue('ExplorerCommandHandler') } else { '' }
if ($existingContextVerb) { $existingContextVerb.Dispose() }
if ($existingContextHandler -and $existingContextHandler -ne "{$contextMenuClsid}") {
    throw "既存のコンテキストメニューコマンドを上書きしません: $contextMenuVerbPath ($existingContextHandler)"
}

$associationInfo = @{}
foreach ($extension in @('.unitypackage', '.prefab', '.zip')) {
    $extensionKey = $classesRoot.OpenSubKey($extension)
    $progId = if ($extensionKey) { [string]$extensionKey.GetValue('') } else { '' }
    if ($extensionKey) { $extensionKey.Dispose() }
    $associationInfo[$extension] = $progId
    $associationKeys = @(
        "$classes\$extension\shellex\$previewCategory",
        "$classes\SystemFileAssociations\$extension\shellex\$previewCategory"
    )
    if (-not [string]::IsNullOrWhiteSpace($progId)) { $associationKeys += "$classes\$progId\shellex\$previewCategory" }
    foreach ($keyPath in $associationKeys) {
        $current = Get-RegisteredPreviewHandler $keyPath
        if ($current -and $current -ne "{$classId}") { throw "既存のプレビューハンドラーがあるため登録を中止しました: $keyPath ($current)" }
    }
}

$previewProgIdKey = $classesRoot.OpenSubKey("$previewProgId\CLSID")
if ($previewProgIdKey) {
    $registeredClassId = [string]$previewProgIdKey.GetValue('')
    $previewProgIdKey.Dispose()
    if ($registeredClassId -and $registeredClassId -ne "{$classId}") {
        throw "既存の ProgID が別の COM クラスを指しているため登録を中止しました: $previewProgId ($registeredClassId)"
    }
}

$state = $reg.CreateSubKey($statePath)
$serverKey = "$classes\CLSID\{$classId}"
$inprocKey = "$serverKey\InprocServer32"
Set-DefaultValue $serverKey 'UnityPackage Explorer Preview Handler'
Set-DefaultValue $inprocKey $comHost
Set-DefaultValue "$serverKey\ProgID" 'UnityPackageExplorer.PreviewHandler'
$inproc = $reg.CreateSubKey($inprocKey)
$inproc.SetValue('ThreadingModel', 'Apartment', [Microsoft.Win32.RegistryValueKind]::String)
$inproc.SetValue('ProgID', $previewProgId, [Microsoft.Win32.RegistryValueKind]::String)
$inproc.Dispose()
$class = $reg.CreateSubKey($serverKey)
$class.SetValue('AppID', "{$appId}", [Microsoft.Win32.RegistryValueKind]::String)
$class.Dispose()
$appIdKey = $reg.CreateSubKey("$classes\AppID\{$appId}")
$appIdKey.SetValue('DllSurrogate', 'Prevhost.exe', [Microsoft.Win32.RegistryValueKind]::String)
$appIdKey.Dispose()
Set-DefaultValue "$classes\$previewProgId\CLSID" "{$classId}"

$previewList = $reg.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\PreviewHandlers')
$previewList.SetValue("{$classId}", 'UnityPackage Explorer', [Microsoft.Win32.RegistryValueKind]::String)
$previewList.Dispose()

foreach ($extension in @('.unitypackage', '.prefab', '.zip')) {
    Add-PreviewAssociation $extension $associationInfo[$extension]
}

$state.SetValue('AppPath', $appPath, [Microsoft.Win32.RegistryValueKind]::String)
$state.SetValue('ComHost', $comHost, [Microsoft.Win32.RegistryValueKind]::String)
$state.SetValue('InstallDirectory', $installDirectory, [Microsoft.Win32.RegistryValueKind]::String)
$state.SetValue('UnityPackageProgId', $associationInfo['.unitypackage'], [Microsoft.Win32.RegistryValueKind]::String)
$state.SetValue('PrefabProgId', $associationInfo['.prefab'], [Microsoft.Win32.RegistryValueKind]::String)
$state.SetValue('ZipProgId', $associationInfo['.zip'], [Microsoft.Win32.RegistryValueKind]::String)
$state.Dispose()

$state = $reg.CreateSubKey($statePath)
$state.SetValue('SendToShortcutPath', $sendToShortcutPath, [Microsoft.Win32.RegistryValueKind]::String)
$state.DeleteValue('ZipFolderMenuPath', $false)
$state.DeleteValue('CompressedFolderMenuPath', $false)
$state.Dispose()

# Windows 11 may suppress unregistered static verbs in its primary context menu.
# Send To is built in and passes the selected path to the shortcut.
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($sendToShortcutPath)
$shortcut.TargetPath = $appPath
$shortcut.WorkingDirectory = $installDirectory
$shortcut.Description = 'ZIP、unitypackage、Prefab を UnityPackage Explorer で開く'
$shortcut.Save()

$startMenuShell = New-Object -ComObject WScript.Shell
$startMenuShortcut = $startMenuShell.CreateShortcut($startMenuShortcutPath)
$startMenuShortcut.TargetPath = $appPath
$startMenuShortcut.WorkingDirectory = $installDirectory
$startMenuShortcut.Description = 'ZIP、unitypackage、Prefab を UnityPackage Explorer で開く'
$startMenuShortcut.Save()

# Remove earlier static ZIP verbs only when their command points to this installation.
foreach ($oldMenuPath in @(
    "$classes\SystemFileAssociations\.unitypackage\shell\UnityPackageExplorerView",
    "$classes\SystemFileAssociations\.prefab\shell\UnityPackageExplorerView",
    "$classes\SystemFileAssociations\.zip\shell\UnityPackageExplorerView",
    "$classes\Folder\shell\UnityPackageExplorerZipPreview",
    "$classes\CompressedFolder\shell\UnityPackageExplorerZipPreview"
)) {
    $oldCommandKey = $reg.OpenSubKey("$oldMenuPath\command")
    $oldCommand = if ($oldCommandKey) { [string]$oldCommandKey.GetValue('') } else { '' }
    if ($oldCommandKey) { $oldCommandKey.Dispose() }
    if ($oldCommand -eq $appCommandLine) {
        $reg.DeleteSubKeyTree($oldMenuPath, $false)
    }
}
$state = $reg.CreateSubKey($statePath)
$state.DeleteValue('ZipFolderMenuPath', $false)
$state.DeleteValue('CompressedFolderMenuPath', $false)
$state.SetValue('StartMenuShortcutPath', $startMenuShortcutPath, [Microsoft.Win32.RegistryValueKind]::String)
$state.Dispose()
if (-not ('UnityPackageExplorerShellNotifications' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class UnityPackageExplorerShellNotifications
{
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
'@
}
[UnityPackageExplorerShellNotifications]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

if ($ContextMenuCertificateThumbprint) {
    $modernMenuInstaller = Join-Path $rootPath 'tools\context-menu\Install-ExplorerContextMenuPackage.ps1'
    $installParameters = @{
        Root = $rootPath
        CertificateThumbprint = $ContextMenuCertificateThumbprint
        ExternalLocation = $installDirectory
        Confirm = $false
    }
    if ($TrustCertificatePath) { $installParameters.TrustCertificatePath = $TrustCertificatePath }
    & $modernMenuInstaller @installParameters
} elseif ($TrustCertificatePath) {
    throw 'Supply -ContextMenuCertificateThumbprint together with -TrustCertificatePath.'
}

# The classic "Show more options" menu uses a per-user wildcard verb whose
# packaged COM command filters the selected item to the three supported types.
# Register it only when this exact sparse package identity is already available.
$packageState = $reg.OpenSubKey($statePath)
$packageName = if ($packageState) { [string]$packageState.GetValue('SparsePackageName') } else { '' }
$packagePublisher = if ($packageState) { [string]$packageState.GetValue('SparsePackagePublisher') } else { '' }
$packageFullName = if ($packageState) { [string]$packageState.GetValue('SparsePackageFullName') } else { '' }
$packageExternalLocation = if ($packageState) { [string]$packageState.GetValue('SparseExternalLocation') } else { '' }
if ($packageState) { $packageState.Dispose() }
$matchingPackage = if ($packageName -and $packagePublisher -and $packageFullName -and
    [IO.Path]::GetFullPath($packageExternalLocation).TrimEnd('\') -eq $installDirectory) {
    Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue |
        Where-Object { $_.Publisher -eq $packagePublisher -and $_.PackageFullName -eq $packageFullName } |
        Select-Object -First 1
} else { $null }
if ($matchingPackage) {
    $verb = $reg.CreateSubKey($contextMenuVerbPath)
    $verb.SetValue('', 'UnityPackage Explorer で開く', [Microsoft.Win32.RegistryValueKind]::String)
    $verb.SetValue('ExplorerCommandHandler', "{$contextMenuClsid}", [Microsoft.Win32.RegistryValueKind]::String)
    $verb.Dispose()
    $installedState = $reg.CreateSubKey($statePath)
    $installedState.SetValue('LegacyContextMenuPath', $contextMenuVerbPath, [Microsoft.Win32.RegistryValueKind]::String)
    $installedState.SetValue('LegacyContextMenuClsid', "{$contextMenuClsid}", [Microsoft.Win32.RegistryValueKind]::String)
    $installedState.Dispose()
    Write-Host '[OK] 「その他のオプションを確認」メニューに対応コマンドを登録しました。'
} else {
    Write-Warning 'sparse packageがCurrentUserに登録されていないため、「その他のオプション」項目は登録しません。Send To とドラッグ＆ドロップは利用できます。'
}

[UnityPackageExplorerShellNotifications]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host '[OK] .unitypackage、.zip、.prefab の Explorer プレビューハンドラー関連付けを登録しました。'
Write-Host '[OK] Send To に UnityPackage Explorer を登録しました。'
Write-Host '[OK] Start Menu に UnityPackage Explorer を登録しました。'
Write-Host "App: $appPath"
Write-Host "Install directory: $installDirectory"
