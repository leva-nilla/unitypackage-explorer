[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0',
    [string]$OutputDirectory,
    [string]$DotNetPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.artifacts\release'))
$releaseRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $artifactRoot "v$Version"
} else {
    [IO.Path]::GetFullPath($OutputDirectory)
}
$releaseRoot = [IO.Path]::GetFullPath($releaseRoot)
if (-not $releaseRoot.StartsWith($artifactRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release outputs must stay inside the generated artifacts folder: $artifactRoot"
}

$dotnetCommand = if ([string]::IsNullOrWhiteSpace($DotNetPath)) { Get-Command dotnet -ErrorAction Stop } else { Get-Item -LiteralPath $DotNetPath -ErrorAction Stop }
$dotnetPathResolved = if ($dotnetCommand -is [IO.FileInfo]) {
    $dotnetCommand.FullName
} elseif ($dotnetCommand.Path) {
    $dotnetCommand.Path
} else {
    $dotnetCommand.Source
}
$publishDirectory = Join-Path $releaseRoot 'publish'
$buildOutputDirectory = [IO.Path]::GetFullPath((Join-Path $releaseRoot 'build')).TrimEnd('\') + '\'
$setupScript = Join-Path $PSScriptRoot 'UnityPackageExplorer.iss'
$applicationProject = Join-Path $repoRoot 'src\UnityPackageExplorer.App\UnityPackageExplorer.App.csproj'
$assetsFile = Join-Path $repoRoot 'src\UnityPackageExplorer.App\obj\project.assets.json'
$packageCache = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) { Join-Path $env:USERPROFILE '.nuget\packages' } else { $env:NUGET_PACKAGES }

if (Test-Path -LiteralPath $releaseRoot) {
    $resolvedArtifacts = $artifactRoot.TrimEnd('\') + '\'
    if (-not ([IO.Path]::GetFullPath($releaseRoot).StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase))) {
        throw "Refusing to replace a directory outside the generated artifacts folder: $releaseRoot"
    }
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

$publishArguments = @(
    'publish', $applicationProject,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-o', $publishDirectory,
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    "-p:BaseOutputPath=$buildOutputDirectory"
)
if (-not [string]::IsNullOrWhiteSpace($env:UPX_RESTORE_CONFIG_FILE)) {
    $publishArguments += "-p:RestoreConfigFile=$env:UPX_RESTORE_CONFIG_FILE"
}
if (-not [string]::IsNullOrWhiteSpace($env:UPX_TARGET_PLATFORM_SDK_ROOT_OVERRIDE)) {
    $publishArguments += "-p:TargetPlatformSdkRootOverride=$env:UPX_TARGET_PLATFORM_SDK_ROOT_OVERRIDE"
    $publishArguments += '-p:TargetPlatformDisplayName=Windows'
}
& $dotnetPathResolved @publishArguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$requiredCrtFiles = @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
$vswhereCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'),
    (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe')
)
$vswhere = $vswhereCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $vswhere) { throw 'vswhere.exe was not found; install Visual Studio C++ redistributable files to build the setup.' }
$crtCandidates = @()
$vsInstallations = @(& $vswhere -all -products '*' -property installationPath)
foreach ($vsInstallation in $vsInstallations) {
    $redistRoot = Join-Path $vsInstallation 'VC\Redist\MSVC'
    if (-not (Test-Path -LiteralPath $redistRoot -PathType Container)) { continue }
    foreach ($toolset in Get-ChildItem -LiteralPath $redistRoot -Directory) {
        $x64 = Join-Path $toolset.FullName 'x64'
        if (-not (Test-Path -LiteralPath $x64 -PathType Container)) { continue }
        foreach ($crt in Get-ChildItem -LiteralPath $x64 -Directory -Filter 'Microsoft.VC*.CRT') {
            $crtCandidates += [pscustomobject]@{ Path = $crt.FullName; Version = [version]$toolset.Name }
        }
    }
}
$crtCandidate = $crtCandidates |
    Sort-Object Version -Descending |
    Where-Object {
        $crtDirectory = $_.Path
        ($requiredCrtFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $crtDirectory $_) -PathType Leaf) }).Count -eq 0
    } |
    Select-Object -First 1
if (-not $crtCandidate) { throw 'The x64 Visual C++ runtime DLLs required by Assimp were not found.' }
foreach ($file in $requiredCrtFiles) {
    Copy-Item -LiteralPath (Join-Path $crtCandidate.Path $file) -Destination $publishDirectory
}

$licenseDirectory = Join-Path $publishDirectory 'Licenses'
New-Item -ItemType Directory -Force -Path $licenseDirectory | Out-Null
Copy-Item -Path (Join-Path $repoRoot 'ThirdPartyLicenses\*') -Destination $licenseDirectory -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $licenseDirectory 'THIRD-PARTY-NOTICES.md') -Force
if (-not (Test-Path -LiteralPath $assetsFile -PathType Leaf)) { throw "NuGet assets file was not produced: $assetsFile" }
$assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json
$runtimeTarget = $assets.targets.PSObject.Properties | Where-Object { $_.Name -like '*/win-x64' } | Select-Object -First 1
if (-not $runtimeTarget) { throw 'The win-x64 NuGet target is missing; run restore for win-x64 before packaging.' }
foreach ($asset in $runtimeTarget.Value.PSObject.Properties) {
    $library = $assets.libraries.PSObject.Properties[$asset.Name].Value
    if (-not $library -or $library.type -ne 'package') { continue }
    if (-not ($asset.Value.runtime -or $asset.Value.native -or $asset.Value.resource -or $asset.Value.runtimeTargets)) { continue }
    $packagePath = Join-Path $packageCache $library.path
    if (-not (Test-Path -LiteralPath $packagePath -PathType Container)) { throw "Package license source is missing: $packagePath" }
    $packageId, $packageVersion = $asset.Name.Split('/')
    $licenseDocs = Get-ChildItem -LiteralPath $packagePath -File -Recurse | Where-Object {
        $_.Name -match '^(?i)(license|copying|third.?party.?notices|notice)(\.|$)'
    }
    foreach ($licenseDoc in $licenseDocs) {
        $targetName = "{0}-{1}-{2}" -f $packageId, $packageVersion, $licenseDoc.Name
        Copy-Item -LiteralPath $licenseDoc.FullName -Destination (Join-Path $licenseDirectory $targetName) -Force
    }
}

$dotnetRoot = Split-Path $dotnetPathResolved -Parent
foreach ($noticeName in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    $sdkNotice = Join-Path $dotnetRoot $noticeName
    if (Test-Path -LiteralPath $sdkNotice -PathType Leaf) {
        $targetName = if ($noticeName -eq 'LICENSE.txt') { 'Microsoft-DotNet-Library-License.txt' } else { 'Microsoft-DotNet-ThirdPartyNotices.txt' }
        Copy-Item -LiteralPath $sdkNotice -Destination (Join-Path $licenseDirectory $targetName) -Force
    }
}
$crtNotice = @"
Microsoft Visual C++ x64 runtime DLLs included with this build
Toolset version: $($crtCandidate.Version)
Files: $($requiredCrtFiles -join ', ')
Source: Visual Studio's x64 Microsoft.VC*.CRT redistributable folder (application-local deployment)
Redistribution is subject to the applicable Microsoft Visual Studio Software License Terms.
https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files
"@
Set-Content -LiteralPath (Join-Path $licenseDirectory 'Microsoft-Visual-Cpp-Redist.txt') -Value $crtNotice -Encoding UTF8

$toolsDirectory = Join-Path $publishDirectory 'Tools'
New-Item -ItemType Directory -Force -Path $toolsDirectory | Out-Null
foreach ($toolScript in @('Cleanup-LegacyExplorerRegistration.ps1', 'Migrate-LegacyInstallFiles.ps1', 'LegacyInstallSafety.psm1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $toolScript) -Destination (Join-Path $toolsDirectory $toolScript) -Force
}
$currentFilesManifest = Join-Path $toolsDirectory 'CurrentInstallFiles.txt'
$currentInstallFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse -Force |
    Where-Object { -not $_.FullName.Equals($currentFilesManifest, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { $_.FullName.Substring($publishDirectory.TrimEnd('\').Length + 1).Replace('/', '\') })
$currentInstallFiles += 'Tools\CurrentInstallFiles.txt'
Set-Content -LiteralPath $currentFilesManifest -Value $currentInstallFiles -Encoding UTF8

$installer = if ($env:INNO_SETUP_COMPILER) { $env:INNO_SETUP_COMPILER } else { Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Inno Setup 6 compiler not found: $installer" }
& $installer '/Qp' "/DAppVersion=$Version" "/DSourceRoot=$repoRoot" "/DPublishRoot=$publishDirectory" "/O$releaseRoot" $setupScript
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

$setupPath = Join-Path $releaseRoot "UnityPackageExplorer-Setup-$Version-x64.exe"
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw "Installer was not produced: $setupPath" }
$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$setupPath.sha256" -Encoding ASCII -Value "$hash  $(Split-Path $setupPath -Leaf)"
Write-Output "Setup: $setupPath"
Write-Output "SHA-256: $hash"
