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
Write-Output "Patched HelixToolkit package: $package"
