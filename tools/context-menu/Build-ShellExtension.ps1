[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vcvars = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC\Auxiliary\Build\vcvars64.bat' | Select-Object -First 1
if (-not $vcvars) { throw 'The x64 Visual C++ toolchain is required for the ZIP bundle context menu.' }
$source = Join-Path $repo 'src\UnityPackageExplorer.ShellExtension\UnityPackageExplorerShellCommand.cpp'
$exports = Join-Path $repo 'src\UnityPackageExplorer.ShellExtension\UnityPackageExplorer.ShellExtension.def'
$dll = Join-Path $output 'UnityPackageExplorer.ShellExtension.dll'
$intermediate = Join-Path $repo '.artifacts\shell-build'
New-Item -ItemType Directory -Path $intermediate -Force | Out-Null
$command = 'call "{0}" >nul && cl.exe /nologo /LD /EHsc /W4 /std:c++20 /MT /DUNICODE /D_UNICODE /DNOMINMAX "{1}" /Fe:"{2}" /link /DEF:"{3}" /IMPLIB:"{4}" Ole32.lib Shell32.lib Shlwapi.lib Advapi32.lib' -f $vcvars, $source, $dll, $exports, (Join-Path $intermediate 'UnityPackageExplorer.ShellExtension.lib')
Push-Location $intermediate
try {
    & $env:ComSpec /d /s /c $command
    if ($LASTEXITCODE -ne 0) { throw 'Native Explorer command build failed.' }
} finally { Pop-Location }
