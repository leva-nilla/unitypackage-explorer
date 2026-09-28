[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$DevelopmentCertificateThumbprint,
    [switch]$SkipPackageConfirmation
)

$ErrorActionPreference = 'Stop'
$packageName = 'UnityPackageExplorer.SparseContextMenu'
$state = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\UnityPackageExplorer\Install')
$storedName = if ($state) { [string]$state.GetValue('SparsePackageName') } else { '' }
$storedPublisher = if ($state) { [string]$state.GetValue('SparsePackagePublisher') } else { '' }
$storedFullName = if ($state) { [string]$state.GetValue('SparsePackageFullName') } else { '' }
if ($state) { $state.Dispose() }

if ($storedName -eq $packageName -and $storedPublisher -and $storedFullName) {
    $ownedPackage = Get-AppxPackage -Name $storedName -ErrorAction SilentlyContinue |
        Where-Object { $_.Publisher -eq $storedPublisher -and $_.PackageFullName -eq $storedFullName } |
        Select-Object -First 1
    $removePackage = $ownedPackage -and (
        $SkipPackageConfirmation -or
        $PSCmdlet.ShouldProcess("CurrentUser AppX package $($ownedPackage.PackageFullName) from $storedPublisher", 'Remove the exact UnityPackage Explorer package recorded by this installer')
    )
    if ($removePackage) {
        Remove-AppxPackage -Package $ownedPackage.PackageFullName
        $state = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\UnityPackageExplorer\Install', $true)
        if ($state) {
            $state.DeleteValue('SparsePackageName', $false)
            $state.DeleteValue('SparsePackagePublisher', $false)
            $state.DeleteValue('SparsePackageFullName', $false)
            $state.Dispose()
        }
    }
} else {
    Write-Host 'No exact CurrentUser package identity recorded by this installer; no package was removed.'
}

if ($DevelopmentCertificateThumbprint) {
    $thumbprint = $DevelopmentCertificateThumbprint.Replace(' ', '').ToUpperInvariant()
    $expectedSubject = 'CN=UnityPackage Explorer Development'
    foreach ($store in @('Cert:\CurrentUser\TrustedPeople', 'Cert:\CurrentUser\My')) {
        $matchingCertificates = @(Get-ChildItem -LiteralPath $store -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $thumbprint -and $_.Subject -eq $expectedSubject })
        foreach ($certificate in $matchingCertificates) {
            if ($PSCmdlet.ShouldProcess("$store\$thumbprint ($expectedSubject)", 'Remove only this UnityPackage Explorer development certificate')) {
                Remove-Item -LiteralPath (Join-Path $store $thumbprint) -Force
            }
        }
    }
} else {
    Write-Host 'Signing certificate and TrustedPeople entry were retained. Supply -DevelopmentCertificateThumbprint to review removal of this exact development certificate.'
}
