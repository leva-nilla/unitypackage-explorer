[CmdletBinding()]
$extensionsPath = 'HKCU:\Software\Classes\SystemFileAssociations\.unitypackage\shell'

$verbs = @('UnityPackageInspect', 'UnityPackageExtract', 'UnityPackageOpen')

foreach ($verb in $verbs) {
    $menuPath = Join-Path $extensionsPath $verb
    if (Test-Path -Path $menuPath) {
        Remove-Item -Path $menuPath -Recurse -Force
        Write-Host "[OK] removed: $verb"
    }
    else {
        Write-Host "[skip] not found: $verb"
    }
}

if ((Test-Path -Path $extensionsPath) -and -not (Get-ChildItem -Path $extensionsPath -ErrorAction SilentlyContinue)) {
    Remove-Item -Path $extensionsPath -Recurse -Force
}

Write-Host 'UnityPackage context menu uninstall complete.'
