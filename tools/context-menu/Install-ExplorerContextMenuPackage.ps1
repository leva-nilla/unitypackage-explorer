[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [Parameter(Mandatory)]
    [string]$CertificateThumbprint,
    [string]$ExternalLocation,
    [string]$TrustCertificatePath = (Join-Path $env:LOCALAPPDATA 'UnityPackageExplorer\Signing\UnityPackageExplorer.Development.cer')
)

$ErrorActionPreference = 'Stop'
$rootPath = [IO.Path]::GetFullPath($Root)
$appOutput = if ($ExternalLocation) { [IO.Path]::GetFullPath($ExternalLocation) } else { Join-Path $rootPath 'src\UnityPackageExplorer.App\bin\Release\net9.0-windows' }
$packagePath = Join-Path $PSScriptRoot 'obj\UnityPackageExplorer.SparseContextMenu.msix'
$packageName = 'UnityPackageExplorer.SparseContextMenu'
$thumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction Stop
if (-not $certificate.HasPrivateKey) { throw 'The selected current-user signing certificate has no private key.' }
$enhancedKeyUsageOids = @($certificate.EnhancedKeyUsageList | ForEach-Object { [string]$_.ObjectId })
if ('1.3.6.1.5.5.7.3.3' -notin $enhancedKeyUsageOids) { throw 'The selected certificate does not have the Code Signing EKU.' }
$constraintsExtension = $certificate.Extensions |
    Where-Object { $_.Oid.Value -eq '2.5.29.19' } |
    Select-Object -First 1
$basicConstraints = if ($constraintsExtension) {
    [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($constraintsExtension, $constraintsExtension.Critical)
}
if (-not $basicConstraints -or $basicConstraints.CertificateAuthority) {
    throw 'The selected certificate must contain an explicit Basic Constraints extension with CA:false.'
}

& (Join-Path $PSScriptRoot 'Build-ExplorerContextMenuPackage.ps1') -Root $rootPath -CertificateThumbprint $thumbprint -ExternalLocation $appOutput
if (-not (Test-Path -LiteralPath $packagePath)) { throw "Signed package was not created: $packagePath" }

$existingPackages = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)
$otherPublisher = $existingPackages | Where-Object { $_.Publisher -ne $certificate.Subject }
if ($otherPublisher) { throw "A package with this name is registered under a different publisher; it was not replaced: $($otherPublisher.PackageFullName -join ', ')" }

$trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $thumbprint } | Select-Object -First 1
if (-not $trusted) {
    if (-not $TrustCertificatePath) {
        throw 'The signing certificate is not in Cert:\LocalMachine\TrustedPeople. Supply its public .cer with -TrustCertificatePath.'
    }

    $certificateFile = [IO.Path]::GetFullPath($TrustCertificatePath)
    $quotedCertificateFile = "'" + $certificateFile.Replace("'", "''") + "'"
    $importCommand = "Import-Certificate -FilePath $quotedCertificateFile -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    if (-not (Test-Path -LiteralPath $certificateFile -PathType Leaf)) {
        throw "The signing certificate is not in Cert:\LocalMachine\TrustedPeople and the public CER is missing at $certificateFile. Generate/export the public certificate first. After confirming the machine-wide trust change, run this command from an elevated PowerShell:`r`n$importCommand"
    }
    $publicCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificateFile)
    try {
        if ($publicCertificate.Thumbprint -ne $thumbprint) {
            throw 'The public certificate file does not match -CertificateThumbprint.'
        }
    } finally {
        $publicCertificate.Dispose()
    }

    throw "The signing certificate is not in Cert:\LocalMachine\TrustedPeople. This step requires an administrator; no elevation or import was attempted. Open PowerShell as Administrator and run:`r`n$importCommand`r`nThen rerun this installer."
}

if (-not $PSCmdlet.ShouldProcess("CurrentUser AppX package $packageName", "Install signed sparse package with $appOutput as ExternalLocation")) {
    Write-Host 'Package registration was not approved; no trust store was changed.'
    return
}

Add-AppxPackage -Path $packagePath -ExternalLocation $appOutput -ForceUpdateFromAnyVersion

$registeredPackage = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue |
    Where-Object { $_.Publisher -eq $certificate.Subject } | Select-Object -First 1
if (-not $registeredPackage) {
    throw 'Package registration completed without a matching current-user package.'
}

$installState = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\UnityPackageExplorer\Install')
try {
    $installState.SetValue('SparsePackageName', $packageName, [Microsoft.Win32.RegistryValueKind]::String)
    $installState.SetValue('SparsePackagePublisher', $registeredPackage.Publisher, [Microsoft.Win32.RegistryValueKind]::String)
    $installState.SetValue('SparsePackageFullName', $registeredPackage.PackageFullName, [Microsoft.Win32.RegistryValueKind]::String)
    $installState.SetValue('SparseExternalLocation', $appOutput, [Microsoft.Win32.RegistryValueKind]::String)
    $installState.SetValue('SparsePackageCertificateThumbprint', $thumbprint, [Microsoft.Win32.RegistryValueKind]::String)
} finally {
    $installState.Dispose()
}
Write-Host "Registered for current user: $($registeredPackage.PackageFullName)"
