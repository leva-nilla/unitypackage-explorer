Set-StrictMode -Version Latest

$script:HandlerClsid = '{70C67CC6-949B-4B5D-A8F2-7844D5C29E76}'
$script:HandlerAppId = '{C50AF32D-4778-4B45-A2D3-FB0944F70271}'
$script:PreviewHandlersKey = 'Software\Microsoft\Windows\CurrentVersion\PreviewHandlers'
$script:PreviewShellGuid = '{8895B1C6-B41F-4C1C-A562-0D564250836F}'

function Get-PreviewRegistryPath([string]$Root, [string]$RelativePath) {
    if ($Root -eq 'HKCU:') { return "Registry::HKEY_CURRENT_USER\$RelativePath" }
    if ($Root -eq 'HKLM:') { return "Registry::HKEY_LOCAL_MACHINE\$RelativePath" }
    return (Join-Path $Root $RelativePath)
}

function Get-PreviewValueSnapshot([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path)) { return [pscustomobject]@{ Path=$Path; Name=$Name; Exists=$false } }
    $key = Get-Item -LiteralPath $Path
    if (-not $key.GetValueNames().Contains($Name)) { return [pscustomobject]@{ Path=$Path; Name=$Name; Exists=$false } }
    $kind = $key.GetValueKind($Name).ToString()
    $value = $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    return [pscustomobject]@{ Path=$Path; Name=$Name; Exists=$true; Kind=$kind; Value=$value }
}

function Set-PreviewValue([string]$Path, [string]$Name, [object]$Value, [string]$Kind) {
    if ($Path -notmatch '^Registry::HKEY_(CURRENT_USER|LOCAL_MACHINE)\\(.+)$') { throw "Unsupported registry path: $Path" }
    $hive = if ($Matches[1] -eq 'CURRENT_USER') { [Microsoft.Win32.RegistryHive]::CurrentUser } else { [Microsoft.Win32.RegistryHive]::LocalMachine }
    $subKey = $Matches[2].Replace('/', '\')
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, [Microsoft.Win32.RegistryView]::Default)
    $key = $baseKey.CreateSubKey($subKey, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree)
    if (-not $key) { $baseKey.Dispose(); throw "Could not create registry key: $Path" }
    $registryKind = [Enum]::Parse([Microsoft.Win32.RegistryValueKind], $Kind)
    $key.SetValue($Name, $Value, $registryKind)
    $key.Dispose()
    $baseKey.Dispose()
}

function Test-PreviewValue([string]$Path, [string]$Name, [object]$Value, [string]$Kind) {
    $snapshot = Get-PreviewValueSnapshot $Path $Name
    if (-not $snapshot.Exists -or $snapshot.Kind -ne $Kind) { return $false }
    if ($Value -is [byte[]]) { return [Convert]::ToBase64String($snapshot.Value) -eq [Convert]::ToBase64String($Value) }
    return [string]$snapshot.Value -ceq [string]$Value
}

function Restore-PreviewValue($Snapshot) {
    if ($Snapshot.Exists) {
        $value = $Snapshot.Value
        if ($Snapshot.PSObject.Properties.Name -contains 'Encoding' -and $Snapshot.Encoding -eq 'Base64') {
            $value = [Convert]::FromBase64String([string]$Snapshot.Value)
        } else {
            if ([string]$Snapshot.Kind -in @('Binary','None') -and $value -isnot [byte[]]) { $value = [byte[]]@($value) }
            if ([string]$Snapshot.Kind -eq 'MultiString' -and $value -isnot [string[]]) { $value = [string[]]@($value) }
        }
        Set-PreviewValue $Snapshot.Path $Snapshot.Name $value $Snapshot.Kind
    } elseif (Test-Path -LiteralPath $Snapshot.Path) {
        if ($Snapshot.Path -notmatch '^Registry::HKEY_(CURRENT_USER|LOCAL_MACHINE)\\(.+)$') { throw "Unsupported registry path: $($Snapshot.Path)" }
        $hive = if ($Matches[1] -eq 'CURRENT_USER') { [Microsoft.Win32.RegistryHive]::CurrentUser } else { [Microsoft.Win32.RegistryHive]::LocalMachine }
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, [Microsoft.Win32.RegistryView]::Default)
        $key = $baseKey.OpenSubKey($Matches[2], $true)
        if ($key) { $key.DeleteValue([string]$Snapshot.Name, $false); $key.Dispose() }
        $baseKey.Dispose()
    }
}

function Remove-PreviewEmptyKey([string]$Path) {
    if ($Path -notmatch '^Registry::HKEY_(CURRENT_USER|LOCAL_MACHINE)\\(.+)$') { throw "Unsupported registry path: $Path" }
    $hive = if ($Matches[1] -eq 'CURRENT_USER') { [Microsoft.Win32.RegistryHive]::CurrentUser } else { [Microsoft.Win32.RegistryHive]::LocalMachine }
    $subKey = $Matches[2].Replace('/', '\')
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, [Microsoft.Win32.RegistryView]::Default)
    try {
        $key = $baseKey.OpenSubKey($subKey, $false)
        if (-not $key) { return }
        $empty = $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0
        $key.Dispose()
        if (-not $empty) { return }
        $parentPath = Split-Path $subKey -Parent
        $leaf = Split-Path $subKey -Leaf
        if (-not $parentPath -or -not $leaf) { return }
        $parent = $baseKey.OpenSubKey($parentPath, $true)
        if ($parent) { try { $parent.DeleteSubKey($leaf, $false) } finally { $parent.Dispose() } }
    } finally { $baseKey.Dispose() }
}

function Get-EffectiveProgId([string]$Extension) {
    $choice = Get-PreviewRegistryPath 'HKCU:' "Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$Extension\UserChoice"
    if (Test-Path -LiteralPath $choice) {
        $value = [string](Get-Item -LiteralPath $choice).GetValue('ProgId')
        if ($value -match '^[A-Za-z0-9._-]+(\\[A-Za-z0-9._-]+)*$' -and (Test-Path -LiteralPath "Registry::HKEY_CLASSES_ROOT\$value")) { return $value }
    }
    $extPath = "Registry::HKEY_CLASSES_ROOT\$Extension"
    if (Test-Path -LiteralPath $extPath) {
        $value = [string](Get-Item -LiteralPath $extPath).GetValue('')
        if ($value -match '^[A-Za-z0-9._-]+(\\[A-Za-z0-9._-]+)*$' -and (Test-Path -LiteralPath "Registry::HKEY_CLASSES_ROOT\$value")) { return $value }
    }
    return $null
}

Export-ModuleMember -Function Get-PreviewRegistryPath,Get-PreviewValueSnapshot,Set-PreviewValue,Test-PreviewValue,Restore-PreviewValue,Remove-PreviewEmptyKey,Get-EffectiveProgId
