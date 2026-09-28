[CmdletBinding()]
param(
    [string]$Root = $PSScriptRoot,
    [string]$ContextMenuCertificateThumbprint,
    [string]$TrustCertificatePath,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$rootPath = [IO.Path]::GetFullPath($Root)
$output = Join-Path $rootPath 'src\UnityPackageExplorer.App\bin\Release\net9.0-windows'
$appPath = Join-Path $output 'UnityPackageExplorer.App.exe'
$extractorPath = Join-Path $rootPath 'src\UnityPackageExplorer.Cli\bin\Release\net9.0\UnityPackageExplorer.Cli.exe'
$comHost = Join-Path $rootPath 'src\UnityPackageExplorer.PreviewHandler\bin\Release\net9.0-windows\UnityPackageExplorer.PreviewHandler.comhost.dll'

$appCommandLine = '"{0}" "%1"' -f $appPath
$sendToShortcutPath = Join-Path ([Environment]::GetFolderPath('SendTo')) 'UnityPackage Explorer.lnk'
$solutionPath = Join-Path $rootPath 'UnityPackageExplorer.sln'
if (-not $SkipBuild) {
    & dotnet build $solutionPath -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
} else {
    Write-Host 'Skipping Release build; using existing outputs.'
}
if (-not (Test-Path -LiteralPath $appPath)) { throw "App executable was not produced: $appPath" }
if (-not (Test-Path -LiteralPath $extractorPath)) { throw "Safe extractor was not produced: $extractorPath" }
if (-not (Test-Path -LiteralPath $comHost)) { throw "Preview handler COM host was not produced: $comHost" }

$classId = 'C24F7D7D-2C91-4AA9-969D-FADE6F9A17D2'
$appId = 'A643A2EF-8C1B-4A1B-83B1-7855A66E7CB3'
$previewProgId = 'UnityPackageExplorer.PreviewHandler'
$previewCategory = '{8895b1c6-b41f-4c1c-a562-0d564250836f}'
$reg = [Microsoft.Win32.Registry]::CurrentUser
$classesRoot = [Microsoft.Win32.Registry]::ClassesRoot
$classes = 'Software\Classes'
$statePath = 'Software\UnityPackageExplorer\Install'
$contextMenuClsid = '1C490A47-4D85-4AA0-A2A3-4A7827B5DA79'
$contextMenuVerbPath = "$classes\*\shell\UnityPackageExplorerOpen"

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
if (Test-Path -LiteralPath $sendToShortcutPath) {
    $existingShell = New-Object -ComObject WScript.Shell
    $existingShortcut = $existingShell.CreateShortcut($sendToShortcutPath)
    if ($existingShortcut.TargetPath -and
        ([IO.Path]::GetFullPath($existingShortcut.TargetPath) -ne [IO.Path]::GetFullPath($appPath))) {
        throw "既存の Send To ショートカットを上書きしません: $sendToShortcutPath"
    }
}
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($sendToShortcutPath)
$shortcut.TargetPath = $appPath
$shortcut.WorkingDirectory = $output
$shortcut.Description = 'ZIP、unitypackage、Prefab を UnityPackage Explorer で開く'
$shortcut.Save()

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
if ($packageState) { $packageState.Dispose() }
$matchingPackage = if ($packageName -and $packagePublisher -and $packageFullName) {
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
Write-Host "App: $appPath"
