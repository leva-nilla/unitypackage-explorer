[CmdletBinding()]
param(
    [string]$InstallDirectory,
    [string]$RegistryRoot = 'HKCU:',
    [string]$MachineRegistryRoot = 'HKLM:',
    [string]$StatePath = (Join-Path $env:LOCALAPPDATA 'UnityPackageExplorer\ExplorerPreviewRegistration.json'),
    [string]$MachineStatePath = (Join-Path $env:LOCALAPPDATA 'UnityPackageExplorer\ExplorerPreviewMachineRegistration.json')
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PreviewRegistration.Common.psm1') -Force
if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
    if (Test-Path -LiteralPath $MachineStatePath -PathType Leaf) {
        & (Join-Path $PSScriptRoot 'PreviewMachineRegistration.ps1') -Action Unregister -InstallDirectory $InstallDirectory -RegistryRoot $MachineRegistryRoot -StatePath $MachineStatePath
    }
    return
}

$state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
$clsid = '{70C67CC6-949B-4B5D-A8F2-7844D5C29E76}'
$appId = '{C50AF32D-4778-4B45-A2D3-FB0944F70271}'
$shellGuid = '{8895B1C6-B41F-4C1C-A562-0D564250836F}'
$classes = 'Software\Classes\'
$root = $RegistryRoot
$targets = [Collections.Generic.List[object]]::new()
$targetIds = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
$add = {
    param($path,$name,$value)
    $identity = [string]$path + "`n" + [string]$name
    if ($targetIds.ContainsKey($identity)) {
        if ([string]$targetIds[$identity].Value -cne [string]$value) { throw "Conflicting saved values target the same registry value: $path" }
        return
    }
    $target = [pscustomobject]@{ Path=$path; Name=$name; Value=$value }
    $targetIds.Add($identity, $target)
    $targets.Add($target)
}
$classPath = Get-PreviewRegistryPath $root ($classes + 'CLSID\' + $clsid)
& $add $classPath '' 'UnityPackage Explorer Preview Handler'
& $add (Get-PreviewRegistryPath $root 'Software\Microsoft\Windows\CurrentVersion\PreviewHandlers') $clsid 'UnityPackage Explorer Preview Handler'
foreach ($extension in @('.zip','.unitypackage')) {
    & $add (Get-PreviewRegistryPath $root ($classes + 'SystemFileAssociations\' + $extension + '\shellex\' + $shellGuid)) '' $clsid
}

$version = if ($state.PSObject.Properties.Name -contains 'Version') { [int]$state.Version } else { 1 }
if ($version -notin @(1,2)) { throw 'Preview registration state has an unsupported version.' }
if ($version -eq 2) {
    if ([string]$state.RegistryRoot -cne $root -or [string]$state.Clsid -cne $clsid -or [string]$state.AppId -cne $appId) { throw 'Preview registration state does not match this user and product.' }
    $progIds = @($state.UserProgIds)
    $seenProgIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($progId in $progIds) {
        $segments = ([string]$progId).Split('\')
        if ([string]$progId -notmatch '^[A-Za-z0-9._-]+(\\[A-Za-z0-9._-]+)*$' -or $segments -contains '.' -or $segments -contains '..' -or -not $seenProgIds.Add([string]$progId)) { throw 'Preview registration state contains an invalid ProgID.' }
        & $add (Get-PreviewRegistryPath $root ($classes + $progId + '\shellex\' + $shellGuid)) '' $clsid
    }
} else {
    # Version 1 stored the COM class in HKCU. Permit only its known legacy values.
    if ($InstallDirectory) {
        $oldInproc = Join-Path $classPath 'InprocServer32'
        & $add $oldInproc '' (Join-Path ([IO.Path]::GetFullPath($InstallDirectory)) 'PreviewHandler\UnityPackageExplorer.PreviewHandler.comhost.dll')
        & $add $oldInproc 'ThreadingModel' 'Apartment'
        & $add $oldInproc 'ProgID' 'UnityPackageExplorer.PreviewHandler'
    }
    & $add $classPath 'AppID' $appId
    & $add (Get-PreviewRegistryPath $root ($classes + 'AppID\' + $appId)) 'DllSurrogate' 'Prevhost.exe'
    foreach ($snapshot in @($state.Values)) {
        $path = [string]$snapshot.Path
        $suffix = '\shellex\' + $shellGuid
        if ($path.EndsWith($suffix, [StringComparison]::OrdinalIgnoreCase)) {
            $prefix = (Get-PreviewRegistryPath $root $classes).TrimEnd('\') + '\'
            if ($path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                $relative = $path.Substring($prefix.Length)
                if (-not $relative.StartsWith('SystemFileAssociations\', [StringComparison]::OrdinalIgnoreCase)) {
                    $progId = $relative.Substring(0, $relative.Length - $suffix.Length)
                    $segments = $progId.Split('\')
                    if ($progId -notmatch '^[A-Za-z0-9._-]+(\\[A-Za-z0-9._-]+)*$' -or $segments -contains '.' -or $segments -contains '..') { throw 'Legacy preview state contains an invalid ProgID.' }
                    & $add (Get-PreviewRegistryPath $root ($classes + $progId + $suffix)) '' $clsid
                }
            }
        }
    }
}

$expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($target in $targets) { $expected.Add(([string]$target.Path)+"`n"+[string]$target.Name, $target) }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$snapshotByIdentity = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($snapshot in @($state.Values)) {
    $path = [string]$snapshot.Path
    $name = [string]$snapshot.Name
    $identity = $path + "`n" + $name
    if (-not $expected.ContainsKey($identity)) { throw 'Preview registration state contains an unknown user registry target.' }
    if (-not $seen.Add($identity)) {
        $priorSnapshot = $snapshotByIdentity[$identity]
        if ($version -ne 1 -or (($priorSnapshot | ConvertTo-Json -Compress -Depth 10) -cne ($snapshot | ConvertTo-Json -Compress -Depth 10))) { throw 'Preview registration state contains a duplicate or conflicting user registry target.' }
        continue
    }
    $snapshotByIdentity.Add($identity, $snapshot)
    if ($path -notlike 'Registry::HKEY_CURRENT_USER\*' -and $root -eq 'HKCU:') { throw 'Preview registration state attempted to access another registry hive.' }
    if ($snapshot.Exists -isnot [bool]) { throw 'Preview registration state has an invalid Exists flag.' }
    if ($snapshot.Exists) {
        if ([string]$snapshot.Kind -notin @('String','ExpandString','DWord','QWord','Binary','MultiString','None')) { throw 'Preview registration state contains an invalid registry value kind.' }
        if ($null -eq $snapshot.Value) { throw 'Preview registration state is missing a prior value.' }
    }
}

$safeKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($target in $targets) {
    $key = [string]$target.Path
    while ($key -match '\\Software\\Classes\\.+$') {
        [void]$safeKeys.Add($key)
        $key = Split-Path $key -Parent
    }
    if ([string]$target.Path -match '\\PreviewHandlers$') { [void]$safeKeys.Add([string]$target.Path) }
}
if ($version -eq 2 -and $seen.Count -ne $expected.Count) { throw 'Preview registration state is incomplete.' }
if ($state.PSObject.Properties.Name -contains 'Keys') {
    $seenKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($key in @($state.Keys)) {
        if ([string]$key.Path -notin $safeKeys -or $key.Existed -isnot [bool] -or -not $seenKeys.Add([string]$key.Path)) { throw 'Preview registration state contains an unknown or duplicate user key target.' }
    }
}
foreach ($snapshot in @($state.Values)) {
    $target = $expected[[string]$snapshot.Path + "`n" + [string]$snapshot.Name]
    if (Test-PreviewValue $target.Path $target.Name $target.Value 'String') { Restore-PreviewValue $snapshot }
}
if ($version -eq 1) {
    # The old installer did not record key presence. Prune only its fixed empty COM leaves.
    Remove-PreviewEmptyKey (Join-Path $classPath 'InprocServer32')
    Remove-PreviewEmptyKey (Get-PreviewRegistryPath $root ($classes + 'AppID\' + $appId))
    Remove-PreviewEmptyKey $classPath
}
if ($state.PSObject.Properties.Name -contains 'Keys') {
    foreach ($key in @($state.Keys | Where-Object { -not $_.Existed } | Sort-Object { ([string]$_.Path).Length } -Descending)) {
        if ([string]$key.Path -in $safeKeys) { Remove-PreviewEmptyKey ([string]$key.Path) }
    }
}

# Only restore machine COM values through the fixed-target elevated helper.
if (Test-Path -LiteralPath $MachineStatePath -PathType Leaf) {
    & (Join-Path $PSScriptRoot 'PreviewMachineRegistration.ps1') -Action Unregister -InstallDirectory $InstallDirectory -RegistryRoot $MachineRegistryRoot -StatePath $MachineStatePath
}
Remove-Item -LiteralPath $StatePath -Force
Write-Output 'Explorer preview handler registration restored.'
