# HelixToolkit.SharpDX lifetime fixes

This directory vendors `Source/HelixToolkit.SharpDX` from upstream **v3.1.2**,
commit `0d10a9fc27bbe06d4ce74c580612217af14d3fc6`:
https://github.com/helix-toolkit/helix-toolkit/tree/0d10a9fc27bbe06d4ce74c580612217af14d3fc6/Source/HelixToolkit.SharpDX

The MIT license, AUTHORS, CONTRIBUTORS and public upstream signing key
(`Source/HelixToolkit.snk`) are included. This is a locally maintained build,
not an official Helix Toolkit release.

## Changes from upstream

- `Core/MorphTargetUploaderCore.cs`: dispose `mtDeltasSRV` and `mtOffsetsSRV`
  before their buffers on detach.
- `Core/OITDepthPeeling.cs`: dispose the reference returned by the render target
  view's `Resource` getter after copying/resolving the background. The getter
  acquires a COM reference on every rendered frame.
- The project targets only net8.0 (the framework previously selected by this
  application), references the unchanged 3.1.2 peer packages, and packs as
  **3.1.2.1-upx.1**. Assembly identity remains 3.1.2.0 with the upstream public key.
- `Resources/*.cso` contains the 103 unmodified shader bytecode resources
  extracted from the official 3.1.2 net8.0 assembly. No shader compilation or
  rendering-algorithm change is introduced. `Resources/SHA256SUMS` records each
  resource hash. Official assembly SHA-256:
  `6dfe0e03457a2c0262a0d7bb47930569c7182359ad39e66c33cef1b13406840d`.

No other upstream C# or texture sources are changed. When upgrading, compare
these two fixes against upstream and remove the local package only once both
lifetime regressions pass on the replacement.

## Build

Run `tools/dependencies/Build-PatchedHelix.ps1` before solution restore/build.
It verifies the shader hashes and builds the package into `.artifacts/packages`.
The root NuGet.config makes this local feed available; consumers require the
exact patched package version, so a missing bootstrap fails rather than silently
shipping the original assembly. CI and Build-Setup.ps1 perform this bootstrap.

## Evidence and regression scope

With the original assembly, repeated complete previews accumulated roughly
1–2 GiB after unload. Native debug reports and a saved managed dump identified
14 undisposed morph SRVs (two on each of seven meshes), and COM creation tracking
identified unreleased OIT Resource getter references. Diagnostic-only balanced
releases brought the device's reference count from 15 to zero and stopped the
cumulative growth across three complete previews. GC/finalizers alone did not.

Regression tests exercise detach and both copy/resolve branches using a small
WARP D3D11 device, without WPF/D3D9 interop. Real Explorer repeated and cancelled
preview remains a separate release check. The product adds no forced GC, global
COM tracking, raw pointer releases, or software-rendering workaround.
