$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$id = [Guid]::NewGuid().ToString('N')
$registryRoot = "Registry::HKEY_CURRENT_USER\Software\UnityPackageExplorerTests\$id"
$installDirectory = Join-Path ([IO.Path]::GetTempPath()) "UpePreviewTest-$id"
$statePath = Join-Path $installDirectory 'registration.json'
$register = Join-Path $repoRoot 'tools\release\Register-ExplorerPreview.ps1'
$unregister = Join-Path $repoRoot 'tools\release\Unregister-ExplorerPreview.ps1'
$installerScript = Get-Content -LiteralPath (Join-Path $repoRoot 'tools\release\UnityPackageExplorer.iss') -Raw
if ($installerScript -notmatch 'Name: "explorerpreview"[^\r\n]*Flags: unchecked') { throw 'Explorer preview must remain opt-in by default.' }
$clsid = '{70C67CC6-949B-4B5D-A8F2-7844D5C29E76}'
try {
    New-Item -ItemType Directory -Path (Join-Path $installDirectory 'PreviewHandler') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $installDirectory 'PreviewHandler\UnityPackageExplorer.PreviewHandler.comhost.dll') -Value 'fixture'
    Set-Content -LiteralPath (Join-Path $installDirectory 'PreviewHandler\UnityPackageExplorer.PreviewHandler.runtimeconfig.json') -Value '{}'
    $existingPath = "$registryRoot\Software\Classes\SystemFileAssociations\.zip\shellex\{8895B1C6-B41F-4C1C-A562-0D564250836F}"
    New-Item -Path $existingPath -Force | Out-Null
    Set-Item -LiteralPath $existingPath -Value '{11111111-1111-1111-1111-111111111111}'
    $handlerListPath = "$registryRoot\Software\Microsoft\Windows\CurrentVersion\PreviewHandlers"
    New-Item -Path $handlerListPath -Force | Out-Null
    New-ItemProperty -LiteralPath $handlerListPath -Name $clsid -PropertyType DWord -Value 7 -Force | Out-Null

    & $register -InstallDirectory $installDirectory -RegistryRoot $registryRoot -StatePath $statePath -SkipRuntimeCheck
    $registered = [string](Get-Item -LiteralPath $existingPath).GetValue('')
    if ($registered -ne $clsid) { throw "ZIP preview handler was not registered: $registered" }
    $appIdKey = Get-Item -LiteralPath "$registryRoot\Software\Classes\AppID\{C50AF32D-4778-4B45-A2D3-FB0944F70271}"
    if ($appIdKey.GetValue('DllSurrogate') -ne 'Prevhost.exe') { throw 'Dedicated AppID is not configured for Prevhost.exe.' }
    if ($appIdKey.GetValueNames().Contains('DisableLowILProcessIsolation')) { throw 'Preview registration disabled low-integrity isolation.' }
    $unmodifiedPath = "$registryRoot\Software\Classes\SystemFileAssociations\.unitypackage\shellex\{8895B1C6-B41F-4C1C-A562-0D564250836F}"
    if ([string](Get-Item -LiteralPath $unmodifiedPath).GetValue('') -ne $clsid) { throw 'UnityPackage preview handler was not registered.' }
    if (-not (Test-Path -LiteralPath $statePath)) { throw 'Registration backup was not created.' }

    & $register -InstallDirectory $installDirectory -RegistryRoot $registryRoot -StatePath $statePath -SkipRuntimeCheck
    $backupCount = @((Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json).Values).Count
    if ($backupCount -lt 5) { throw 'Upgrade registration overwrote or lost the original backup.' }

    Set-Item -LiteralPath $existingPath -Value '{22222222-2222-2222-2222-222222222222}'
    & $unregister -InstallDirectory $installDirectory -RegistryRoot $registryRoot -StatePath $statePath
    if ([string](Get-Item -LiteralPath $existingPath).GetValue('') -ne '{22222222-2222-2222-2222-222222222222}') { throw 'A registration changed by another app was overwritten during restore.' }
    $restoredHandlerList = Get-Item -LiteralPath $handlerListPath
    if ($restoredHandlerList.GetValueKind($clsid) -ne [Microsoft.Win32.RegistryValueKind]::DWord -or [int]$restoredHandlerList.GetValue($clsid) -ne 7) { throw 'The original PreviewHandlers value type was not restored.' }
    $removed = Get-Item -LiteralPath $unmodifiedPath
    if ($removed.GetValueNames().Contains('')) { throw 'An originally absent association value was not removed.' }
    if (Test-Path -LiteralPath $statePath) { throw 'Registration backup remained after restore.' }

    & $register -InstallDirectory $installDirectory -RegistryRoot $registryRoot -StatePath $statePath -SkipRuntimeCheck
    & $unregister -InstallDirectory $installDirectory -RegistryRoot $registryRoot -StatePath $statePath
    $restored = (Get-Item -LiteralPath $existingPath).GetValue('')
    if ($restored -ne '{22222222-2222-2222-2222-222222222222}') { throw 'Opt-out failed to preserve the value that existed before the latest registration.' }

    $runtimeInstalledPath = "$registryRoot\RuntimeInstalled"
    New-Item -Path $runtimeInstalledPath -Force | Out-Null
    New-ItemProperty -LiteralPath $runtimeInstalledPath -Name '10.0.14' -PropertyType String -Value '1' -Force | Out-Null
    & $register -InstallDirectory $installDirectory -RegistryRoot $registryRoot -StatePath $statePath -RuntimeKeyPath $runtimeInstalledPath -RuntimeFallbackKeyPath "$registryRoot\RuntimeFallbackMissing"
    if (-not (Test-Path -LiteralPath $statePath)) { throw 'A registered .NET 10 Desktop Runtime was not detected.' }
    & $unregister -InstallDirectory $installDirectory -RegistryRoot $registryRoot -StatePath $statePath

    $missingRuntimePath = "$registryRoot\RuntimeNotInstalled"
    $runtimeProbe = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile','-File',"`"$register`"",'-InstallDirectory',"`"$installDirectory`"",'-RegistryRoot',"`"$registryRoot`"",'-StatePath',"`"$statePath`"",'-RuntimeKeyPath',"`"$missingRuntimePath`"",'-RuntimeFallbackKeyPath',"`"$missingRuntimePath`"") -Wait -PassThru -NoNewWindow
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
