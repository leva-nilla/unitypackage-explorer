# Preview verification harness

Checks that the Explorer preview handler really renders a package on a real Windows
session, without driving the Explorer UI.

Explorer's preview pane activates the handler through `prevhost.exe`. This harness
activates the same registered COM class with `CLSCTX_LOCAL_SERVER`, gives it a host
window, waits for the child window to render, captures it to a PNG, and reports pixel
statistics. It then unloads the handler and checks the measured teardown time, whether
the child window disappeared, and whether request scratch folders were left behind.

Driving the preview pane with UI automation was avoided on purpose: it depends on focus,
pane state, language, and DWM composition, so it breaks easily.

## Requirements

- Windows 10/11 x64 with an interactive desktop session (not a headless CI runner).
- The .NET 10 Desktop Runtime, because the preview handler is framework-dependent.
- A registered preview handler that points at the build under test.
  Install the setup, or pass `-Register` to write the registration from a publish folder
  (this needs elevation for the machine-wide COM registration).
- The file under test must not carry Mark of the Web. Windows refuses to preview those by
  design; unblock trusted files first (see `Unblock-MotW-Folder.bat` in the release tools).

## Quick start

Check the installed build against a real package:

```powershell
.\tools\PreviewVerification\Invoke-PreviewCheck.ps1 -File 'D:\samples\Avatar.zip'
```

Check several files and also confirm the app removes its temporary folders on exit:

```powershell
.\tools\PreviewVerification\Invoke-PreviewCheck.ps1 `
    -File 'D:\samples\Avatar.zip','D:\samples\Model.unitypackage' `
    -IncludeApp
```

Check a build before installing it (publish first, then register):

```powershell
dotnet publish .\src\UnityPackageExplorer.App -c Release -o .\.artifacts\verify\install
dotnet publish .\src\UnityPackageExplorer.PreviewHandler -c Release -o .\.artifacts\verify\install\PreviewHandler
.\tools\PreviewVerification\Invoke-PreviewCheck.ps1 `
    -InstallDirectory .\.artifacts\verify\install `
    -File 'D:\samples\Avatar.zip' -Register
```

`-Register` records the previous registration and `Unregister-ExplorerPreview.ps1`
restores it after the run unless `-KeepRegistration` is passed.

## What it checks

| Check | How |
|---|---|
| Handler activates through the real surrogate | `CoCreateInstance` with `CLSCTX_LOCAL_SERVER` against the registered CLSID |
| File and stream initialization both work | Runs once with `IInitializeWithFile`, once with `IInitializeWithStream` (disable with `-SkipStream`) |
| Something actually rendered | PNG capture of the handler's child window with non-blank, colored, center-of-viewport pixel checks |
| The render did not fail silently | New `prevhost.log` lines from the handler process are scanned; a logged load failure fails the run even when the error screen has colored pixels |
| Preview does not hang Explorer on teardown | `Unload` is timed and must finish inside `--max-unload-ms` (default 5 s) |
| The child window is really torn down | The captured HWND must be gone within 5 s of `Unload` |
| Request scratch folders are cleaned up | Watches `%LOCALAPPDATA%\..\LocalLow\UnityPackageExplorer\PreviewRequests` during the run |
| App temporary folders are cleaned up | `-IncludeApp` launches the app, closes it, and watches `%TEMP%\UnityPackageExplorer` |

A pass means "the preview rendered content, shut down cleanly, and the handler did not log
a failure", not that the picture is correct. Look at the PNGs in the artifact folder for
material, texture, and lighting judgement.

## Options

| Option | Meaning |
|---|---|
| `-File <path[]>` | Packages to check (`.unitypackage`, `.zip`, `.rar`, `.prefab`) |
| `-InstallDirectory <path>` | Install or publish folder. Default `%LOCALAPPDATA%\Programs\UnityPackageExplorer` |
| `-Register` | Register the handler from `-InstallDirectory` when the CLSID points elsewhere |
| `-KeepRegistration` | Do not restore the previous registration after the run |
| `-OutputDirectory <path>` | Where PNG/JSON artifacts go. Default `.artifacts\preview-verification` |
| `-MinimumNonUniformRatio <0-1>` | Blank-window threshold over the whole window (default 0.005) |
| `-MinimumChromaticRatio <0-1>` | Colored-content threshold; status and error screens are gray (default 0.01) |
| `-MinimumCenterNonUniformRatio <0-1>` | Empty-viewport threshold for the center box (default 0.01). Set to 0 for list-only previews |
| `-SettleMilliseconds <n>` | How long to wait for rendering (default 60000; large archives index for a while) |
| `-SkipStream` | Skip the `IInitializeWithStream` run |
| `-IncludeApp` | Also check the app's temporary-folder cleanup |
| `-NoBuild` | Reuse the already-built harness |

The low-level harness can be called directly:

```powershell
.\tools\PreviewVerification\bin\Release\net10.0-windows\PreviewVerification.exe `
    --file 'D:\samples\Avatar.zip' --capture .\avatar.png --json .\avatar.json `
    --settle-ms 60000 --min-center-nonuniform 0.01
```

Its exit codes are `0` pass, `2` usage, `3` activation, `4` initialize or preview call,
`5` unhandled exception, `6` no child window, `7` blank capture, `8` unload failed or too
slow, `9` the child window survived `Unload`, and `10` the handler logged an error.

## Reading the metrics

- `nonUniformRatio`: share of pixels that differ from the most common color. A flat
  background with a short status line is still around 0.10-0.13, so this alone cannot
  prove a 3D render.
- `centerNonUniformRatio`: the same idea over the middle of the window (x 15-85%,
  y 15-55%), which is where the 3D viewport sits. A status-only preview is near 0.
- `chromaticRatio`: share of pixels with a saturated color. Gray status text and dark
  backgrounds are near 0; a textured model plus the axis gizmo is around 0.03.

## Limitations

- The handler-log check only sees failures the handler writes to `prevhost.log`. A package
  the handler treats as valid but that has no previewable content shows a status screen;
  the pixel checks catch that case.
- The harness needs an interactive desktop; it is not part of CI.
- The screen-copy fallback needs the verification window visible and unobstructed. Pass
  `--no-screen-capture` to the low-level harness if that is not acceptable.
- This does not verify the installer's dialogs, upgrade path, or Mark of the Web behavior;
  those stay manual checks.
