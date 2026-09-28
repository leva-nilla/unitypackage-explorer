[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA 'UnityPackageExplorer\Signing')
)

$ErrorActionPreference = 'Stop'
$subject = 'CN=UnityPackage Explorer Development'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$certificatePath = Join-Path $outputPath 'UnityPackageExplorer.Development.cer'

if ($PSCmdlet.ShouldProcess("Cert:\CurrentUser\My and $certificatePath", 'Create a local-only, non-exportable Code Signing certificate and export its public certificate')) {
    New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $subject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy NonExportable `
        -KeyUsage DigitalSignature `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
        -NotAfter (Get-Date).AddYears(3)
    $ekuOids = @($certificate.EnhancedKeyUsageList | ForEach-Object { [string]$_.ObjectId })
    $constraintsExtension = $certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.19' } |
        Select-Object -First 1
    $basicConstraints = if ($constraintsExtension) {
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($constraintsExtension, $constraintsExtension.Critical)
    }
    $isCertificateAuthority = $false
    if ($basicConstraints) { $isCertificateAuthority = $basicConstraints.CertificateAuthority }
    if ('1.3.6.1.5.5.7.3.3' -notin $ekuOids -or -not $constraintsExtension -or $isCertificateAuthority) {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -Force -ErrorAction SilentlyContinue
        throw 'The generated certificate failed Code Signing EKU or explicit CA:false Basic Constraints validation; it was removed.'
    }
    Export-Certificate -Cert $certificate -FilePath $certificatePath -Force | Out-Null
    Write-Host "Thumbprint: $($certificate.Thumbprint)"
    Write-Host "Public certificate: $certificatePath"
    Write-Host 'The private key remains in Cert:\CurrentUser\My and is not exported.'
    Write-Host 'This does not trust the certificate. LocalMachine\TrustedPeople trust is required on this device; the installer will not add it or elevate privileges.'
}
