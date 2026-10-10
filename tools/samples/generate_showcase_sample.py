#!/usr/bin/env python3
"""Generate a deterministic, fully original UnityPackage Explorer sample ZIP."""

from __future__ import annotations

import argparse
import gzip
import io
import struct
import tarfile
import zipfile
import zlib
from pathlib import Path


HERE = Path(__file__).resolve().parent
DEFAULT_OUTPUT = HERE / "UnityPackageExplorer-Showcase.zip"
ASSETS = {
    "model": ("4c4b4d1a53fc4f7da008e9659e082101", "Assets/UnityPackageExplorerShowcase/Models/Pouch.fbx"),
    "texture": ("e819c34e870d4a879499f0a61769ac02", "Assets/UnityPackageExplorerShowcase/Textures/Canvas.png"),
    "material": ("a2bb9ed343e7494f84cc459943720f31", "Assets/UnityPackageExplorerShowcase/Materials/PouchCanvas.mat"),
    "prefab": ("97cb9b09474c4e58a72acd2075d352de", "Assets/UnityPackageExplorerShowcase/Prefabs/Pouch.prefab"),
}


def png_chunk(kind: bytes, data: bytes) -> bytes:
    block = kind + data
    return struct.pack(">I", len(data)) + block + struct.pack(">I", zlib.crc32(block) & 0xFFFFFFFF)


def create_texture() -> bytes:
    """Create original woven teal canvas with stitching and a small emblem."""
    width = height = 256
    pixels = bytearray()
    for y in range(height):
        pixels.append(0)  # PNG filter None
        for x in range(width):
            weave = 7 if (x // 4 + y // 4) % 2 == 0 else 0
            color = (32 + weave, 88 + weave, 98 + weave)
            if 10 <= x <= 245 and 10 <= y <= 245 and (x in (10, 11, 244, 245) or y in (10, 11, 244, 245)):
                color = (222, 184, 111)
            if 16 <= x <= 239 and 16 <= y <= 239 and (x in (16, 17, 238, 239) or y in (16, 17, 238, 239)):
                color = (17, 57, 66)
            if 82 <= x <= 174 and 72 <= y <= 166:
                color = (218, 177, 103)
            if 89 <= x <= 167 and 79 <= y <= 159:
                color = (35, 79, 87)
            # Small geometric mountain badge, entirely generated in this script.
            if 105 <= x <= 151 and 102 <= y <= 140 and y > 138 - abs(x - 128) // 2:
                color = (231, 199, 135)
            if 124 <= x <= 132 and 107 <= y <= 140:
                color = (231, 199, 135)
            if 118 <= x <= 138 and 194 <= y <= 211:
                color = (215, 173, 94)
            if 123 <= x <= 133 and 198 <= y <= 207:
                color = (24, 61, 69)
            pixels.extend(color)

    result = b"\x89PNG\r\n\x1a\n"
    result += png_chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
    result += png_chunk(b"IDAT", zlib.compress(bytes(pixels), 9))
    return result + png_chunk(b"IEND", b"")


def create_fbx() -> bytes:
    """Create one low-poly satchel mesh and a material using Canvas.png."""
    vertices = [
        (-0.58, -0.48, -0.22), (0.58, -0.48, -0.22), (0.48, 0.38, -0.22), (-0.48, 0.38, -0.22),
        (-0.58, -0.48, 0.22), (0.58, -0.48, 0.22), (0.48, 0.38, 0.22), (-0.48, 0.38, 0.22),
    ]
    faces = [(0, 3, 2, 1), (5, 6, 7, 4), (4, 7, 3, 0), (1, 2, 6, 5), (3, 7, 6, 2), (4, 0, 1, 5)]
    indices = []
    uv = []
    for face in faces:
        indices.extend((*face[:-1], -face[-1] - 1))
        uv.extend(((0.04, 0.04), (0.04, 0.96), (0.96, 0.96), (0.96, 0.04)))
    vertex_data = ",".join(f"{value:.6f}" for point in vertices for value in point)
    polygon_data = ",".join(map(str, indices))
    uv_data = ",".join(f"{value:.6f}" for point in uv for value in point)

    source = f'''; FBX 7.4.0 project file
FBXHeaderExtension:  {{ FBXHeaderVersion: 1003 FBXVersion: 7400 Creator: "UnityPackageExplorer Original Sample" }}
GlobalSettings:  {{ Version: 1000 Properties70:  {{
    P: "UpAxis", "int", "Integer", "",1
    P: "UpAxisSign", "int", "Integer", "",1
    P: "FrontAxis", "int", "Integer", "",2
    P: "FrontAxisSign", "int", "Integer", "",-1
    P: "CoordAxis", "int", "Integer", "",0
    P: "CoordAxisSign", "int", "Integer", "",1
    P: "UnitScaleFactor", "double", "Number", "",100
}} }}
Documents:  {{ Count: 1 Document: 1, "Scene", "Scene" {{ RootNode: 0 }} }}
References:  {{}}
Definitions:  {{ Version: 100 Count: 5
    ObjectType: "Geometry" {{ Count: 1 }} ObjectType: "Model" {{ Count: 1 }}
    ObjectType: "Material" {{ Count: 1 }} ObjectType: "Texture" {{ Count: 1 }} ObjectType: "Video" {{ Count: 1 }}
}}
Objects:  {{
    Geometry: 1000000, "Geometry::PouchMesh", "Mesh" {{
        GeometryVersion: 124
        Vertices: *24 {{ a: {vertex_data} }}
        PolygonVertexIndex: *24 {{ a: {polygon_data} }}
        LayerElementMaterial: 0 {{ Version: 101 Name: "" MappingInformationType: "AllSame" ReferenceInformationType: "IndexToDirect" Materials: *1 {{ a: 0 }} }}
        LayerElementUV: 0 {{ Version: 101 Name: "UVChannel_1" MappingInformationType: "ByPolygonVertex" ReferenceInformationType: "Direct" UV: *48 {{ a: {uv_data} }} }}
        Layer: 0 {{ Version: 100 LayerElement: {{ Type: "LayerElementMaterial" TypedIndex: 0 }} LayerElement: {{ Type: "LayerElementUV" TypedIndex: 0 }} }}
    }}
    Model: 2000000, "Model::Pouch", "Mesh" {{
        Version: 232
        Properties70: {{ P: "Lcl Translation", "Lcl Translation", "", "A",0,0,0 P: "Lcl Rotation", "Lcl Rotation", "", "A",0,0,0 P: "Lcl Scaling", "Lcl Scaling", "", "A",1,1,1 }}
        Shading: T Culling: "CullingOff"
    }}
    Material: 3000000, "Material::PouchCanvas", "" {{
        Version: 102 ShadingModel: "phong" MultiLayer: 0
        Properties70: {{ P: "DiffuseColor", "Color", "", "A",0.12,0.42,0.48 P: "Shininess", "double", "Number", "",18 }}
    }}
    Texture: 4000000, "Texture::Canvas", "" {{
        Type: "TextureVideoClip" Version: 202 TextureName: "Texture::Canvas" Media: ""
        FileName: "Canvas.png" RelativeFilename: "../Textures/Canvas.png"
        ModelUVTranslation: 0,0 ModelUVScaling: 1,1 Texture_Alpha_Source: "None" Cropping: 0,0,0,0
    }}
    Video: 5000000, "Video::Canvas.png", "Clip" {{
        Type: "Clip" UseMipMap: 0 Filename: "../Textures/Canvas.png" RelativeFilename: "../Textures/Canvas.png"
    }}
}}
Connections:  {{
    C: "OO",1000000,2000000 C: "OO",3000000,2000000
    C: "OP",4000000,3000000,"DiffuseColor" C: "OO",5000000,4000000 C: "OO",2000000,0
}}
Takes:  {{ Current: "" }}
'''
    return source.encode("utf-8")


def create_material() -> bytes:
    texture_guid = ASSETS["texture"][0]
    content = f'''%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  serializedVersion: 8
  m_ObjectHideFlags: 0
  m_Name: PouchCanvas
  m_Shader: {{fileID: 0}}
  m_SavedProperties:
    serializedVersion: 3
    m_TexEnvs:
    - _MainTex:
        m_Texture: {{fileID: 2800000, guid: {texture_guid}, type: 3}}
        m_Scale: {{x: 1, y: 1}}
        m_Offset: {{x: 0, y: 0}}
    m_Ints: []
    m_Floats: []
    m_Colors: []
'''
    return content.encode("utf-8")


def create_prefab() -> bytes:
    model_guid = ASSETS["model"][0]
    material_guid = ASSETS["material"][0]
    content = f'''%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &1000
GameObject:
  m_Component:
  - component: {{fileID: 1001}}
  - component: {{fileID: 1002}}
  - component: {{fileID: 1003}}
  m_Layer: 0
  m_Name: Original Canvas Satchel
  m_TagString: Untagged
  m_IsActive: 1
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
'''
    return content.encode("utf-8")


def meta(guid: str, importer: str) -> bytes:
    return f"fileFormatVersion: 2{chr(10)}guid: {guid}{chr(10)}{importer}".encode("utf-8")


def assets() -> dict[str, tuple[bytes, bytes]]:
    payloads = {"model": create_fbx(), "texture": create_texture(), "material": create_material(), "prefab": create_prefab()}
    importers = {
        "model": "ModelImporter:\n  serializedVersion: 22200\n  externalObjects: {}\n",
        "texture": "TextureImporter:\n  serializedVersion: 12\n  textureType: 0\n  sRGBTexture: 1\n",
        "material": "NativeFormatImporter:\n  mainObjectFileID: 2100000\n  externalObjects: {}\n",
        "prefab": "PrefabImporter:\n  externalObjects: {}\n",
    }
    return {key: (payload, meta(ASSETS[key][0], importers[key])) for key, payload in payloads.items()}


def generate(output: Path) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)
    package_bytes = io.BytesIO()
    with gzip.GzipFile(fileobj=package_bytes, mode="wb", mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode="w", format=tarfile.USTAR_FORMAT) as package:
            for key, (asset_data, meta_data) in assets().items():
                guid, path = ASSETS[key]
                for name, data in (("pathname", (path + "\n").encode("utf-8")), ("asset", asset_data), ("asset.meta", meta_data)):
                    entry = tarfile.TarInfo(f"{guid}/{name}")
                    entry.size = len(data)
                    entry.mtime = 0
                    entry.mode = 0o644
                    package.addfile(entry, io.BytesIO(data))

    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        item = zipfile.ZipInfo("UnityPackageExplorer-Showcase.unitypackage", (2024, 1, 1, 0, 0, 0))
        item.compress_type = zipfile.ZIP_DEFLATED
        item.external_attr = 0o100644 << 16
        archive.writestr(item, package_bytes.getvalue())
    print(f"Created {output.resolve()} ({output.stat().st_size:,} bytes)")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT, help="output ZIP path")
    args = parser.parse_args()
    generate(args.output)


if __name__ == "__main__":
    main()
