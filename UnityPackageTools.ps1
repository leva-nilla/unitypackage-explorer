[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('inspect', 'extract', 'open')]
    [string]$Action,
    [Parameter(Mandatory = $true)]
    [string]$File,
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

function Test-UnityAssetPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][ValidateSet('inspect', 'extract', 'open')][string]$Action
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "指定されたファイルが見つかりません: $Path"
    }
    $extension = [IO.Path]::GetExtension($Path)
    if ($Action -eq 'inspect' -and $extension -in @('.unitypackage', '.zip', '.rar', '.prefab')) { return }
    if ($extension -ine '.unitypackage') {
        throw "inspect は .unitypackage / .zip / .rar / .prefab、extract と open は .unitypackage に対応します。: $Path"
    }
}

function Invoke-UnityPackageInspect {
    param([Parameter(Mandatory = $true)][string]$PackagePath)

    $app = Join-Path $PSScriptRoot 'src\UnityPackageExplorer.App\bin\Release\net10.0-windows\UnityPackageExplorer.App.exe'
    if (-not (Test-Path -LiteralPath $app -PathType Leaf)) {
        throw 'Viewer app is not built. Run Install-UnityPackageExplorerContextMenu.ps1 first.'
    }
    Start-Process -FilePath $app -ArgumentList ('"' + $PackagePath + '"')
    return $app
}

function Invoke-UnityPackageExtract {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [string]$Destination
    )

    $cli = Join-Path $PSScriptRoot 'src\UnityPackageExplorer.Cli\bin\Release\net10.0\UnityPackageExplorer.Cli.exe'
    if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) {
        throw 'Safe extractor is not built. Run Install-UnityPackageExplorerContextMenu.ps1 first.'
    }

    $destinationBase = if ([string]::IsNullOrWhiteSpace($Destination)) {
        Join-Path (Split-Path -Parent $PackagePath) (([IO.Path]::GetFileNameWithoutExtension($PackagePath)) + '_extracted')
    } else {
        $Destination
    }

    $output = & $cli 'extract' $PackagePath $destinationBase 2>&1
    if ($LASTEXITCODE -ne 0) {
        $message = if ($output -is [object[]]) { $output -join [Environment]::NewLine } else { [string]$output }
        throw "パッケージの安全な展開に失敗しました: $message"
    }

    $extractedPath = [string]($output | Select-Object -Last 1)
    if ([string]::IsNullOrWhiteSpace($extractedPath) -or -not (Test-Path -LiteralPath $extractedPath -PathType Container)) {
        throw '展開処理は成功しましたが、出力先を確認できませんでした。'
    }
    return $extractedPath.Trim()
}

function Invoke-UnityPackageOpen {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [string]$Destination
    )

    $output = Invoke-UnityPackageExtract -PackagePath $PackagePath -Destination $Destination
    Start-Process -FilePath 'explorer.exe' -ArgumentList ('"' + $output + '"')
    return $output
}

try {
    Test-UnityAssetPath -Path $File -Action $Action
    $result = switch ($Action) {
        'inspect' { Invoke-UnityPackageInspect -PackagePath $File }
        'extract' { Invoke-UnityPackageExtract -PackagePath $File -Destination $Destination }
        'open' { Invoke-UnityPackageOpen -PackagePath $File -Destination $Destination }
    }

    if ($Action -eq 'inspect') {
        Write-Host "[OK] viewer: $result"
    } else {
        Write-Host "[OK] extracted: $result"
    }
}
catch {
    Write-Error "[$Action] $_"
    exit 1
}
