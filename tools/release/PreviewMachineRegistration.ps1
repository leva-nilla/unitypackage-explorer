[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Register','Unregister')][string]$Action,
    [string]$InstallDirectory,
    [string]$RegistryRoot = 'HKLM:',
    [string]$StatePath = (Join-Path $env:LOCALAPPDATA 'UnityPackageExplorer\ExplorerPreviewMachineRegistration.json')
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PreviewRegistration.Common.psm1') -Force

$clsid = '{70C67CC6-949B-4B5D-A8F2-7844D5C29E76}'
$appId = '{C50AF32D-4778-4B45-A2D3-FB0944F70271}'
$classes = 'Software\Classes\'
$clsidPath = Get-PreviewRegistryPath $RegistryRoot ($classes + 'CLSID\' + $clsid)
$inprocPath = Join-Path $clsidPath 'InprocServer32'
$appIdPath = Get-PreviewRegistryPath $RegistryRoot ($classes + 'AppID\' + $appId)
$keyPaths = @($clsidPath, $inprocPath, $appIdPath)

function Get-Targets([string]$Directory) {
    if (-not $Directory) { throw 'InstallDirectory is required to validate the fixed registration values.' }
    $comhost = Join-Path ([IO.Path]::GetFullPath($Directory)) 'PreviewHandler\UnityPackageExplorer.PreviewHandler.comhost.dll'
    if ($Action -eq 'Register' -and -not (Test-Path -LiteralPath $comhost -PathType Leaf)) { throw "Preview handler publish file is missing: $comhost" }
    $previousHost = Join-Path ([Environment]::GetFolderPath('Windows')) 'System32\prevhost.exe'
    return @(
        [pscustomobject]@{ Path=$inprocPath; Name=''; Value=$comhost; Kind='String' },
        [pscustomobject]@{ Path=$inprocPath; Name='ThreadingModel'; Value='Apartment'; Kind='String' },
        [pscustomobject]@{ Path=$clsidPath; Name='AppID'; Value=$appId; Kind='String' },
        [pscustomobject]@{ Path=$appIdPath; Name='DllSurrogate'; Value=$previousHost; Kind='String' }
    )
}

function Read-State([object[]]$Targets) {
    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) { return $null }
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    if ($state.Version -ne 1 -or $state.RegistryRoot -cne $RegistryRoot -or $state.Clsid -cne $clsid -or $state.AppId -cne $appId) { throw 'Machine registration state does not match this product and registry root.' }
    if (@($state.Values).Count -ne 4 -or @($state.Keys).Count -ne 3 -or @($state.Installed).Count -ne 4) { throw 'Machine registration state has an unexpected target count.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    for ($i = 0; $i -lt 4; $i++) {
        $record = $state.Values[$i]
        $target = $Targets[$i]
        $identity = ([string]$record.Path)+"`n"+[string]$record.Name
        if ([string]$record.Path -cne [string]$target.Path -or [string]$record.Name -cne [string]$target.Name -or $record.Exists -isnot [bool] -or -not $seen.Add($identity)) { throw 'Machine registration state contains an unknown or duplicate value target.' }
        if ($record.Exists -and ([string]$record.Kind -notin @('String','ExpandString','DWord','QWord','Binary','MultiString','None') -or $null -eq $record.Value)) { throw 'Machine registration state contains an invalid prior value.' }
        $installed = $state.Installed[$i]
        if ([string]$installed.Path -cne [string]$target.Path -or [string]$installed.Name -cne [string]$target.Name -or [string]$installed.Kind -cne 'String' -or [string]$installed.Value -cne [string]$target.Value) { throw 'Machine registration state has an invalid installed value record.' }
    }
    $seenKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    for ($i = 0; $i -lt 3; $i++) {
        $record = $state.Keys[$i]
        if ([string]$record.Path -cne [string]$keyPaths[$i] -or $record.Existed -isnot [bool] -or -not $seenKeys.Add([string]$record.Path)) { throw 'Machine registration state contains an unknown or duplicate key target.' }
    }
    return $state
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-SameSnapshot($Left, $Right) {
    if ([bool]$Left.Exists -ne [bool]$Right.Exists) { return $false }
    if (-not $Left.Exists) { return $true }
    if ([string]$Left.Kind -cne [string]$Right.Kind) { return $false }
    return (($Left.Value | ConvertTo-Json -Compress -Depth 10) -ceq ($Right.Value | ConvertTo-Json -Compress -Depth 10))
}

function Invoke-ElevatedMachineValues($State, [object[]]$Targets) {
    if ($RegistryRoot -ne 'HKLM:' -or (Test-Administrator)) { return $false }
    $systemPowerShell = Join-Path ([Environment]::GetEnvironmentVariable('SystemRoot')) 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $systemPowerShell -PathType Leaf)) { throw "Windows PowerShell was not found: $systemPowerShell" }

    # Everything the elevated process needs is captured in this immutable command line.
    # No script, module, request file, or user state file is loaded after UAC consent.
    $request = [pscustomobject]@{ Action=$Action; InstallDirectory=[IO.Path]::GetFullPath($InstallDirectory); RegistryRoot=$RegistryRoot; State=$State }
    $payloadBytes = [Text.Encoding]::Unicode.GetBytes(($request | ConvertTo-Json -Depth 10 -Compress))
    $compressedPayload = [IO.MemoryStream]::new()
    $gzip = [IO.Compression.GZipStream]::new($compressedPayload, [IO.Compression.CompressionMode]::Compress, $true)
    try { $gzip.Write($payloadBytes, 0, $payloadBytes.Length) } finally { $gzip.Dispose() }
    $payload = [Convert]::ToBase64String($compressedPayload.ToArray())
    $compressedPayload.Dispose()
    $elevatedCode = @'
$ErrorActionPreference = 'Stop'
$compressed = [IO.MemoryStream]::new([Convert]::FromBase64String('__REQUEST__'))
$gzip = [IO.Compression.GZipStream]::new($compressed, [IO.Compression.CompressionMode]::Decompress)
$decompressed = [IO.MemoryStream]::new()
try { $gzip.CopyTo($decompressed) } finally { $gzip.Dispose(); $compressed.Dispose() }
$raw = [Text.Encoding]::Unicode.GetString($decompressed.ToArray())
$decompressed.Dispose()
$request = $raw | ConvertFrom-Json
$clsid = '{70C67CC6-949B-4B5D-A8F2-7844D5C29E76}'
$appId = '{C50AF32D-4778-4B45-A2D3-FB0944F70271}'
$machinePrefix = 'Registry::HKEY_LOCAL_MACHINE\Software\Classes\'
$keyPaths = @(
    $machinePrefix + 'CLSID\' + $clsid,
    $machinePrefix + 'CLSID\' + $clsid + '\InprocServer32',
    $machinePrefix + 'AppID\' + $appId
)
$valueTargets = @(
    [pscustomobject]@{ Path=$keyPaths[1]; Name=''; Value=(Join-Path $request.InstallDirectory 'PreviewHandler\UnityPackageExplorer.PreviewHandler.comhost.dll') },
    [pscustomobject]@{ Path=$keyPaths[1]; Name='ThreadingModel'; Value='Apartment' },
    [pscustomobject]@{ Path=$keyPaths[0]; Name='AppID'; Value=$appId },
    [pscustomobject]@{ Path=$keyPaths[2]; Name='DllSurrogate'; Value=(Join-Path ([Environment]::GetFolderPath('Windows')) 'System32\prevhost.exe') }
)
$state = $request.State
if ($request.Action -notin @('Register','Unregister') -or $request.RegistryRoot -cne 'HKLM:' -or $state.RegistryRoot -cne 'HKLM:' -or $state.Clsid -cne $clsid -or $state.AppId -cne $appId) { throw 'Elevated request identity was rejected.' }
if (@($state.Values).Count -ne 4 -or @($state.Installed).Count -ne 4 -or @($state.Keys).Count -ne 3) { throw 'Elevated request target count was rejected.' }
$seenValues = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
for ($i = 0; $i -lt 4; $i++) {
    $target = $valueTargets[$i]
    $saved = $state.Values[$i]
    $installed = $state.Installed[$i]
    $identity = ([string]$saved.Path) + "`n" + [string]$saved.Name
    if ([string]$saved.Path -cne [string]$target.Path -or [string]$saved.Name -cne [string]$target.Name -or $saved.Exists -isnot [bool] -or -not $seenValues.Add($identity)) { throw 'Elevated request contains an unknown or duplicate registry value target.' }
    if ($saved.Exists -and ([string]$saved.Kind -notin @('String','ExpandString','DWord','QWord','Binary','MultiString','None') -or $null -eq $saved.Value)) { throw 'Elevated request contains an invalid prior value.' }
    if ([string]$installed.Path -cne [string]$target.Path -or [string]$installed.Name -cne [string]$target.Name -or [string]$installed.Kind -cne 'String' -or [string]$installed.Value -cne [string]$target.Value) { throw 'Elevated request contains an invalid installed value.' }
}
$seenKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
for ($i = 0; $i -lt 3; $i++) {
    $savedKey = $state.Keys[$i]
    if ([string]$savedKey.Path -cne [string]$keyPaths[$i] -or $savedKey.Existed -isnot [bool] -or -not $seenKeys.Add([string]$savedKey.Path)) { throw 'Elevated request contains an unknown or duplicate registry key target.' }
}
$base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
try {
    if ($request.Action -eq 'Register') {
        foreach ($target in $valueTargets) {
            $subkey = ([string]$target.Path).Substring('Registry::HKEY_LOCAL_MACHINE\'.Length)
            $key = $base.CreateSubKey($subkey, [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree)
            if (-not $key) { throw 'Could not create a fixed preview registration key.' }
            try { $key.SetValue([string]$target.Name, [string]$target.Value, [Microsoft.Win32.RegistryValueKind]::String) } finally { $key.Dispose() }
        }
    } else {
        for ($i = 0; $i -lt 4; $i++) {
            $target = $valueTargets[$i]
            $saved = $state.Values[$i]
            $subkey = ([string]$target.Path).Substring('Registry::HKEY_LOCAL_MACHINE\'.Length)
            $key = $base.OpenSubKey($subkey, $false)
            if (-not $key) { continue }
            $owned = $key.GetValueNames().Contains([string]$target.Name) -and
                $key.GetValueKind([string]$target.Name) -eq [Microsoft.Win32.RegistryValueKind]::String -and
                [string]$key.GetValue([string]$target.Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -ceq [string]$target.Value
            $key.Dispose()
            if (-not $owned) { continue }
            $key = $base.OpenSubKey($subkey, $true)
            if (-not $key) { continue }
            try {
                if ($saved.Exists) {
                    $prior = $saved.Value
                    if ($saved.PSObject.Properties.Name -contains 'Encoding' -and $saved.Encoding -eq 'Base64') { $prior = [Convert]::FromBase64String([string]$prior) }
                    elseif ([string]$saved.Kind -in @('Binary','None') -and $prior -isnot [byte[]]) { $prior = [byte[]]@($prior) }
                    elseif ([string]$saved.Kind -eq 'MultiString' -and $prior -isnot [string[]]) { $prior = [string[]]@($prior) }
                    $kind = [Enum]::Parse([Microsoft.Win32.RegistryValueKind], [string]$saved.Kind)
                    $key.SetValue([string]$target.Name, $prior, $kind)
                } else { $key.DeleteValue([string]$target.Name, $false) }
            } finally { $key.Dispose() }
        }
        foreach ($savedKey in @($state.Keys | Where-Object { -not $_.Existed } | Sort-Object { ([string]$_.Path).Length } -Descending)) {
            if ([string]$savedKey.Path -notin $keyPaths) { throw 'Elevated cleanup target was rejected.' }
            $subkey = ([string]$savedKey.Path).Substring('Registry::HKEY_LOCAL_MACHINE\'.Length)
            $key = $base.OpenSubKey($subkey, $false)
            if (-not $key) { continue }
            $empty = $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0
            $key.Dispose()
            if (-not $empty) { continue }
            $parentPath = Split-Path $subkey -Parent
            $leaf = Split-Path $subkey -Leaf
            $parent = $base.OpenSubKey($parentPath, $true)
            if ($parent) { try { $parent.DeleteSubKey($leaf, $false) } finally { $parent.Dispose() } }
        }
    }
} finally { $base.Dispose() }
'@
    $elevatedCode = $elevatedCode.Replace('__REQUEST__', $payload)
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($elevatedCode))
    if ($encodedCommand.Length -gt 30000) { throw 'The validated machine registration request is too large for a safe Windows PowerShell command line.' }
    $arguments = @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-EncodedCommand',$encodedCommand)
    $child = Start-Process -FilePath $systemPowerShell -Verb RunAs -WindowStyle Hidden -ArgumentList $arguments -Wait -PassThru
    if ($child.ExitCode -ne 0) { throw "Elevated machine registration helper failed with exit code $($child.ExitCode)." }
    return $true
}

if ([string]::IsNullOrWhiteSpace($InstallDirectory)) { throw 'InstallDirectory is required.' }
$targets = Get-Targets $InstallDirectory
$state = Read-State $targets

if ($Action -eq 'Register') {
    if (-not $state) {
        $values = foreach ($target in $targets) { Get-PreviewValueSnapshot $target.Path $target.Name }
        $keys = foreach ($path in $keyPaths) { [pscustomobject]@{ Path=$path; Existed=(Test-Path -LiteralPath $path) } }
        $state = [pscustomobject]@{ Version=1; RegistryRoot=$RegistryRoot; Clsid=$clsid; AppId=$appId; Values=@($values); Keys=@($keys); Installed=@($targets | ForEach-Object { [pscustomobject]@{ Path=$_.Path; Name=$_.Name; Kind=$_.Kind; Value=$_.Value } }) }
    } else {
        for ($i = 0; $i -lt 4; $i++) {
            $current = Get-PreviewValueSnapshot $targets[$i].Path $targets[$i].Name
            $isCurrentInstall = Test-PreviewValue $targets[$i].Path $targets[$i].Name $state.Installed[$i].Value $state.Installed[$i].Kind
            if (-not $isCurrentInstall -and -not (Test-SameSnapshot $current $state.Values[$i])) { throw "Machine preview registration was changed by another application; preserving it: $($targets[$i].Path)" }
        }
        $state.Installed = @($targets | ForEach-Object { [pscustomobject]@{ Path=$_.Path; Name=$_.Name; Kind=$_.Kind; Value=$_.Value } })
    }
    $stateDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($StatePath))
    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
    $state | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $StatePath -Encoding UTF8
    if (Invoke-ElevatedMachineValues $state $targets) { return }
    foreach ($target in $targets) { Set-PreviewValue $target.Path $target.Name $target.Value $target.Kind }
    Write-Output 'Machine preview COM values registered.'
    return
}

if (-not $state) { return }
if (Invoke-ElevatedMachineValues $state $targets) {
    Remove-Item -LiteralPath $StatePath -Force
    return
}
for ($i = 0; $i -lt 4; $i++) {
    $snapshot = $state.Values[$i]
    $installed = $state.Installed[$i]
    if (Test-PreviewValue $installed.Path $installed.Name $installed.Value $installed.Kind) { Restore-PreviewValue $snapshot }
}
foreach ($key in @($state.Keys | Where-Object { -not $_.Existed } | Sort-Object { ([string]$_.Path).Length } -Descending)) {
    if ([string]$key.Path -in $keyPaths) { Remove-PreviewEmptyKey ([string]$key.Path) }
}
Remove-Item -LiteralPath $StatePath -Force
Write-Output 'Machine preview COM values restored.'
