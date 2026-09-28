[CmdletBinding()]
param(
    [string]$DevelopmentCertificateThumbprint
)

$ErrorActionPreference = 'Stop'
$classId = 'C24F7D7D-2C91-4AA9-969D-FADE6F9A17D2'
$appId = 'A643A2EF-8C1B-4A1B-83B1-7855A66E7CB3'
$previewProgId = 'UnityPackageExplorer.PreviewHandler'
$previewCategory = '{8895b1c6-b41f-4c1c-a562-0d564250836f}'
$reg = [Microsoft.Win32.Registry]::CurrentUser
$classes = 'Software\Classes'
$statePath = 'Software\UnityPackageExplorer\Install'
$state = $reg.OpenSubKey($statePath)

function Remove-OurPreviewAssociation([string]$extension, [string]$progId) {
    $associationKeys = [System.Collections.Generic.List[string]]::new()
    $associationKeys.Add("$classes\$extension\shellex\$previewCategory")
    $associationKeys.Add("$classes\SystemFileAssociations\$extension\shellex\$previewCategory")
    if (-not [string]::IsNullOrWhiteSpace($progId)) {
        $associationKeys.Add("$classes\$progId\shellex\$previewCategory")
    }
    foreach ($keyPath in $associationKeys) {
        $key = $reg.OpenSubKey($keyPath)
        if ($key) {
            $current = [string]$key.GetValue('')
            $key.Dispose()
            if ($current -eq "{$classId}") {
                $parentPath = $keyPath.Substring(0, $keyPath.LastIndexOf('\'))
                $parent = $reg.OpenSubKey($parentPath, $true)
                if ($parent) {
                    $parent.DeleteSubKey($previewCategory, $false)
                    $parent.Dispose()
                }
            }
        }
    }
}

$unityPackageProgId = if ($state) { [string]$state.GetValue('UnityPackageProgId') } else { '' }
$prefabProgId = if ($state) { [string]$state.GetValue('PrefabProgId') } else { '' }
$zipProgId = if ($state) { [string]$state.GetValue('ZipProgId') } else { '' }
$appPath = if ($state) { [string]$state.GetValue('AppPath') } else { '' }
$sendToShortcutPath = if ($state) { [string]$state.GetValue('SendToShortcutPath') } else { '' }
$zipFolderMenuPath = if ($state) { [string]$state.GetValue('ZipFolderMenuPath') } else { '' }
$legacyCompressedFolderMenuPath = if ($state) { [string]$state.GetValue('CompressedFolderMenuPath') } else { '' }
if ($state) { $state.Dispose() }

$packageUninstaller = Join-Path $PSScriptRoot 'tools\context-menu\Uninstall-ExplorerContextMenuPackage.ps1'
if ($DevelopmentCertificateThumbprint) {
    & $packageUninstaller -DevelopmentCertificateThumbprint $DevelopmentCertificateThumbprint -SkipPackageConfirmation
} else {
    & $packageUninstaller -SkipPackageConfirmation
}

Remove-OurPreviewAssociation '.unitypackage' $unityPackageProgId
Remove-OurPreviewAssociation '.prefab' $prefabProgId
Remove-OurPreviewAssociation '.zip' $zipProgId

$previewList = $reg.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\PreviewHandlers', $true)
if ($previewList) {
    $previewList.DeleteValue("{$classId}", $false)
    $previewList.Dispose()
}

$associationMenus = @(
    "$classes\SystemFileAssociations\.unitypackage\shell\UnityPackageExplorerView",
    "$classes\SystemFileAssociations\.prefab\shell\UnityPackageExplorerView",
    "$classes\SystemFileAssociations\.zip\shell\UnityPackageExplorerView",
    "$classes\SystemFileAssociations\.unitypackage\shell\UnityPackageInspect",
    "$classes\Folder\shell\UnityPackageExplorerZipPreview",
    "$classes\CompressedFolder\shell\UnityPackageExplorerZipPreview"
)
foreach ($keyPath in $associationMenus) {
    $commandKey = $reg.OpenSubKey("$keyPath\command")
    $registeredCommand = if ($commandKey) { [string]$commandKey.GetValue('') } else { '' }
    if ($commandKey) { $commandKey.Dispose() }
    if ($appPath -and $registeredCommand -eq ('"' + $appPath + '" "%1"')) {
        $reg.DeleteSubKeyTree($keyPath, $false)
    }
}

if ($appPath -and $sendToShortcutPath -and (Test-Path -LiteralPath $sendToShortcutPath)) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($sendToShortcutPath)
    if ($shortcut.TargetPath -and
        ([IO.Path]::GetFullPath($shortcut.TargetPath) -eq [IO.Path]::GetFullPath($appPath))) {
        Remove-Item -LiteralPath $sendToShortcutPath -Force
    }
}
if ($appPath -and $zipFolderMenuPath) {
    $commandKey = $reg.OpenSubKey("$zipFolderMenuPath\command")
    $registeredCommand = if ($commandKey) { [string]$commandKey.GetValue('') } else { '' }
    if ($commandKey) { $commandKey.Dispose() }
    if ($registeredCommand -eq ('"' + $appPath + '" "%1"')) {
        $reg.DeleteSubKeyTree($zipFolderMenuPath, $false)
    }
}
if ($appPath -and $legacyCompressedFolderMenuPath) {
    $commandKey = $reg.OpenSubKey("$legacyCompressedFolderMenuPath\command")
    $registeredCommand = if ($commandKey) { [string]$commandKey.GetValue('') } else { '' }
    if ($commandKey) { $commandKey.Dispose() }
    if ($registeredCommand -eq ('"' + $appPath + '" "%1"')) {
        $reg.DeleteSubKeyTree($legacyCompressedFolderMenuPath, $false)
    }
}

# Remove verbs from the first PowerShell version when present; keep all unrelated file handlers.
$legacyMenuPath = "$classes\SystemFileAssociations\.unitypackage\shell"
foreach ($verb in @('UnityPackageInspect', 'UnityPackageExtract', 'UnityPackageOpen')) {
    $reg.DeleteSubKeyTree("$legacyMenuPath\$verb", $false)
}

$reg.DeleteSubKeyTree("$classes\CLSID\{$classId}", $false)
$reg.DeleteSubKeyTree("$classes\AppID\{$appId}", $false)
$previewProgIdPath = "$classes\$previewProgId"
$previewProgIdClass = $reg.OpenSubKey("$previewProgIdPath\CLSID")
if ($previewProgIdClass) {
    $registeredClassId = [string]$previewProgIdClass.GetValue('')
    $previewProgIdClass.Dispose()
    if ($registeredClassId -eq "{$classId}") {
        $reg.DeleteSubKey("$previewProgIdPath\CLSID", $false)
        $parent = $reg.OpenSubKey($previewProgIdPath)
        if ($parent) {
            $children = $parent.GetSubKeyNames()
            $values = $parent.GetValueNames()
            $parent.Dispose()
            if ($children.Count -eq 0 -and $values.Count -eq 0) { $reg.DeleteSubKey($previewProgIdPath, $false) }
        }
    }
}
if (Test-Path -LiteralPath 'HKCU:\Software\UnityPackageExplorer\Install') {
    Remove-Item -LiteralPath 'HKCU:\Software\UnityPackageExplorer\Install' -Recurse -Force
}
if (Test-Path -LiteralPath 'HKCU:\Software\UnityPackageExplorer') {
    $parent = $reg.OpenSubKey('Software\UnityPackageExplorer')
    $children = $parent.GetSubKeyNames()
    $values = $parent.GetValueNames()
    $parent.Dispose()
    if ($children.Count -eq 0 -and $values.Count -eq 0) { $reg.DeleteSubKey('Software\UnityPackageExplorer', $false) }
}

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

Write-Host '[OK] UnityPackage Explorer のプレビューと右クリック登録を解除しました。'
