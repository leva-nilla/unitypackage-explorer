[CmdletBinding()]
param([string]$DotNetPath = 'dotnet')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sourceRoot = Join-Path $repoRoot 'third_party\HelixToolkit.SharpDX'
$feed = Join-Path $repoRoot '.artifacts\packages'
New-Item -ItemType Directory -Force -Path $feed | Out-Null

foreach ($line in Get-Content -LiteralPath (Join-Path $sourceRoot 'Resources\SHA256SUMS')) {
    if ($line -notmatch '^([a-f0-9]{64})  ([A-Za-z0-9_.-]+\.cso)$') { throw "Invalid shader manifest entry: $line" }
    $expected = $Matches[1]
    $resource = Join-Path $sourceRoot "Resources\$($Matches[2])"
    if ((Get-FileHash -LiteralPath $resource -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
        throw "Vendored shader checksum mismatch: $resource"
    }
}

& $DotNetPath pack (Join-Path $sourceRoot 'HelixToolkit.SharpDX.csproj') -c Release -o $feed
if ($LASTEXITCODE -ne 0) { throw "Patched HelixToolkit pack failed with exit code $LASTEXITCODE." }
$package = Join-Path $feed 'HelixToolkit.SharpDX.3.1.2.1-upx.1.nupkg'
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "Patched HelixToolkit package is missing: $package" }

# NuGet otherwise reuses a previous local build with this exact version. Only
# invalidate our generated package, never the original upstream package/cache.
$cacheRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path $env:USERPROFILE '.nuget\packages'
} else {
    $env:NUGET_PACKAGES
}
$cachedPackage = Join-Path $cacheRoot 'helixtoolkit.sharpdx\3.1.2.1-upx.1'
$cachedDll = Join-Path $cachedPackage 'lib\net8.0\HelixToolkit.SharpDX.dll'
$builtDll = Join-Path $sourceRoot 'bin\Release\net8.0\HelixToolkit.SharpDX.dll'
if ((Test-Path -LiteralPath $cachedDll) -and
    (Get-FileHash -LiteralPath $cachedDll -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $builtDll -Algorithm SHA256).Hash) {
    Remove-Item -LiteralPath $cachedPackage -Recurse -Force
    Write-Output 'Invalidated the stale generated HelixToolkit 3.1.2.1-upx.1 cache.'
}
Write-Output "Patched HelixToolkit package: $package"
