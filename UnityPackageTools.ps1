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

$script:UseTar = $null -ne (Get-Command tar -ErrorAction SilentlyContinue)
$script:HasTarApi = [bool][Type]::GetType('System.Formats.Tar.TarFile', $false)
$tarCommandName = 'tar'

function Get-ScriptName {
    switch ($Action) {
        'inspect' { '内容を表示（UnityPackage）' }
        'extract' { '展開（UnityPackage）' }
        default { 'UnityPackage' }
    }
}

function Test-UnityPackagePath {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "指定されたファイルが見つかりません: $Path"
    }

    if ([IO.Path]::GetExtension($Path).ToLowerInvariant() -ne '.unitypackage') {
        throw ".unitypackage ファイルを指定してください。: $Path"
    }
}

function Test-EnvironmentSupport {
    if (-not $script:UseTar -and -not $script:HasTarApi) {
        throw 'tar コマンドも System.Formats.Tar API も利用できないため、処理を実行できません。'
    }
}

function Invoke-Tar {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & $tarCommandName @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $message = if ($output -is [object[]]) { $output -join [Environment]::NewLine } else { [string]$output }
        throw "tar 実行に失敗しました: $message"
    }
    return $output
}

function Get-TempDirectory {
    $base = Join-Path -Path ([IO.Path]::GetTempPath()) -ChildPath "UnityPackageTools-$(New-Guid)"
    New-Item -ItemType Directory -Path $base | Out-Null
    return $base
}

function Escape-Html {
    param([string]$Value)

    if ($null -eq $Value) { return '' }
    return [string]$Value.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace('"', '&quot;').Replace("'", '&#39;')
}

function Resolve-PathForUnityPackagePath {
    param([string]$UnityPackagePath)

    if ([string]::IsNullOrWhiteSpace($UnityPackagePath)) {
        return $null
    }

    $normalized = $UnityPackagePath.Trim().Replace('/', '\')
    if ([IO.Path]::IsPathRooted($normalized) -or $normalized -match '^[A-Za-z]:') {
        throw "絶対パスは展開できません: $UnityPackagePath"
    }
    $parts = $normalized -split '[\\]+'
    $safeParts = New-Object System.Collections.Generic.List[string]

    foreach ($part in $parts) {
        if ([string]::IsNullOrWhiteSpace($part)) {
            continue
        }

        if ($part -eq '.' -or $part -eq '..') {
            throw "相対パスの遡りは展開できません: $UnityPackagePath"
        }

        $clean = ($part.Trim() -replace '[<>:"/\\|?*]', '_').Trim()
        if ([string]::IsNullOrWhiteSpace($clean)) { continue }

        if ($clean -cmatch '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?$') {
            throw "Windows 予約名は展開できません: $part"
        }

        $safeParts.Add($clean)
    }

    if ($safeParts.Count -eq 0) {
        return $null
    }

    return [IO.Path]::Combine($safeParts.ToArray())
}

function Resolve-DestinationFolder {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,
        [string]$Destination
    )

    if ([string]::IsNullOrWhiteSpace($Destination)) {
        $base = Join-Path (Split-Path -Parent $PackagePath) (([IO.Path]::GetFileNameWithoutExtension($PackagePath)) + '_extracted')
    }
    else {
        $base = $Destination
    }

    $candidate = $base
    $index = 2
    while (Test-Path -LiteralPath $candidate) {
        $candidate = "$base-$index"
        $index += 1
    }

    return $candidate
}

function Read-PathnameFromTar {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,
        [Parameter(Mandatory = $true)]
        [string]$Hash,
        [Parameter(Mandatory = $true)]
        [string]$WorkingDir
    )

    $entry = "$Hash/pathname"
    $target = Join-Path $WorkingDir $entry

    Invoke-Tar @('-x', '-f', $PackagePath, '-C', $WorkingDir, $entry)
    if (-not (Test-Path -LiteralPath $target)) {
        return $null
    }

    $raw = Get-Content -LiteralPath $target -Raw -Encoding UTF8
    return $raw.Trim([char[]] @("`r", "`n"))
}

function Get-UnityPackageEntriesByTar {
    param([Parameter(Mandatory = $true)] [string]$PackagePath)

    $rawList = Invoke-Tar @('-tf', $PackagePath)

    $entries = @{}
    foreach ($line in $rawList) {
        $trimmed = [string]$line
        if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
        $trimmed = $trimmed.Trim().TrimEnd('/')

        $parts = $trimmed -split '/', 2
        if ($parts.Count -lt 2) { continue }
        $hash = $parts[0]
        if ($hash -notmatch '^[0-9a-f]{32}$') { continue }

        $payload = $parts[1]
        if (-not $entries.ContainsKey($hash)) {
            $entries[$hash] = [ordered]@{
                Hash = $hash
                PathName = $null
                HasAsset = $false
                HasMeta = $false
            }
        }

        switch ($payload) {
            'asset' {
                $entries[$hash].HasAsset = $true
            }
            'asset.meta' {
                $entries[$hash].HasMeta = $true
            }
            'pathname' {
                # defer path read
            }
        }
    }

    $tempRoot = Get-TempDirectory
    try {
        foreach ($hash in $entries.Keys) {
            $entries[$hash].PathName = Read-PathnameFromTar -PackagePath $PackagePath -Hash $hash -WorkingDir $tempRoot
        }
        return $entries.Values
    }
    finally {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Expand-TarWithDotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,
        [Parameter(Mandatory = $true)]
        [string]$WorkingDir
    )

    if (-not $script:HasTarApi) {
        throw 'System.Formats.Tar API が利用できません。'
    }

    [System.IO.Directory]::CreateDirectory($WorkingDir) | Out-Null
    [System.Formats.Tar.TarFile]::ExtractToDirectory($PackagePath, $WorkingDir)
}

function Get-UnityPackageEntriesFromDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $entries = New-Object System.Collections.ArrayList
    $dirs = Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue
    foreach ($dir in $dirs) {
        if ($dir.Name -notmatch '^[0-9a-f]{32}$') {
            continue
        }

        $pathnameFile = Join-Path $dir.FullName 'pathname'
        $pathText = $null
        if (Test-Path -LiteralPath $pathnameFile) {
            $pathText = (Get-Content -LiteralPath $pathnameFile -Raw -Encoding UTF8).Trim([char[]] @("`r", "`n"))
        }

        $assetPath = Join-Path $dir.FullName 'asset'
        $metaPath = Join-Path $dir.FullName 'asset.meta'

        $item = [PSCustomObject]@{
            Hash = $dir.Name
            PathName = $pathText
            HasAsset = Test-Path -LiteralPath $assetPath
            HasMeta = Test-Path -LiteralPath $metaPath
        }

        $null = $entries.Add($item)
    }

    return $entries
}

function Get-UnityPackageEntriesFallback {
    param([Parameter(Mandatory = $true)] [string]$PackagePath)

    $working = Get-TempDirectory
    try {
        Expand-TarWithDotNet -PackagePath $PackagePath -WorkingDir $working
        return Get-UnityPackageEntriesFromDirectory -Root $working
    }
    finally {
        Remove-Item -LiteralPath $working -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Get-UnityPackageEntries {
    param([Parameter(Mandatory = $true)] [string]$PackagePath)

    if ($script:UseTar) {
        try {
            return Get-UnityPackageEntriesByTar -PackagePath $PackagePath
        }
        catch {
            if ($script:HasTarApi) {
                $script:UseTar = $false
                return Get-UnityPackageEntriesFallback -PackagePath $PackagePath
            }
            throw
        }
    }

    return Get-UnityPackageEntriesFallback -PackagePath $PackagePath
}

function Build-InspectHtml {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,
        [Parameter(Mandatory = $true)]
        [array]$Entries
    )

    $rows = New-Object System.Text.StringBuilder
    foreach ($entry in ($Entries | Sort-Object PathName)) {
        $display = if ([string]::IsNullOrWhiteSpace($entry.PathName)) { '(不明なパス)' } else { $entry.PathName }
        $safe = Resolve-PathForUnityPackagePath -UnityPackagePath $entry.PathName
        $status = if ($entry.HasAsset) { 'asset' } else { '' }
        if ($entry.HasMeta) {
            if ([string]::IsNullOrWhiteSpace($status)) {
                $status = 'meta'
            }
            else {
                $status = 'asset + meta'
            }
        }

        [void]$rows.AppendLine((
            "<tr><td>{0}</td><td>{1}</td><td>{2}</td><td>{3}</td></tr>" -f 
            (Escape-Html -Value $display),
            (Escape-Html -Value $status),
            (Escape-Html -Value $entry.Hash),
            (Escape-Html -Value $safe)
        ))
    }

    $body = if ($rows.Length -gt 0) { $rows.ToString() } else { '<tr><td colspan="4">有効な pathname が見つかりませんでした。</td></tr>' }

    return @"
<!DOCTYPE html>
<html lang=""ja"">
  <head>
    <meta charset=""utf-8"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
    <title>UnityPackage View - $((Escape-Html -Value (Split-Path $PackagePath -Leaf)))</title>
    <style>
      body { font-family: system-ui, -apple-system, Segoe UI, sans-serif; padding: 16px; }
      table { width: 100%; border-collapse: collapse; font-size: 14px; }
      th, td { border: 1px solid #ddd; padding: 8px; vertical-align: top; }
      th { text-align: left; background: #f6f6f6; }
      tr:nth-child(even) { background: #fafafa; }
    </style>
  </head>
  <body>
    <h1>UnityPackage 内容一覧</h1>
    <div><strong>ファイル:</strong> $(Escape-Html -Value $PackagePath)</div>
    <div><strong>総エントリ:</strong> $($Entries.Count)</div>
    <table>
      <thead>
        <tr>
          <th>pathname</th>
          <th>内容</th>
          <th>GUID</th>
          <th>安全化パス</th>
        </tr>
      </thead>
      <tbody>
$body
      </tbody>
    </table>
  </body>
</html>
"@
}

function Extract-SingleByTar {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,
        [Parameter(Mandatory = $true)]
        [string]$Hash,
        [Parameter(Mandatory = $true)]
        [string]$EntryName,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath,
        [Parameter(Mandatory = $true)]
        [string]$WorkingDir
    )

    $entry = "$Hash/$EntryName"
    $source = Join-Path $WorkingDir $entry

    Invoke-Tar @('-x', '-f', $PackagePath, '-C', $WorkingDir, $entry)
    if (-not (Test-Path -LiteralPath $source)) {
        throw "アーカイブ内に '$entry' が見つかりません。"
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $DestinationPath) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $DestinationPath -Force
}

function Invoke-UnityPackageInspect {
    param([Parameter(Mandatory = $true)] [string]$PackagePath)

    $app = Join-Path $PSScriptRoot 'src\UnityPackageExplorer.App\bin\Release\net9.0-windows\UnityPackageExplorer.App.exe'
    if (-not (Test-Path -LiteralPath $app)) {
        throw 'Viewer app is not built. Run Install-UnityPackageExplorerContextMenu.ps1 first.'
    }
    Start-Process -FilePath $app -ArgumentList ('"' + $PackagePath + '"')
    return $app
}

function Invoke-UnityPackageExtract {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,
        [string]$Destination
    )

    $destinationRoot = Resolve-DestinationFolder -PackagePath $PackagePath -Destination $Destination
    New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null

    $entries = Get-UnityPackageEntries -PackagePath $PackagePath
    if (-not $script:UseTar) {
        $working = Get-TempDirectory
        try {
            Expand-TarWithDotNet -PackagePath $PackagePath -WorkingDir $working
            foreach ($item in Get-UnityPackageEntriesFromDirectory -Root $working) {
                if ([string]::IsNullOrWhiteSpace($item.PathName)) { continue }

                $safe = Resolve-PathForUnityPackagePath -UnityPackagePath $item.PathName
                if ([string]::IsNullOrWhiteSpace($safe)) { continue }

                $targetBase = Join-Path $destinationRoot $safe
                $sourceBase = Join-Path $working $item.Hash

                if ($item.HasAsset) {
                    $sourceAsset = Join-Path $sourceBase 'asset'
                    if (Test-Path -LiteralPath $sourceAsset) {
                        New-Item -ItemType Directory -Path (Split-Path -Parent $targetBase) -Force | Out-Null
                        Copy-Item -LiteralPath $sourceAsset -Destination $targetBase -Force
                    }
                }

                if ($item.HasMeta) {
                    $sourceMeta = Join-Path $sourceBase 'asset.meta'
                    if (Test-Path -LiteralPath $sourceMeta) {
                        New-Item -ItemType Directory -Path (Split-Path -Parent ($targetBase + '.meta')) -Force | Out-Null
                        Copy-Item -LiteralPath $sourceMeta -Destination ($targetBase + '.meta') -Force
                    }
                }
            }
        }
        finally {
            Remove-Item -LiteralPath $working -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    else {
        $working = Get-TempDirectory
        try {
            foreach ($item in $entries) {
                if ([string]::IsNullOrWhiteSpace($item.PathName)) { continue }

                $safe = Resolve-PathForUnityPackagePath -UnityPackagePath $item.PathName
                if ([string]::IsNullOrWhiteSpace($safe)) { continue }

                $targetBase = Join-Path $destinationRoot $safe
                if ($item.HasAsset) {
                    Extract-SingleByTar -PackagePath $PackagePath -Hash $item.Hash -EntryName 'asset' -DestinationPath $targetBase -WorkingDir $working
                }

                if ($item.HasMeta) {
                    Extract-SingleByTar -PackagePath $PackagePath -Hash $item.Hash -EntryName 'asset.meta' -DestinationPath ($targetBase + '.meta') -WorkingDir $working
                }
            }
        }
        finally {
            Remove-Item -LiteralPath $working -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    return $destinationRoot
}

function Invoke-UnityPackageOpen {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath,
        [string]$Destination
    )

    $output = Invoke-UnityPackageExtract -PackagePath $PackagePath -Destination $Destination
    Start-Process -FilePath 'explorer.exe' -ArgumentList ('"' + $output + '"')
    return $output
}

try {
    Test-EnvironmentSupport
    Test-UnityPackagePath -Path $File

    $result = switch ($Action) {
        'inspect' { Invoke-UnityPackageInspect -PackagePath $File }
        'extract' { Invoke-UnityPackageExtract -PackagePath $File -Destination $Destination }
        'open' { Invoke-UnityPackageOpen -PackagePath $File -Destination $Destination }
    }

    if ($Action -eq 'inspect') {
        Write-Host "[OK] viewer: $result"
    }
    else {
        Write-Host "[OK] extracted: $result"
    }
}
catch {
    Write-Error "[$(Get-ScriptName)] $_"
    exit 1
}


