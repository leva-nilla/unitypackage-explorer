[CmdletBinding()]
param([switch]$Test)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$viewerRoot = Join-Path $repoRoot 'src\UnityPackageExplorer.App\WebViewer'
$npm = Get-Command npm.cmd -ErrorAction Stop

Push-Location $viewerRoot
try {
    & $npm.Source ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "Web viewer dependency restore failed ($LASTEXITCODE)." }
    & $npm.Source run build
    if ($LASTEXITCODE -ne 0) { throw "Web viewer build failed ($LASTEXITCODE)." }
    if ($Test) {
        & $npm.Source test
        if ($LASTEXITCODE -ne 0) { throw "Web viewer tests failed ($LASTEXITCODE)." }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $viewerRoot 'dist\index.html') -PathType Leaf)) {
        throw 'The built web viewer entry point is missing.'
    }
}
finally { Pop-Location }
