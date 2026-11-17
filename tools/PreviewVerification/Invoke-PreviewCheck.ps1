[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$File,
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\UnityPackageExplorer'),
    [switch]$Register,
    [switch]$KeepRegistration,
    [string]$OutputDirectory,
    [double]$MinimumNonUniformRatio = 0.005,
    [double]$MinimumChromaticRatio = 0.01,
    [double]$MinimumCenterNonUniformRatio = 0.01,
    [int]$SettleMilliseconds = 60000,
    [switch]$SkipStream,
    [switch]$IncludeApp,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '..\..\.artifacts\preview-verification' }

$clsid = '{70C67CC6-949B-4B5D-A8F2-7844D5C29E76}'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$exe = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\PreviewVerification.exe'
$project = Join-Path $PSScriptRoot 'PreviewVerification.csproj'
$install = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$comhost = Join-Path $install 'PreviewHandler\UnityPackageExplorer.PreviewHandler.comhost.dll'
$app = Join-Path $install 'UnityPackageExplorer.App.exe'

if (-not (Test-Path -LiteralPath $comhost -PathType Leaf)) {
    throw "Preview handler publish output was not found: $comhost. Point -InstallDirectory at an installed or published build."
}

function Get-RegisteredComhost {
    # Per-user classes override machine classes, so check HKCU first like COM does.
    foreach ($hive in @('HKEY_CURRENT_USER', 'HKEY_LOCAL_MACHINE')) {
        $path = "Registry::${hive}\Software\Classes\CLSID\$clsid\InprocServer32"
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $value = (Get-Item -LiteralPath $path).GetValue('')
        if ($value) { return [IO.Path]::GetFullPath([string]$value) }
    }
    return $null
}

$registered = Get-RegisteredComhost
$registrationMatches = $registered -and $registered.Equals([IO.Path]::GetFullPath($comhost), [StringComparison]::OrdinalIgnoreCase)
if (-not $registrationMatches) {
    if (-not $Register) {
        throw "The preview handler is not registered for $comhost. Install the setup, or re-run with -Register (this writes the machine-wide COM registration and may ask for elevation)."
    }
    Write-Host 'Registering the preview handler...'
    & (Join-Path $repoRoot 'tools\release\Register-ExplorerPreview.ps1') -InstallDirectory $install
    if ($LASTEXITCODE -ne 0) { throw "Preview registration failed with exit code $LASTEXITCODE." }
    $registered = Get-RegisteredComhost
    if (-not $registered -or -not $registered.Equals([IO.Path]::GetFullPath($comhost), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Preview registration did not point the CLSID at this install directory.'
    }
}

if (-not $NoBuild) {
    Write-Host 'Building the preview verification harness...'
    & dotnet build $project -c Release --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'The preview verification harness did not build.' }
}
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "The preview verification harness is missing: $exe" }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

$requestRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA '..\LocalLow\UnityPackageExplorer\PreviewRequests'))
$appTempRoot = Join-Path $env:TEMP 'UnityPackageExplorer'

function Get-ChildNames([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    return @(Get-ChildItem -LiteralPath $Path -Directory -ErrorAction SilentlyContinue | ForEach-Object Name)
}

function Wait-ForRemoval([string[]]$Known, [string]$Path, [int]$TimeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $remaining = @(Get-ChildNames $Path | Where-Object { $Known -notcontains $_ })
        if ($remaining.Count -eq 0) { return @() }
        Start-Sleep -Milliseconds 500
    }
    return @(Get-ChildNames $Path | Where-Object { $Known -notcontains $_ })
}

function Get-PrevhostIds {
    return @(Get-Process -Name 'prevhost' -ErrorAction SilentlyContinue | ForEach-Object Id)
}

$results = [Collections.Generic.List[object]]::new()
$requestDirectoriesBefore = @(Get-ChildNames $requestRoot)
$appTempDirectoriesBefore = @(Get-ChildNames $appTempRoot)
$prevhostBefore = Get-PrevhostIds
$registrationChanged = $false
if (-not $registrationMatches) { $registrationChanged = $true }

try {
    foreach ($path in $File) {
        $fullPath = [IO.Path]::GetFullPath($path)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "Preview source was not found: $fullPath" }
        $baseName = [IO.Path]::GetFileNameWithoutExtension($fullPath)
        $modes = @('file')
        if (-not $SkipStream) { $modes += 'stream' }

        foreach ($mode in $modes) {
            $suffix = $baseName + '-' + $mode
            $capture = Join-Path $outputDirectory ($suffix + '.png')
            $json = Join-Path $outputDirectory ($suffix + '.json')
            Remove-Item -LiteralPath $capture -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $json -ErrorAction SilentlyContinue

            $arguments = @(
                '--file', $fullPath,
                '--capture', $capture,
                '--json', $json,
                '--settle-ms', $SettleMilliseconds,
                '--min-nonuniform', $MinimumNonUniformRatio,
                '--min-chromatic', $MinimumChromaticRatio,
                '--min-center-nonuniform', $MinimumCenterNonUniformRatio
            )
            if ($mode -eq 'stream') { $arguments += '--stream' }

            Write-Host ("Checking {0} ({1} init)..." -f ([IO.Path]::GetFileName($fullPath)), $mode)
            & $exe @arguments
            $exitCode = $LASTEXITCODE
            $payload = $null
            if (Test-Path -LiteralPath $json -PathType Leaf) {
                try { $payload = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json } catch { $payload = $null }
            }

            $leftover = @(Wait-ForRemoval $requestDirectoriesBefore $requestRoot 15)
            if ($leftover.Count -eq 0) {
                $requestDirectoriesBefore = @(Get-ChildNames $requestRoot)
            }

            $results.Add([pscustomobject]@{
                file            = $fullPath
                mode            = $mode
                exitCode        = $exitCode
                passed          = ($exitCode -eq 0 -and $leftover.Count -eq 0)
                nonUniform      = if ($payload) { $payload.NonUniformRatio } else { $null }
                center          = if ($payload) { $payload.CenterNonUniformRatio } else { $null }
                chromatic       = if ($payload) { $payload.ChromaticRatio } else { $null }
                unloadMs        = if ($payload) { $payload.UnloadMilliseconds } else { $null }
                capture         = $capture
                leftoverTemp    = ($leftover -join ', ')
                error           = if ($payload -and $payload.Error) { $payload.Error } else { $null }
            })
        }
    }

    if ($IncludeApp) {
        if (-not (Test-Path -LiteralPath $app -PathType Leaf)) { throw "The application was not found: $app" }
        foreach ($path in $File) {
            $fullPath = [IO.Path]::GetFullPath($path)
            Write-Host ("Checking application temporary folders with {0}..." -f ([IO.Path]::GetFileName($fullPath)))
            $process = Start-Process -FilePath $app -ArgumentList @("`"$fullPath`"") -PassThru
            $passed = $false
            $appError = $null
            try {
                $deadline = (Get-Date).AddSeconds(60)
                while ((Get-Date) -lt $deadline -and $process.MainWindowHandle -eq [IntPtr]::Zero) {
                    Start-Sleep -Milliseconds 500
                    $process.Refresh()
                }
                if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
                    $appError = 'The application did not create a window.'
                } else {
                    Start-Sleep -Seconds 10
                    [void]$process.CloseMainWindow()
                    if (-not $process.WaitForExit(15000)) {
                        $appError = 'The application did not exit after closing its window.'
                        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    }
                }
            } finally {
                if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
            }

            $leftover = @(Wait-ForRemoval $appTempDirectoriesBefore $appTempRoot 20)
            if ($leftover.Count -eq 0) {
                $appTempDirectoriesBefore = @(Get-ChildNames $appTempRoot)
                $passed = (-not $appError)
            } else {
                $appError = "Temporary folders remained after exit: $($leftover -join ', ')"
            }

            $results.Add([pscustomobject]@{
                file            = $fullPath
                mode            = 'app-temp'
                exitCode        = if ($passed) { 0 } else { 1 }
                passed          = $passed
                nonUniform      = $null
                center          = $null
                chromatic       = $null
                unloadMs        = $null
                capture         = $null
                leftoverTemp    = ($leftover -join ', ')
                error           = $appError
            })
        }
    }
} finally {
    if ($registrationChanged -and -not $KeepRegistration) {
        Write-Host 'Restoring the previous preview registration...'
        try {
            & (Join-Path $repoRoot 'tools\release\Unregister-ExplorerPreview.ps1') -InstallDirectory $install
        } catch {
            Write-Warning ("Could not restore the previous preview registration: {0}" -f $_.Exception.Message)
        }
    }
}

$prevhostAfter = Get-PrevhostIds
$newPrevhost = @($prevhostAfter | Where-Object { $prevhostBefore -notcontains $_ })

$summaryPath = Join-Path $outputDirectory 'summary.json'
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host ''
$results | Format-Table -AutoSize file, mode, passed, nonUniform, center, chromatic, unloadMs, leftoverTemp | Out-String -Width 4096 | Write-Host
if ($newPrevhost.Count -gt 0) {
    Write-Host ("Note: new prevhost.exe process ids are still running: {0} (Windows may reuse the surrogate; only leftover request folders count as a failure)." -f ($newPrevhost -join ', '))
}
Write-Host ("Artifacts: {0}" -f $outputDirectory)

$failed = @($results | Where-Object { -not $_.passed })
if ($failed.Count -gt 0) {
    Write-Host ''
    $failed | ForEach-Object { Write-Host ("FAIL {0} ({1}): {2}" -f ([IO.Path]::GetFileName($_.file)), $_.mode, $_.error) }
    exit 1
}

Write-Host 'All preview verification checks passed.'
exit 0
