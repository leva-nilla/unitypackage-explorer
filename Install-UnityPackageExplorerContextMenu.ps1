[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $MyInvocation.MyCommand.Path)
)

$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $Root 'UnityPackageTools.ps1'
if (-not (Test-Path -LiteralPath $scriptPath)) {
    throw "UnityPackageTools.ps1 が見つかりません: $scriptPath"
}

$extensionsPath = 'HKCU:\Software\Classes\SystemFileAssociations\.unitypackage\shell'

$actions = [ordered]@{
    Inspect = '内容を表示（UnityPackage）'
    Extract = '展開（UnityPackage）'
    Open = '開く（UnityPackage）'
}

foreach ($action in $actions.Keys) {
    $itemText = $actions[$action]
    $verb = "UnityPackage${action}"
    $menuPath = Join-Path $extensionsPath $verb

    $command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$scriptPath`" -Action $($action.ToLower()) -File `"%1`""

    $key = New-Item -Path $menuPath -Force
    Set-ItemProperty -Path $menuPath -Name '(default)' -Value $itemText -ErrorAction SilentlyContinue

    $commandKey = New-Item -Path (Join-Path $menuPath 'command') -Force
    Set-ItemProperty -Path (Join-Path $menuPath 'command') -Name '(default)' -Value $command -ErrorAction SilentlyContinue

    Write-Host "[OK] installed: $itemText"
}

Write-Host "UnityPackage context menu installed (HKCU)."
