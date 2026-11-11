[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallDirectory,
    [string]$RegistryRoot = 'HKCU:',
    [string]$StatePath = (Join-Path $env:LOCALAPPDATA 'UnityPackageExplorer\ExplorerPreviewRegistration.json'),
    [string]$RuntimeKeyPath = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
    [string]$RuntimeFallbackKeyPath = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
    [switch]$SkipRuntimeCheck
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PreviewRegistration.Common.psm1') -Force
$install = [IO.Path]::GetFullPath($InstallDirectory)
$runtimeAvailable = $false
foreach ($runtimePath in @($RuntimeKeyPath, $RuntimeFallbackKeyPath)) {
    if (-not (Test-Path -LiteralPath $runtimePath)) { continue }
    $runtimeRegistryKey = Get-Item -LiteralPath $runtimePath
    foreach ($versionName in $runtimeRegistryKey.GetValueNames()) {
        $parsed = [version]::new()
        if ([version]::TryParse(($versionName -split '-')[0], [ref]$parsed) -and $parsed.Major -eq 10) { $runtimeAvailable = $true; break }
    }
    if ($runtimeAvailable) { break }
}
if (-not $runtimeAvailable -and -not $SkipRuntimeCheck) { Write-Output 'RUNTIME_MISSING'; exit 3 }
$comhost = Join-Path $install 'PreviewHandler\UnityPackageExplorer.PreviewHandler.comhost.dll'
$runtimeConfig = Join-Path $install 'PreviewHandler\UnityPackageExplorer.PreviewHandler.runtimeconfig.json'
foreach ($required in @($comhost, $runtimeConfig)) { if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Preview handler publish file is missing: $required" } }

$extensions = @('.zip', '.unitypackage')
$targets = [Collections.Generic.List[object]]::new()
$add = { param($path,$name,$value,$kind) $targets.Add([pscustomobject]@{ Path=$path; Name=$name; Value=$value; Kind=$kind }) }
$clsid = '{70C67CC6-949B-4B5D-A8F2-7844D5C29E76}'
$appId = '{C50AF32D-4778-4B45-A2D3-FB0944F70271}'
$classes = 'Software\Classes\'
$clsidPath = Get-PreviewRegistryPath $RegistryRoot ($classes + 'CLSID\' + $clsid)
$inproc = Join-Path $clsidPath 'InprocServer32'
& $add $clsidPath '' 'UnityPackage Explorer Preview Handler' 'String'
& $add $inproc '' $comhost 'String'
& $add $inproc 'ThreadingModel' 'Apartment' 'String'
& $add $inproc 'ProgID' 'UnityPackageExplorer.PreviewHandler' 'String'
& $add $clsidPath 'AppID' $appId 'String'
& $add (Get-PreviewRegistryPath $RegistryRoot ($classes + 'AppID\' + $appId)) 'DllSurrogate' 'Prevhost.exe' 'String'
& $add (Get-PreviewRegistryPath $RegistryRoot 'Software\Microsoft\Windows\CurrentVersion\PreviewHandlers') $clsid 'UnityPackage Explorer Preview Handler' 'String'
foreach ($extension in $extensions) {
    $shellKey = Get-PreviewRegistryPath $RegistryRoot ($classes + 'SystemFileAssociations\' + $extension + '\shellex\{8895B1C6-B41F-4C1C-A562-0D564250836F}')
    & $add $shellKey '' $clsid 'String'
    $progId = Get-EffectiveProgId $extension
    if ($progId) {
        $progKey = Get-PreviewRegistryPath $RegistryRoot ($classes + $progId + '\shellex\{8895B1C6-B41F-4C1C-A562-0D564250836F}')
        & $add $progKey '' $clsid 'String'
    }
}

$stateDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($StatePath))
New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
if (Test-Path -LiteralPath $StatePath) {
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    $backups = [Collections.Generic.List[object]]::new()
    foreach ($item in $state.Values) { $backups.Add($item) }
    $known = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $backups) { [void]$known.Add(([string]$item.Path) + "`n" + ([string]$item.Name)) }
    $changed = $false
    foreach ($target in $targets) {
        $identity = $target.Path + "`n" + $target.Name
        if ($known.Add($identity)) { $backups.Add((Get-PreviewValueSnapshot $target.Path $target.Name)); $changed = $true }
    }
    if ($changed) {
        $state = [pscustomobject]@{ Version=1; Clsid=$clsid; Values=@($backups) }
        $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $StatePath -Encoding UTF8
    }
    foreach ($target in $targets) {
        $current = Get-PreviewValueSnapshot $target.Path $target.Name
        if ($current.Exists -and -not (Test-PreviewValue $target.Path $target.Name $target.Value $target.Kind)) {
            throw "Preview registration was changed by another application; preserving it: $($target.Path)"
        }
    }
} else {
    $backups = foreach ($target in $targets) { Get-PreviewValueSnapshot $target.Path $target.Name }
    $state = [pscustomobject]@{ Version=1; Clsid=$clsid; Values=@($backups) }
    $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $StatePath -Encoding UTF8
}
foreach ($target in $targets) { Set-PreviewValue $target.Path $target.Name $target.Value $target.Kind }
Write-Output 'Explorer preview handler registered for ZIP and UnityPackage.'
