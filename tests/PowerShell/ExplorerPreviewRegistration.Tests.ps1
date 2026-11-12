$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$id = [Guid]::NewGuid().ToString('N')
$registryRoot = "Registry::HKEY_CURRENT_USER\Software\UnityPackageExplorerTests\$id"
$machineRegistryRoot = $registryRoot
$installDirectory = Join-Path ([IO.Path]::GetTempPath()) "UpePreviewTest-$id"
$statePath = Join-Path $installDirectory 'registration.json'
$machineStatePath = Join-Path $installDirectory 'machine-registration.json'
$register = Join-Path $repoRoot 'tools\release\Register-ExplorerPreview.ps1'
$unregister = Join-Path $repoRoot 'tools\release\Unregister-ExplorerPreview.ps1'
$machineHelperPath = Join-Path $repoRoot 'tools\release\PreviewMachineRegistration.ps1'
$machineHelperSource = Get-Content -LiteralPath $machineHelperPath -Raw
if ($machineHelperSource -notmatch "'-EncodedCommand'") { throw 'The elevated machine helper must use an immutable encoded command.' }
if ($machineHelperSource -match "'-File'|PSCommandPath.*Start-Process|Start-Process.*PSCommandPath") { throw 'The elevated machine helper must not load a mutable script path after consent.' }
$templateStart = $machineHelperSource.IndexOf('$elevatedCode = @''', [StringComparison]::Ordinal)
$templateBodyStart = $templateStart + '$elevatedCode = @'''.Length
$templateEnd = $machineHelperSource.IndexOf("`n'@", $templateBodyStart, [StringComparison]::Ordinal)
if ($templateStart -lt 0 -or $templateEnd -lt 0) { throw 'The immutable elevated command template was not found.' }
$elevatedTemplate = $machineHelperSource.Substring($templateBodyStart, $templateEnd - $templateBodyStart)
if ($elevatedTemplate -notmatch 'OpenBaseKey' -or $elevatedTemplate -notmatch 'CLSID\\.*InprocServer32' -or $elevatedTemplate -notmatch 'DllSurrogate') { throw 'The elevated snapshot does not contain the fixed COM registry operation.' }
if ($elevatedTemplate -match 'Import-Module|Get-Content|Invoke-Expression|PSCommandPath|StatePath|\s-File\b') { throw 'The elevated snapshot depends on a mutable script, module, or state file.' }
if ($machineHelperSource -notmatch '-WindowStyle Hidden') { throw 'The elevated snapshot must use a hidden Windows PowerShell process.' }
[scriptblock]::Create($elevatedTemplate.Replace('__REQUEST__','dGVzdA==')) | Out-Null
$installerScript = Get-Content -LiteralPath (Join-Path $repoRoot 'tools\release\UnityPackageExplorer.iss') -Raw
if ($installerScript -notmatch 'Name: "explorerpreview"[^\r\n]*Flags: unchecked') { throw 'Explorer preview must remain opt-in by default.' }
$clsid = '{70C67CC6-949B-4B5D-A8F2-7844D5C29E76}'
try {
    New-Item -ItemType Directory -Path (Join-Path $installDirectory 'PreviewHandler') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $installDirectory 'PreviewHandler\UnityPackageExplorer.PreviewHandler.comhost.dll') -Value 'fixture'
    Set-Content -LiteralPath (Join-Path $installDirectory 'PreviewHandler\UnityPackageExplorer.PreviewHandler.runtimeconfig.json') -Value '{}'
    Import-Module (Join-Path $repoRoot 'tools\release\PreviewRegistration.Common.psm1') -Force
    $legacyRoot = "$registryRoot\Legacy"
    $legacyMachineRoot = "$registryRoot\LegacyMachine"
    $legacyStatePath = Join-Path $installDirectory 'legacy-registration.json'
    $legacyMachineStatePath = Join-Path $installDirectory 'legacy-machine-registration.json'
    $legacyClassPath = "$legacyRoot\Software\Classes\CLSID\$clsid"
    $legacyInprocPath = "$legacyClassPath\InprocServer32"
    $legacyAppIdPath = "$legacyRoot\Software\Classes\AppID\{C50AF32D-4778-4B45-A2D3-FB0944F70271}"
    $legacyPreviewList = "$legacyRoot\Software\Microsoft\Windows\CurrentVersion\PreviewHandlers"
    $legacyZip = "$legacyRoot\Software\Classes\SystemFileAssociations\.zip\shellex\{8895B1C6-B41F-4C1C-A562-0D564250836F}"
    $legacyUnity = "$legacyRoot\Software\Classes\SystemFileAssociations\.unitypackage\shellex\{8895B1C6-B41F-4C1C-A562-0D564250836F}"
    $legacySharedProgId = "$legacyRoot\Software\Classes\Applications\Shared.exe\shellex\{8895B1C6-B41F-4C1C-A562-0D564250836F}"
    foreach ($path in @($legacyClassPath,$legacyInprocPath,$legacyAppIdPath,$legacyPreviewList,$legacyZip,$legacyUnity,$legacySharedProgId)) { New-Item -Path $path -Force | Out-Null }
    Set-Item -LiteralPath $legacyClassPath -Value 'Before preview install'
    New-ItemProperty -LiteralPath $legacyAppIdPath -Name DllSurrogate -PropertyType String -Value 'BeforeHost.exe' -Force | Out-Null
    New-ItemProperty -LiteralPath $legacyPreviewList -Name $clsid -PropertyType DWord -Value 7 -Force | Out-Null
    Set-Item -LiteralPath $legacyZip -Value '{11111111-1111-1111-1111-111111111111}'
    Set-Item -LiteralPath $legacySharedProgId -Value '{22222222-2222-2222-2222-222222222222}'
    $legacySnapshots = @(
        (Get-PreviewValueSnapshot $legacyClassPath ''),
        (Get-PreviewValueSnapshot $legacyClassPath 'AppID'),
        (Get-PreviewValueSnapshot $legacyInprocPath ''),
        (Get-PreviewValueSnapshot $legacyInprocPath 'ThreadingModel'),
        (Get-PreviewValueSnapshot $legacyInprocPath 'ProgID'),
        (Get-PreviewValueSnapshot $legacyAppIdPath 'DllSurrogate'),
        (Get-PreviewValueSnapshot $legacyPreviewList $clsid),
        (Get-PreviewValueSnapshot $legacyZip ''),
        (Get-PreviewValueSnapshot $legacyUnity ''),
        (Get-PreviewValueSnapshot $legacySharedProgId ''),
        (Get-PreviewValueSnapshot $legacySharedProgId '')
    )
    Set-Item -LiteralPath $legacyClassPath -Value 'UnityPackage Explorer Preview Handler'
    New-ItemProperty -LiteralPath $legacyClassPath -Name AppID -PropertyType String -Value '{C50AF32D-4778-4B45-A2D3-FB0944F70271}' -Force | Out-Null
    Set-Item -LiteralPath $legacyInprocPath -Value (Join-Path $installDirectory 'PreviewHandler\UnityPackageExplorer.PreviewHandler.comhost.dll')
    New-ItemProperty -LiteralPath $legacyInprocPath -Name ThreadingModel -PropertyType String -Value Apartment -Force | Out-Null
    New-ItemProperty -LiteralPath $legacyInprocPath -Name ProgID -PropertyType String -Value 'UnityPackageExplorer.PreviewHandler' -Force | Out-Null
    Set-ItemProperty -LiteralPath $legacyAppIdPath -Name DllSurrogate -Value 'Prevhost.exe'
    New-ItemProperty -LiteralPath $legacyPreviewList -Name $clsid -PropertyType String -Value 'UnityPackage Explorer Preview Handler' -Force | Out-Null
    Set-Item -LiteralPath $legacyZip -Value $clsid
    Set-Item -LiteralPath $legacyUnity -Value $clsid
    Set-Item -LiteralPath $legacySharedProgId -Value $clsid
    [pscustomobject]@{ Version=1; Clsid=$clsid; Values=$legacySnapshots } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $legacyStatePath -Encoding UTF8
    & $register -InstallDirectory $installDirectory -RegistryRoot $legacyRoot -MachineRegistryRoot $legacyMachineRoot -StatePath $legacyStatePath -MachineStatePath $legacyMachineStatePath -SkipRuntimeCheck
    $legacyCurrent = Get-Content -LiteralPath $legacyStatePath -Raw | ConvertFrom-Json
    $savedLabel = @($legacyCurrent.Values | Where-Object { $_.Path -eq $legacyClassPath -and $_.Name -eq '' })[0]
    if (-not $savedLabel.Exists -or $savedLabel.Value -ne 'Before preview install') { throw 'Legacy migration did not retain the original CLSID display name.' }
    if ((Get-Item -LiteralPath $legacyAppIdPath).GetValue('DllSurrogate') -ne 'BeforeHost.exe') { throw ('Legacy migration did not restore the original AppID value: ' + (Get-Item -LiteralPath $legacyAppIdPath).GetValue('DllSurrogate')) }
    & $unregister -InstallDirectory $installDirectory -RegistryRoot $legacyRoot -MachineRegistryRoot $legacyMachineRoot -StatePath $legacyStatePath -MachineStatePath $legacyMachineStatePath
    if ((Get-Item -LiteralPath $legacyClassPath).GetValue('') -ne 'Before preview install') { throw 'Legacy CLSID display name was not restored.' }
    if ((Get-Item -LiteralPath $legacyAppIdPath).GetValue('DllSurrogate') -ne 'BeforeHost.exe') { throw 'Legacy AppID value was not restored.' }
    if ((Get-Item -LiteralPath $legacyPreviewList).GetValueKind($clsid) -ne [Microsoft.Win32.RegistryValueKind]::DWord) { throw 'Legacy PreviewHandlers value type was not restored.' }
    if ((Get-Item -LiteralPath $legacyZip).GetValue('') -ne '{11111111-1111-1111-1111-111111111111}') { throw 'Legacy foreign ZIP association was not restored.' }
    if ((Get-Item -LiteralPath $legacySharedProgId).GetValue('') -ne '{22222222-2222-2222-2222-222222222222}') { throw 'Duplicate legacy ProgID snapshots did not preserve the other application value.' }
    if (Test-Path -LiteralPath $legacyInprocPath) { throw 'Legacy HKCU InprocServer32 key remained after restoring its originally empty state.' }

    $conflictingLegacyRoot = "$registryRoot\ConflictingLegacy"
    $conflictingLegacyState = Join-Path $installDirectory 'conflicting-legacy-registration.json'
    $conflictingProgIdPath = "$conflictingLegacyRoot\Software\Classes\Applications\Conflict.exe\shellex\{8895B1C6-B41F-4C1C-A562-0D564250836F}"
    New-Item -Path $conflictingProgIdPath -Force | Out-Null
    Set-Item -LiteralPath $conflictingProgIdPath -Value $clsid
    $snapshotA = [pscustomobject]@{ Path=$conflictingProgIdPath; Name=''; Exists=$true; Kind='String'; Value='{11111111-1111-1111-1111-111111111111}' }
    $snapshotB = [pscustomobject]@{ Path=$conflictingProgIdPath; Name=''; Exists=$true; Kind='String'; Value='{22222222-2222-2222-2222-222222222222}' }
    [pscustomobject]@{ Version=1; Clsid=$clsid; Values=@($snapshotA,$snapshotB) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $conflictingLegacyState -Encoding UTF8
    $conflictingLegacyRejected = $false
    try { & $unregister -InstallDirectory $installDirectory -RegistryRoot $conflictingLegacyRoot -MachineRegistryRoot $legacyMachineRoot -StatePath $conflictingLegacyState -MachineStatePath $legacyMachineStatePath } catch { $conflictingLegacyRejected = $true }
    if (-not $conflictingLegacyRejected) { throw 'Conflicting duplicate legacy snapshots were accepted.' }
    if ((Get-Item -LiteralPath $conflictingProgIdPath).GetValue('') -ne $clsid) { throw 'Rejected conflicting legacy state modified the shared ProgID value.' }

    $existingPath = "$registryRoot\Software\Classes\SystemFileAssociations\.zip\shellex\{8895B1C6-B41F-4C1C-A562-0D564250836F}"
    New-Item -Path $existingPath -Force | Out-Null
    Set-Item -LiteralPath $existingPath -Value '{11111111-1111-1111-1111-111111111111}'
    $handlerListPath = "$registryRoot\Software\Microsoft\Windows\CurrentVersion\PreviewHandlers"
    New-Item -Path $handlerListPath -Force | Out-Null
    New-ItemProperty -LiteralPath $handlerListPath -Name $clsid -PropertyType DWord -Value 7 -Force | Out-Null
    $machineAppIdKeyPath = "$machineRegistryRoot\Software\Classes\AppID\{C50AF32D-4778-4B45-A2D3-FB0944F70271}"
    New-Item -Path $machineAppIdKeyPath -Force | Out-Null
    New-ItemProperty -LiteralPath $machineAppIdKeyPath -Name DllSurrogate -PropertyType DWord -Value 19 -Force | Out-Null
    $machineInprocKeyPath = "$machineRegistryRoot\Software\Classes\CLSID\$clsid\InprocServer32"
    New-Item -Path $machineInprocKeyPath -Force | Out-Null
    New-ItemProperty -LiteralPath $machineInprocKeyPath -Name ThreadingModel -PropertyType Binary -Value ([byte[]](1,2,3)) -Force | Out-Null

    & $register -InstallDirectory $installDirectory -RegistryRoot $registryRoot -MachineRegistryRoot $machineRegistryRoot -StatePath $statePath -MachineStatePath $machineStatePath -SkipRuntimeCheck
    $registered = [string](Get-Item -LiteralPath $existingPath).GetValue('')
    if ($registered -ne $clsid) { throw "ZIP preview handler was not registered: $registered" }
    $inprocKey = Get-Item -LiteralPath "$machineRegistryRoot\Software\Classes\CLSID\$clsid\InprocServer32"
    if (-not ([string]$inprocKey.GetValue('')).EndsWith('PreviewHandler\UnityPackageExplorer.PreviewHandler.comhost.dll', [StringComparison]::OrdinalIgnoreCase)) { throw 'Machine COM registration does not point to the installed comhost.' }
    if ($inprocKey.GetValue('ThreadingModel') -ne 'Apartment') { throw 'Machine COM registration has the wrong threading model.' }
    if ($inprocKey.GetValueNames().Contains('ProgID')) { throw 'The COM registration contains an unnecessary ProgID value.' }
    $classKey = Get-Item -LiteralPath "$machineRegistryRoot\Software\Classes\CLSID\$clsid"
    if ($classKey.GetValue('AppID') -ne '{C50AF32D-4778-4B45-A2D3-FB0944F70271}') { throw 'Machine COM class does not use its dedicated AppID.' }
    $appIdKey = Get-Item -LiteralPath "$machineRegistryRoot\Software\Classes\AppID\{C50AF32D-4778-4B45-A2D3-FB0944F70271}"
    if (-not ([string]$appIdKey.GetValue('DllSurrogate')).EndsWith('System32\prevhost.exe', [StringComparison]::OrdinalIgnoreCase)) { throw 'Dedicated AppID is not configured for the system Preview Host.' }
    if ($appIdKey.GetValueNames().Contains('DisableLowILProcessIsolation')) { throw 'Preview registration disabled low-integrity isolation.' }
    $unmodifiedPath = "$registryRoot\Software\Classes\SystemFileAssociations\.unitypackage\shellex\{8895B1C6-B41F-4C1C-A562-0D564250836F}"
    if ([string](Get-Item -LiteralPath $unmodifiedPath).GetValue('') -ne $clsid) { throw 'UnityPackage preview handler was not registered.' }
    if (-not (Test-Path -LiteralPath $statePath)) { throw 'Registration backup was not created.' }
    $machineScript = $machineHelperPath
    $validMachineState = Get-Content -LiteralPath $machineStatePath -Raw
    $tamperedMachineState = $validMachineState | ConvertFrom-Json
    $tamperedMachineState.Values[0].Path = "$machineRegistryRoot\Software\Classes\Unrelated\InprocServer32"
    $tamperedMachineState | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $machineStatePath -Encoding UTF8
    $machineTamperingRejected = $false
    try { & $machineScript -Action Unregister -InstallDirectory $installDirectory -RegistryRoot $machineRegistryRoot -StatePath $machineStatePath } catch { $machineTamperingRejected = $true }
    if (-not $machineTamperingRejected) { throw 'Machine helper accepted a registry target outside its fixed whitelist.' }
    if (-not (Test-Path -LiteralPath "$machineRegistryRoot\Software\Classes\CLSID\$clsid\InprocServer32")) { throw 'Rejected machine state changed the valid CLSID registration.' }
    $validMachineState | Set-Content -LiteralPath $machineStatePath -Encoding UTF8

    & $register -InstallDirectory $installDirectory -RegistryRoot $registryRoot -MachineRegistryRoot $machineRegistryRoot -StatePath $statePath -MachineStatePath $machineStatePath -SkipRuntimeCheck
    $backupCount = @((Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json).Values).Count
    if ($backupCount -lt 5) { throw 'Upgrade registration overwrote or lost the original backup.' }

    Set-Item -LiteralPath $existingPath -Value '{22222222-2222-2222-2222-222222222222}'
    $validUserState = Get-Content -LiteralPath $statePath -Raw
    $tamperedUserState = $validUserState | ConvertFrom-Json
    $tamperedUserState.Values[0].Path = 'Registry::HKEY_LOCAL_MACHINE\Software\Classes\Unrelated\InprocServer32'
    $tamperedUserState | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $statePath -Encoding UTF8
    $tamperingRejected = $false
    try { & $unregister -InstallDirectory $installDirectory -RegistryRoot $registryRoot -MachineRegistryRoot $machineRegistryRoot -StatePath $statePath -MachineStatePath $machineStatePath } catch { $tamperingRejected = $true }
    if (-not $tamperingRejected) { throw 'An unknown or foreign-hive user state target was accepted.' }
    $validUserState | Set-Content -LiteralPath $statePath -Encoding UTF8
    & $unregister -InstallDirectory $installDirectory -RegistryRoot $registryRoot -MachineRegistryRoot $machineRegistryRoot -StatePath $statePath -MachineStatePath $machineStatePath
    $restoredMachineAppId = Get-Item -LiteralPath $machineAppIdKeyPath
    if ($restoredMachineAppId.GetValueKind('DllSurrogate') -ne [Microsoft.Win32.RegistryValueKind]::DWord -or [int]$restoredMachineAppId.GetValue('DllSurrogate') -ne 19) { throw 'Machine restoration did not preserve the previous value and type.' }
    $restoredMachineThreadingModel = Get-Item -LiteralPath $machineInprocKeyPath
    if ($restoredMachineThreadingModel.GetValueKind('ThreadingModel') -ne [Microsoft.Win32.RegistryValueKind]::Binary -or [Convert]::ToBase64String($restoredMachineThreadingModel.GetValue('ThreadingModel')) -ne [Convert]::ToBase64String([byte[]](1,2,3))) { throw 'Machine restoration did not preserve binary data.' }
    if ([string](Get-Item -LiteralPath $existingPath).GetValue('') -ne '{22222222-2222-2222-2222-222222222222}') { throw 'A registration changed by another app was overwritten during restore.' }
    $restoredHandlerList = Get-Item -LiteralPath $handlerListPath
    if ($restoredHandlerList.GetValueKind($clsid) -ne [Microsoft.Win32.RegistryValueKind]::DWord -or [int]$restoredHandlerList.GetValue($clsid) -ne 7) { throw 'The original PreviewHandlers value type was not restored.' }
    if (Test-Path -LiteralPath $unmodifiedPath) {
        $removed = Get-Item -LiteralPath $unmodifiedPath
        if ($removed.GetValueNames().Contains('')) { throw 'An originally absent association value was not removed.' }
    }
    if (Test-Path -LiteralPath $statePath) { throw 'Registration backup remained after restore.' }

    & $register -InstallDirectory $installDirectory -RegistryRoot $registryRoot -MachineRegistryRoot $machineRegistryRoot -StatePath $statePath -MachineStatePath $machineStatePath -SkipRuntimeCheck
    & $unregister -InstallDirectory $installDirectory -RegistryRoot $registryRoot -MachineRegistryRoot $machineRegistryRoot -StatePath $statePath -MachineStatePath $machineStatePath
    $restored = (Get-Item -LiteralPath $existingPath).GetValue('')
    if ($restored -ne '{22222222-2222-2222-2222-222222222222}') { throw 'Opt-out failed to preserve the value that existed before the latest registration.' }

    $runtimeInstalledPath = "$registryRoot\RuntimeInstalled"
    New-Item -Path $runtimeInstalledPath -Force | Out-Null
    New-ItemProperty -LiteralPath $runtimeInstalledPath -Name '10.0.14' -PropertyType String -Value '1' -Force | Out-Null
    & $register -InstallDirectory $installDirectory -RegistryRoot $registryRoot -MachineRegistryRoot $machineRegistryRoot -StatePath $statePath -MachineStatePath $machineStatePath -RuntimeKeyPath $runtimeInstalledPath -RuntimeFallbackKeyPath "$registryRoot\RuntimeFallbackMissing"
    if (-not (Test-Path -LiteralPath $statePath)) { throw 'A registered .NET 10 Desktop Runtime was not detected.' }
    & $unregister -InstallDirectory $installDirectory -RegistryRoot $registryRoot -MachineRegistryRoot $machineRegistryRoot -StatePath $statePath -MachineStatePath $machineStatePath

    $missingRuntimePath = "$registryRoot\RuntimeNotInstalled"
    $runtimeProbe = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile','-File',"`"$register`"",'-InstallDirectory',"`"$installDirectory`"",'-RegistryRoot',"`"$registryRoot`"",'-MachineRegistryRoot','HKLM:','-StatePath',"`"$statePath`"",'-MachineStatePath',"`"$machineStatePath`"",'-RuntimeKeyPath',"`"$missingRuntimePath`"",'-RuntimeFallbackKeyPath',"`"$missingRuntimePath`"") -Wait -PassThru -NoNewWindow
    if ($runtimeProbe.ExitCode -ne 3) { throw "Missing runtime was not gated (exit $($runtimeProbe.ExitCode))." }
    if (Test-Path -LiteralPath $statePath) { throw 'Runtime absence created preview registration backup state.' }
    Write-Output 'Explorer preview registration contract passed.'
} finally {
    if ($registryRoot.StartsWith('Registry::HKEY_CURRENT_USER\Software\UnityPackageExplorerTests\', [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $registryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ([IO.Path]::GetFullPath($installDirectory).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $installDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
