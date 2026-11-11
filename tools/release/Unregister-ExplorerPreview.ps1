[CmdletBinding()]
param(
    [string]$InstallDirectory,
    [string]$RegistryRoot = 'HKCU:',
    [string]$StatePath = (Join-Path $env:LOCALAPPDATA 'UnityPackageExplorer\ExplorerPreviewRegistration.json')
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PreviewRegistration.Common.psm1') -Force
if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) { return }
$state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
foreach ($snapshot in $state.Values) {
    $owned = $false
    $expectedKind = 'String'
    $expectedValue = $null
    $path = [string]$snapshot.Path
    $name = [string]$snapshot.Name
    # Only restore a value while it still matches this product's registration.
    if ($path -match '\\CLSID\\\{70C67CC6-949B-4B5D-A8F2-7844D5C29E76\}$' -and $name -eq '') { $expectedValue = 'UnityPackage Explorer Preview Handler'; $owned = $true }
    if ($path -match '\\InprocServer32$' -and $name -eq '' -and $InstallDirectory) { $expectedValue = Join-Path ([IO.Path]::GetFullPath($InstallDirectory)) 'PreviewHandler\UnityPackageExplorer.PreviewHandler.comhost.dll'; $owned = $true }
    elseif ($path -match '\\InprocServer32$' -and $name -eq 'ThreadingModel') { $expectedValue = 'Apartment'; $owned = $true }
    elseif ($path -match '\\InprocServer32$' -and $name -eq 'ProgID') { $expectedValue = 'UnityPackageExplorer.PreviewHandler'; $owned = $true }
    elseif ($path -match '\\CLSID\\\{70C67CC6-949B-4B5D-A8F2-7844D5C29E76\}$' -and $name -eq 'AppID') { $expectedValue = '{C50AF32D-4778-4B45-A2D3-FB0944F70271}'; $owned = $true }
    elseif ($path -match '\\AppID\\\{C50AF32D-4778-4B45-A2D3-FB0944F70271\}$' -and $name -eq 'DllSurrogate') { $expectedValue = 'Prevhost.exe'; $owned = $true }
    elseif ($path -match '\\PreviewHandlers$' -and $name -eq '{70C67CC6-949B-4B5D-A8F2-7844D5C29E76}') { $expectedValue = 'UnityPackage Explorer Preview Handler'; $owned = $true }
    elseif ($path -match '\\shellex\\\{8895B1C6-B41F-4C1C-A562-0D564250836F\}$' -and $name -eq '') { $expectedValue = '{70C67CC6-949B-4B5D-A8F2-7844D5C29E76}'; $owned = $true }
    if ($owned -and (Test-PreviewValue $path $name $expectedValue $expectedKind)) { Restore-PreviewValue $snapshot }
}
Remove-Item -LiteralPath $StatePath -Force
Write-Output 'Explorer preview handler registration restored.'
