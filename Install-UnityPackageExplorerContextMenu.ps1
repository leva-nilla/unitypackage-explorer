[CmdletBinding()]
param([string]$Root = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
$rootPath = [IO.Path]::GetFullPath($Root)
$output = Join-Path $rootPath 'src\UnityPackageExplorer.App\bin\Release\net9.0-windows'
$appPath = Join-Path $output 'UnityPackageExplorer.App.exe'
$extractorPath = Join-Path $rootPath 'src\UnityPackageExplorer.Cli\bin\Release\net9.0\UnityPackageExplorer.Cli.exe'
$comHost = Join-Path $rootPath 'src\UnityPackageExplorer.PreviewHandler\bin\Release\net9.0-windows\UnityPackageExplorer.PreviewHandler.comhost.dll'

$solutionPath = Join-Path $rootPath 'UnityPackageExplorer.sln'
& dotnet build $solutionPath -c Release
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
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
$legacyInspectPath = "$classes\SystemFileAssociations\.unitypackage\shell\UnityPackageInspect"
$legacyInspectKey = $reg.OpenSubKey($legacyInspectPath)
$menuVerb = if ($legacyInspectKey) { 'UnityPackageInspect' } else { 'UnityPackageExplorerView' }
if ($legacyInspectKey) { $legacyInspectKey.Dispose() }
$menuPath = "$classes\SystemFileAssociations\.unitypackage\shell\$menuVerb"
$state.SetValue('MenuVerb', $menuVerb, [Microsoft.Win32.RegistryValueKind]::String)
$state.Dispose()

$menuKey = $reg.CreateSubKey($menuPath)
$menuKey.SetValue('', 'プレビュー（UnityPackage Explorer）', [Microsoft.Win32.RegistryValueKind]::String)
$menuKey.Dispose()
$menuCommand = $reg.CreateSubKey("$menuPath\command")
$menuCommand.SetValue('', '"' + $appPath + '" "%1"', [Microsoft.Win32.RegistryValueKind]::String)
$menuCommand.Dispose()

$prefabMenuPath = "$classes\SystemFileAssociations\.prefab\shell\UnityPackageExplorerView"
$prefabMenu = $reg.CreateSubKey($prefabMenuPath)
$prefabMenu.SetValue('', 'Prefab をプレビュー（UnityPackage Explorer）', [Microsoft.Win32.RegistryValueKind]::String)
$prefabMenu.Dispose()
$prefabCommand = $reg.CreateSubKey("$prefabMenuPath\command")
$prefabCommand.SetValue('', '"' + $appPath + '" "%1"', [Microsoft.Win32.RegistryValueKind]::String)
$prefabCommand.Dispose()

$zipMenuPath = "$classes\SystemFileAssociations\.zip\shell\UnityPackageExplorerView"
$zipMenu = $reg.CreateSubKey($zipMenuPath)
$zipMenu.SetValue('', 'ZIP をプレビュー（UnityPackage Explorer）')
$zipMenu.Dispose()
$zipCommand = $reg.CreateSubKey("$zipMenuPath\command")
$zipCommand.SetValue('', '"' + $appPath + '" "%1"')
$zipCommand.Dispose()

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

Write-Host '[OK] .unitypackage、.zip、.prefab の Explorer プレビューを登録しました。'
Write-Host '[OK] 右クリックメニューを登録しました。'
Write-Host "App: $appPath"
