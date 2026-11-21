# UnityPackage Explorer

[日本語](README.md) | English

**See what's inside your outfit ZIPs and RARs without opening Unity.**

A free Windows 11 x64 tool for browsing VRChat outfit packages without extracting them beside the originals. Preview Prefabs and textures, or combine separately distributed outfit and material packages into one file to import in Unity.

**[Download the latest release](https://github.com/leva-nilla/unitypackage-explorer/releases/latest)** · [Try the sample ZIP](tools/samples/UnityPackageExplorer-Showcase.zip) · [Quick start](#quick-start)

![The app showing an asset tree and a 3D preview of the original pouch Prefab](docs/media/showcase-prefab.png)

*The screenshot and sample ZIP use [original CC0 assets](tools/samples/README.md).*

## What you can do

- **Find assets** — Browse and search `.unitypackage` files and loose assets inside ZIPs and RARs. Filter by asset type.
- **Preview the contents** — Inspect Prefabs in 3D and view textures in the app. Switch to a lilToon-compatible view for supported materials. Single-volume RARs, ZIPs, and UnityPackages can also use the Windows Explorer preview pane.
- **Bundle outfits and materials** — Select ZIPs and single-volume RARs together and create one ZIP. Optionally merge the contents into a single `.unitypackage`.

## Install

1. Download and run `UnityPackageExplorer-Setup-<version>-x64.exe` from [GitHub Releases](https://github.com/leva-nilla/unitypackage-explorer/releases/latest).
2. To open or bundle files from Explorer, enable the legacy context-menu option during setup. The commands appear under **Show more options** on Windows 11.
3. Drag the [sample ZIP](tools/samples/UnityPackageExplorer-Showcase.zip) into the app to try the asset list and 3D preview.

**Requires Windows 11 x64.** You don't need to install Unity, the .NET SDK, or Visual Studio to use the app. The installer is unsigned. If SmartScreen warns you, verify the download source and the published `.sha256` file.

## Quick start

### Preview a file

| Open in | File types and steps |
| --- | --- |
| **App** | Drag in a `.zip`, `.rar`, `.unitypackage`, or `.prefab`, or right-click and choose **Open with UnityPackage Explorer** under **Show more options**. |
| **Explorer preview pane** | Enable the preview pane option in setup, then select a `.zip`, `.rar`, or `.unitypackage`. If Windows blocks the preview, open the file in the app instead. |

The app prioritizes a main Prefab and lets you switch candidates. Rotate or zoom the 3D preview, or switch to the lilToon-compatible view for supported materials. Browsing doesn't extract files alongside the source.

After previewing, you can move a ZIP or single-volume RAR into an existing category folder, or extract a standalone `.unitypackage`. Neither action overwrites existing destination files.

### Make one package to import in Unity

1. Select **two or more ZIPs or single-volume RARs** in the same folder. You can mix formats.
2. From **Show more options**, choose the command to bundle the selection into a ZIP.
3. Choose the folder name and turn on **Merge into one UnityPackage**. It is off by default and remembers your last choice.
4. Open the resulting `<name>_統合.zip`, take out `<name>.unitypackage`, and import that one file in Unity. With the option off, the ZIP keeps the original `.unitypackage` files separate.

**The app moves the original ZIPs and RARs only after verifying the result.** It doesn't change their contents. Loose files such as readmes stay in the source archives. Split RARs, archives with no `.unitypackage`, and asset conflicts stop the operation before the originals are moved.

Merging preserves asset GUIDs, paths, and contents, and removes identical duplicates. It does not add dependencies that weren't in the source packages, such as lilToon itself or SDK packages.

<details>
<summary>Open a split RAR</summary>

Put all volumes in the same folder: `name.part1.rar`, `name.part2.rar`, … (zero padding is fine), or `name.rar`, `name.r00`, `name.r01`, …. Drag any volume into the app, or right-click a `.rar` volume to open it.

The app doesn't join the source files on disk. To keep the set together, split RARs can't be bundled or moved by classification. If Explorer passes only one stream to the preview pane, use the app. Encrypted RARs and repair of incomplete sets aren't supported.

</details>

## Common questions

### Will it look exactly like Unity?

The preview is **an approximation for browsing assets**. The compatible view uses the unofficial `three-liltoon` port and shows effects such as shading, outlines, MatCap, and transparency on supported materials. It doesn't reproduce Unity's lighting, physics, custom components, or final shader output. Use Unity for imports and final visual checks.

### Why can't Explorer preview a download?

Windows may block previewing files with **Mark of the Web**. Open the file in the standalone app first. Setup doesn't change Windows security settings, and the app doesn't remove MotW automatically.

<details>
<summary>Enable preview for a trusted file</summary>

After verifying the source, use **Properties → General → Unblock** on that file.

The included `Unblock-MotW-Folder.bat` applies to **every file in the selected folder and its subfolders**. Pressing Enter without typing a path selects the folder containing the script. Don't use it where untrusted files may be mixed in.

</details>

### Do I need anything for the Explorer preview or the lilToon-compatible view?

- The **Explorer preview pane** is optional. Setup installs its bundled .NET 10 Desktop Runtime (x64) if needed. COM activation registration requires administrator approval. If the runtime can't be installed, you can still use the standalone app.
- The **lilToon-compatible view** uses Microsoft Edge WebView2 Runtime. If it isn't available, the app falls back to the lightweight viewer. You can get it from [Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/) if needed. Assets and rendering stay on your PC.

<details>
<summary>Preview and archive limits</summary>

- Text Prefabs can show hierarchy, FBX references, materials, and supported Variant and nested overrides. Unresolved meshes or bindings are reported and may be skipped or shown as a whole-FBX approximation. Individual bone deformation is not reproduced.
- The lightweight viewer approximates base color and alpha. The compatible view supports selected Standard, Lite, and Multi material modes. Unsupported shaders such as Fur, Refraction, Gem, and Tessellation aren't guessed.
- Streamed UnityPackage members are limited to 2 GiB each. Buffered members are limited to 512 MiB, the outer archive's total declared expansion to 8 GiB, and expanded nested packages together to 2 GiB. ZIP/RAR and TAR each have a limit of 100,000 entries.
- Solid RARs may be slower because earlier members have to be decoded in order.
- When Explorer supplies a stream, preview uses a temporary LocalLow copy limited to 8 GiB. The initial preview has a 30-second budget, as does each manual Prefab switch.

</details>

<details>
<summary>Upgrade or uninstall</summary>

Run the newer installer to upgrade. The old PowerShell-based install is cleaned up only if its ownership marker verifies it as this product. Setup stops if it can't verify files that would be affected.

To uninstall, use **Windows Settings → Apps → Installed apps**. The app removes or restores its own registrations without changing your default file associations or UserChoice.

</details>

## Development and license

See the [build instructions](README.md#開発ライセンス) for prerequisites and commands. The source is [MIT-licensed](LICENSE). Dependencies and their licenses are listed in [Third-Party Notices](THIRD-PARTY-NOTICES.md) and [ThirdPartyLicenses](ThirdPartyLicenses/).
