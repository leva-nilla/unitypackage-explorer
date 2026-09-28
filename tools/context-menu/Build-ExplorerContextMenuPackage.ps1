[CmdletBinding()]
param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$CertificateThumbprint
)

$ErrorActionPreference = 'Stop'
$rootPath = [IO.Path]::GetFullPath($Root)
$appOutput = Join-Path $rootPath 'src\UnityPackageExplorer.App\bin\Release\net9.0-windows'
$appExe = Join-Path $appOutput 'UnityPackageExplorer.App.exe'
$shellDll = Join-Path $appOutput 'UnityPackageExplorer.ShellExtension.dll'
$manifestSource = Join-Path $PSScriptRoot 'AppxManifest.xml'
$assetsSource = Join-Path $PSScriptRoot 'Assets'
$packageInput = Join-Path $PSScriptRoot 'obj\SparsePackage'
$packagePath = Join-Path $PSScriptRoot 'obj\UnityPackageExplorer.SparseContextMenu.msix'
$unsignedPublisher = 'CN=UnityPackage Explorer, OID.2.25.311729368913984317654407730594956997722=1'
$publisher = $unsignedPublisher
$certificate = $null
if ($CertificateThumbprint) {
    $thumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction Stop
    if (-not $certificate.HasPrivateKey) { throw 'The selected current-user signing certificate has no private key.' }
    $enhancedKeyUsageOids = @($certificate.EnhancedKeyUsageList | ForEach-Object { [string]$_.ObjectId })
    if ('1.3.6.1.5.5.7.3.3' -notin $enhancedKeyUsageOids) {
        throw 'The selected certificate does not have the Code Signing EKU.'
    }
    $constraintsExtension = $certificate.Extensions |
        Where-Object { $_.Oid.Value -eq '2.5.29.19' } |
        Select-Object -First 1
    $basicConstraints = if ($constraintsExtension) {
        [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($constraintsExtension, $constraintsExtension.Critical)
    }
    if (-not $basicConstraints -or $basicConstraints.CertificateAuthority) {
        throw 'The selected certificate must contain an explicit Basic Constraints extension with CA:false.'
    }
    $publisher = $certificate.Subject
}

if (-not (Test-Path -LiteralPath $appExe)) { throw "Build the WPF app first: $appExe" }
if (-not (Test-Path -LiteralPath $shellDll)) { throw "Build the native shell extension first: $shellDll" }
if (-not (Test-Path -LiteralPath $manifestSource)) { throw "Package manifest not found: $manifestSource" }
foreach ($asset in @('Square150x150Logo.png', 'Square44x44Logo.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $assetsSource $asset))) { throw "Manifest asset is missing: $asset" }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer vswhere.exe was not found.' }
$vsPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw 'A Visual Studio C++ x64 toolchain is required.' }
$msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { throw "MSBuild was not found: $msbuild" }
$nativeProject = Join-Path $rootPath 'src\UnityPackageExplorer.ShellExtension\UnityPackageExplorer.ShellExtension.vcxproj'
& $msbuild $nativeProject /m /v:minimal /p:Configuration=Release /p:Platform=x64 "/p:ShellOutputDirectory=$appOutput"
if ($LASTEXITCODE -ne 0) { throw 'Native Explorer command build failed.' }

$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeAppx = Get-ChildItem -LiteralPath $sdkRoot -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $makeAppx) { throw 'Windows SDK MakeAppx.exe was not found.' }

New-Item -ItemType Directory -Force -Path $packageInput | Out-Null
$manifestContent = [IO.File]::ReadAllText($manifestSource).Replace('@@PUBLISHER@@', [Security.SecurityElement]::Escape($publisher))
$manifestOutput = Join-Path $packageInput 'AppxManifest.xml'
[IO.File]::WriteAllText($manifestOutput, $manifestContent, [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath $shellDll -Destination $packageInput -Force
$packageFiles = @(Get-ChildItem -LiteralPath $packageInput -File | Select-Object -ExpandProperty Name | Sort-Object)
if (($packageFiles -join ',') -ne 'AppxManifest.xml,UnityPackageExplorer.ShellExtension.dll') {
    throw 'The sparse package payload must contain only AppxManifest.xml and the native Explorer command DLL.'
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $packagePath) | Out-Null
& $makeAppx pack /o /nv /d $packageInput /p $packagePath
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed to create the sparse identity package.' }

if ($certificate) {
    $signTool = Join-Path (Split-Path -Parent $makeAppx) 'signtool.exe'
    if (-not (Test-Path -LiteralPath $signTool)) { throw 'Windows SDK SignTool.exe was not found.' }
    & $signTool sign /fd SHA256 /s My /sha1 $certificate.Thumbprint /d 'UnityPackage Explorer sparse context menu' $packagePath
    if ($LASTEXITCODE -ne 0) { throw 'SignTool failed to sign the sparse identity package.' }
}

# A sparse package resolves the app executable, shell DLL, and logos from this external folder.
$externalAssets = Join-Path $appOutput 'Assets'
New-Item -ItemType Directory -Force -Path $externalAssets | Out-Null
Copy-Item -LiteralPath (Join-Path $assetsSource 'Square150x150Logo.png') -Destination $externalAssets -Force
Copy-Item -LiteralPath (Join-Path $assetsSource 'Square44x44Logo.png') -Destination $externalAssets -Force

Write-Host "Package: $packagePath"
Write-Host 'Package payload: AppxManifest.xml and the native Explorer command DLL; the WPF app executable remains external.'
Write-Host "External location: $appOutput"
if ($certificate) {
    Write-Host "Package publisher: $publisher"
    Write-Host "Signed with current-user certificate: $($certificate.Thumbprint)"
} else {
    Write-Warning 'This unsigned package is for manifest validation only. It cannot activate the app on Windows 11.'
}
