#!/usr/bin/env python3
"""Build an original, small UnityPackage ZIP for native material-preview checks."""

from __future__ import annotations

import argparse
import gzip
import io
import re
import struct
import tarfile
import zipfile
import zlib
from pathlib import Path

from generate_showcase_sample import ASSETS as SHOWCASE, assets as showcase_assets, meta, png_chunk

HERE = Path(__file__).resolve().parent
OUTPUT = HERE.parent.parent / ".artifacts" / "liltoon-material-preview-fixture.zip"
TEXTURE_GUID = "a1000000000000000000000000000001"
BACKDROP_GUID = "a2000000000000000000000000000005"
VARIANTS = [
    ("color-only", "a2000000000000000000000000000001", "a3000000000000000000000000000001", "Assets/MaterialFixture/Materials/ColorOnly.mat", "Assets/MaterialFixture/Prefabs/ColorOnly.prefab", (0.85, 0.22, 0.12, 1.0), "", False),
    ("tinted", "a2000000000000000000000000000002", "a3000000000000000000000000000002", "Assets/MaterialFixture/Materials/Tinted.mat", "Assets/MaterialFixture/Prefabs/Tinted.prefab", (0.25, 0.72, 0.9, 1.0), "", True),
    ("transparent", "a2000000000000000000000000000003", "a3000000000000000000000000000003", "Assets/MaterialFixture/Materials/Transparent.mat", "Assets/MaterialFixture/Prefabs/Transparent.prefab", (1.0, 1.0, 1.0, 0.45), "Transparent", True),
    ("cutout", "a2000000000000000000000000000004", "a3000000000000000000000000000004", "Assets/MaterialFixture/Materials/Cutout.mat", "Assets/MaterialFixture/Prefabs/Cutout.prefab", (1.0, 1.0, 1.0, 1.0), "TransparentCutout", True),
    ("opaque-alpha", "a2000000000000000000000000000006", "a3000000000000000000000000000006", "Assets/MaterialFixture/Materials/OpaqueAlpha.mat", "Assets/MaterialFixture/Prefabs/OpaqueAlpha.prefab", (1.0, 1.0, 1.0, 0.4), "Opaque", True),
]


def texture(size: int) -> bytes:
    width = height = size
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        for x in range(width):
            checker = ((x // 16) + (y // 16)) % 2
            rgb = (237, 160, 51) if checker else (42, 116, 164)
            # Large transparent diamonds show cutout holes and transparent blending.
            center = size // 2
            alpha = 0 if abs(x - center) + abs(y - center) < size // 4 else 255
            rows.extend((*rgb, alpha))
    return (b"\x89PNG\r\n\x1a\n" + png_chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + png_chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + png_chunk(b"IEND", b""))


def material(color: tuple[float, float, float, float], render_type: str, with_texture: bool, name: str = "PouchCanvas") -> bytes:
    color_text = ", ".join(f"{key}: {value:g}" for key, value in zip("rgba", color))
    tex_block = (f"    - _MainTex:\n        m_Texture: {{fileID: 2800000, guid: {TEXTURE_GUID}, type: 3}}\n"
                 "        m_Scale: {x: 1, y: 1}\n        m_Offset: {x: 0, y: 0}\n") if with_texture else ""
    tag = f"  stringTagMap:\n    RenderType: {render_type}\n" if render_type else ""
    cutoff = "    - _Cutoff: 0.5\n" if render_type == "TransparentCutout" else ""
    return (f"%YAML 1.1\n--- !u!21 &2100000\nMaterial:\n  serializedVersion: 8\n  m_Name: {name}\n  m_Shader: {{fileID: 0}}\n{tag}"
            f"  m_SavedProperties:\n    m_TexEnvs:\n{tex_block}    m_Ints: []\n    m_Floats:\n{cutoff}    m_Colors:\n    - _Color: {{{color_text}}}\n").encode()


def prefab(model_guid: str, material_guid: str, name: str) -> bytes:
    return f'''%YAML 1.1
--- !u!1 &1000
GameObject:
  m_Component:
  - component: {{fileID: 1001}}
  - component: {{fileID: 1002}}
  - component: {{fileID: 1003}}
  m_Name: {name}
--- !u!4 &1001
Transform:
  m_GameObject: {{fileID: 1000}}
  m_LocalRotation: {{x: 0, y: 0, z: 0, w: 1}}
  m_LocalPosition: {{x: 0, y: 0, z: 0}}
  m_LocalScale: {{x: 1, y: 1, z: 1}}
  m_Children: []
  m_Father: {{fileID: 0}}
--- !u!33 &1002
MeshFilter:
  m_GameObject: {{fileID: 1000}}
  m_Mesh: {{fileID: 4300000, guid: {model_guid}, type: 3}}
--- !u!23 &1003
MeshRenderer:
  m_GameObject: {{fileID: 1000}}
  m_Enabled: 1
  m_Materials:
  - {{fileID: 2100000, guid: {material_guid}, type: 2}}
  - {{fileID: 2100000, guid: {BACKDROP_GUID}, type: 2}}
'''.encode()


def add_backdrop_mesh(fbx: bytes) -> bytes:
    text = fbx.decode()
    geometry = re.search(r"    Geometry: 1000000,.*?(?=    Model: 2000000,)", text, re.S).group()
    model = re.search(r"    Model: 2000000,.*?(?=    Material: 3000000,)", text, re.S).group()
    material_block = re.search(r"    Material: 3000000,.*?(?=    Texture: 4000000,)", text, re.S).group()
    geometry = geometry.replace("1000000", "1000001").replace("Geometry::PouchMesh", "Geometry::PreviewBackdrop")
    model = model.replace("2000000", "2000001").replace("Model::Pouch", "Model::PreviewBackdrop")
    model = model.replace('P: "Lcl Translation", "Lcl Translation", "", "A",0,0,0', 'P: "Lcl Translation", "Lcl Translation", "", "A",0,0,-0.42')
    model = model.replace('"A",1,1,1', '"A",1.35,1.35,1')
    material_block = material_block.replace("3000000", "3000001").replace("Material::PouchCanvas", "Material::PreviewBackdrop")
    material_block = re.sub(r'"DiffuseColor", "Color", "", "A",[^ ]+', '"DiffuseColor", "Color", "", "A",0.82,0.18,0.12', material_block)
    text = text.replace('Count: 5\n    ObjectType: "Geometry" { Count: 1 } ObjectType: "Model" { Count: 1 }', 'Count: 8\n    ObjectType: "Geometry" { Count: 2 } ObjectType: "Model" { Count: 2 }')
    text = text.replace('ObjectType: "Material" { Count: 1 }', 'ObjectType: "Material" { Count: 2 }')
    # Insert copied objects next to their original object records.
    text = text.replace("    Model: 2000000,", geometry + "    Model: 2000000,", 1)
    text = text.replace("    Material: 3000000,", model + "    Material: 3000000,", 1)
    text = text.replace("    Texture: 4000000,", material_block + "    Texture: 4000000,", 1)
    text = text.replace('C: "OO",3000000,2000000', 'C: "OO",3000000,2000000 C: "OO",1000001,2000001 C: "OO",3000001,2000001 C: "OO",2000001,0')
    return text.encode()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=OUTPUT)
    parser.add_argument("--texture-size", type=int, choices=(256, 4096), default=256, help="Use 4096 to create a local-only load-cost fixture.")
    args = parser.parse_args()
    output = args.output
    payloads = showcase_assets()
    entries: list[tuple[str, str, bytes, bytes]] = []
    model_guid, model_path = SHOWCASE["model"]
    texture_path = "Assets/MaterialFixture/Textures/AlphaPattern.png"
    entries.append((model_guid, model_path, add_backdrop_mesh(payloads["model"][0]), payloads["model"][1]))
    entries.append((TEXTURE_GUID, texture_path, texture(args.texture_size), meta(TEXTURE_GUID, "TextureImporter:\n  textureType: 0\n")))
    entries.append((BACKDROP_GUID, "Assets/MaterialFixture/Materials/PreviewBackdrop.mat", material((0.82, 0.18, 0.12, 1), "", False, "PreviewBackdrop"), meta(BACKDROP_GUID, "NativeFormatImporter:\n  mainObjectFileID: 2100000\n")))
    for variant, mat_guid, prefab_guid, mat_path, prefab_path, color, render_type, has_texture in VARIANTS:
        mat_data = material(color, render_type, has_texture)
        prefab_data = prefab(model_guid, mat_guid, variant)
        entries.append((mat_guid, mat_path, mat_data, meta(mat_guid, "NativeFormatImporter:\n  mainObjectFileID: 2100000\n")))
        entries.append((prefab_guid, prefab_path, prefab_data, meta(prefab_guid, "PrefabImporter:\n")))
    package_bytes = io.BytesIO()
    with gzip.GzipFile(fileobj=package_bytes, mode="wb", mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode="w", format=tarfile.USTAR_FORMAT) as package:
            for guid, path, data, meta_data in entries:
                for kind, content in (("pathname", (path + "\n").encode()), ("asset", data), ("asset.meta", meta_data)):
                    item = tarfile.TarInfo(f"{guid}/{kind}")
                    item.size, item.mtime, item.mode = len(content), 0, 0o644
                    package.addfile(item, io.BytesIO(content))
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        item = zipfile.ZipInfo("LiltoonMaterialFixture.unitypackage", (2024, 1, 1, 0, 0, 0))
        item.compress_type, item.external_attr = zipfile.ZIP_DEFLATED, 0o100644 << 16
        archive.writestr(item, package_bytes.getvalue())
    print(f"Created {output.resolve()} ({output.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
