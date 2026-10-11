# Original Showcase Sample

`UnityPackageExplorer-Showcase.zip` is generated entirely from source in this folder. It contains one UnityPackage with an original low-poly canvas satchel FBX, a procedural color texture, a Unity material, and a prefab that references them. It uses no purchased, downloaded, or third-party artwork.

The sample is dedicated to the public domain under [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/). The generator uses only the Python standard library.

Regenerate it with:

```powershell
python tools/samples/generate_showcase_sample.py
```

Pass `--output <path>` to write the ZIP elsewhere. The generated archive is suitable for opening directly in UnityPackage Explorer; the UnityPackage remains inside the ZIP and is not unpacked beside the source ZIP by the app.

## Material preview fixture

`generate_liltoon_material_fixture.py` creates an ignored `.artifacts/liltoon-material-preview-fixture.zip` with color-only, tinted texture, transparent, and cutout Prefab choices. Their original FBX includes a red opaque backdrop behind the foreground mesh so transparency and cutout are visible. The alpha-pattern texture is generated in code; no third-party artwork is included.

For a local decode/preprocessing load-cost case, use `--texture-size 4096 --output .artifacts/liltoon-material-preview-4k.zip`. This large ZIP stays ignored and is not part of releases.
