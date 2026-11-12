[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallDirectory,
    [string]$RegistryRoot = 'HKCU:',
    [string]$MachineRegistryRoot = 'HKLM:',
    [string]$StatePath = (Join-Path $env:LOCALAPPDATA 'UnityPackageExplorer\ExplorerPreviewRegistration.json'),
    [string]$MachineStatePath = (Join-Path $env:LOCALAPPDATA 'UnityPackageExplorer\ExplorerPreviewMachineRegistration.json'),
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
if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
    $priorState = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    if ($priorState.Version -lt 2) {
        $unregisterScript = Join-Path $PSScriptRoot 'Unregister-ExplorerPreview.ps1'
        & $unregisterScript -InstallDirectory $InstallDirectory -RegistryRoot $RegistryRoot -StatePath $StatePath
        if (Test-Path -LiteralPath $StatePath) { throw 'The legacy per-user preview registration could not be restored before migration.' }
    }
}
$extensions = @('.zip', '.unitypackage')
$targets = [Collections.Generic.List[object]]::new()
$targetIds = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
$add = {
    param($path,$name,$value,$kind)
    $identity = [string]$path + "`n" + [string]$name
    if ($targetIds.ContainsKey($identity)) {
        $existing = $targetIds[$identity]
        if ([string]$existing.Value -cne [string]$value -or [string]$existing.Kind -cne [string]$kind) { throw "Conflicting values were resolved for a preview registry target: $path" }
        return
    }
    $target = [pscustomobject]@{ Path=$path; Name=$name; Value=$value; Kind=$kind }
    $targetIds.Add($identity, $target)
    $targets.Add($target)
}
$clsid = '{70C67CC6-949B-4B5D-A8F2-7844D5C29E76}'
$appId = '{C50AF32D-4778-4B45-A2D3-FB0944F70271}'
$classes = 'Software\Classes\'
& $add (Get-PreviewRegistryPath $RegistryRoot ($classes + 'CLSID\' + $clsid)) '' 'UnityPackage Explorer Preview Handler' 'String'
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

$userProgIds = [Collections.Generic.List[string]]::new()
$userProgIdSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
    $priorState = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    if ($priorState.PSObject.Properties.Name -contains 'UserProgIds') {
        foreach ($priorProgId in $priorState.UserProgIds) {
            $segments = ([string]$priorProgId).Split('\')
            if ([string]$priorProgId -notmatch '^[A-Za-z0-9._-]+(\\[A-Za-z0-9._-]+)*$' -or $segments -contains '.' -or $segments -contains '..') { throw 'Existing preview state contains an invalid ProgID.' }
            if ($userProgIdSet.Add([string]$priorProgId)) { $userProgIds.Add([string]$priorProgId) }
        }
    }
}
foreach ($target in $targets | Where-Object { $_.Path -match '\\shellex\\\{8895B1C6-B41F-4C1C-A562-0D564250836F\}$' }) {
    $prefix = (Get-PreviewRegistryPath $RegistryRoot ($classes)).TrimEnd('\') + '\'
    $relative = $target.Path.Substring($prefix.Length)
    if (-not $relative.StartsWith('SystemFileAssociations\', [StringComparison]::OrdinalIgnoreCase)) {
        $progId = $relative.Substring(0, $relative.Length - '\shellex\{8895B1C6-B41F-4C1C-A562-0D564250836F}'.Length)
        $segments = $progId.Split('\')
        if ($progId -notmatch '^[A-Za-z0-9._-]+(\\[A-Za-z0-9._-]+)*$' -or $segments -contains '.' -or $segments -contains '..') { throw 'Resolved preview ProgID is invalid.' }
        if ($userProgIdSet.Add($progId)) { $userProgIds.Add($progId) }
    }
}
foreach ($progId in $userProgIds) {
    $progTarget = Get-PreviewRegistryPath $RegistryRoot ($classes + $progId + '\shellex\{8895B1C6-B41F-4C1C-A562-0D564250836F}')
    if (-not ($targets | Where-Object { $_.Path -eq $progTarget })) { & $add $progTarget '' $clsid 'String' }
}

$stateDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($StatePath))
New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
$ownedKeyPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($target in $targets) {
    $keyPath = [string]$target.Path
    while ($keyPath -match '\\Software\\Classes\\.+$') {
        [void]$ownedKeyPaths.Add($keyPath)
        $keyPath = Split-Path $keyPath -Parent
    }
}
if (Test-Path -LiteralPath $StatePath) {
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    $backups = [Collections.Generic.List[object]]::new()
    foreach ($item in $state.Values) { $backups.Add($item) }
    $keyBackups = [Collections.Generic.List[object]]::new()
    if ($state.PSObject.Properties.Name -contains 'Keys') { foreach ($item in $state.Keys) { $keyBackups.Add($item) } }
    $knownKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $keyBackups) { [void]$knownKeys.Add([string]$item.Path) }
    foreach ($path in $ownedKeyPaths) { if ($knownKeys.Add($path)) { $keyBackups.Add([pscustomobject]@{ Path=$path; Existed=(Test-Path -LiteralPath $path) }) } }
    $known = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $backups) { [void]$known.Add(([string]$item.Path) + "`n" + ([string]$item.Name)) }
    $changed = $false
    foreach ($target in $targets) {
        $identity = $target.Path + "`n" + $target.Name
        if ($known.Add($identity)) { $backups.Add((Get-PreviewValueSnapshot $target.Path $target.Name)); $changed = $true }
    }
    if ($changed) {
        $state = [pscustomobject]@{ Version=2; RegistryRoot=$RegistryRoot; Clsid=$clsid; AppId=$appId; UserProgIds=@($userProgIds); Values=@($backups); Keys=@($keyBackups) }
        $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $StatePath -Encoding UTF8
    }
    elseif ($state.PSObject.Properties.Name -notcontains 'Keys' -and $keyBackups.Count -gt 0) {
        $state = [pscustomobject]@{ Version=2; RegistryRoot=$RegistryRoot; Clsid=$clsid; AppId=$appId; UserProgIds=@($userProgIds); Values=@($backups); Keys=@($keyBackups) }
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
    $keyBackups = foreach ($path in $ownedKeyPaths) { [pscustomobject]@{ Path=$path; Existed=(Test-Path -LiteralPath $path) } }
    $state = [pscustomobject]@{ Version=2; RegistryRoot=$RegistryRoot; Clsid=$clsid; AppId=$appId; UserProgIds=@($userProgIds); Values=@($backups); Keys=@($keyBackups) }
    $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $StatePath -Encoding UTF8
}
$machineScript = Join-Path $PSScriptRoot 'PreviewMachineRegistration.ps1'
& $machineScript -Action Register -InstallDirectory $InstallDirectory -RegistryRoot $MachineRegistryRoot -StatePath $MachineStatePath
foreach ($target in $targets) { Set-PreviewValue $target.Path $target.Name $target.Value $target.Kind }
Write-Output 'Explorer preview handler registered for ZIP and UnityPackage.'
