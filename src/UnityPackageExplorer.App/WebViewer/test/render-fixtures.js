function sphereGeometry(rings = 12, segments = 20) {
  const positions = [], normals = [], uvs = [], indices = [];
  for (let y = 0; y <= rings; y++) {
    const v = y / rings;
    const theta = v * Math.PI;
    for (let x = 0; x <= segments; x++) {
      const u = x / segments;
      const phi = u * Math.PI * 2;
      const nx = Math.sin(theta) * Math.cos(phi);
      const ny = Math.cos(theta);
      const nz = Math.sin(theta) * Math.sin(phi);
      positions.push(nx, ny, nz);
      normals.push(nx, ny, nz);
      uvs.push(u, 1 - v);
    }
  }
  for (let y = 0; y < rings; y++) {
    for (let x = 0; x < segments; x++) {
      const a = y * (segments + 1) + x;
      const b = a + segments + 1;
      const c = b + 1;
      const d = a + 1;
      indices.push(a, d, b, b, d, c);
    }
  }
  return { positions, normals, uvs, indices };
}

export function createEffectScene(requestId, baseUrl) {
  const geometry = sphereGeometry();
  const main = `${baseUrl}/test/render-fixtures/main.svg`;
  const normal = `${baseUrl}/test/render-fixtures/normal.svg`;
  const matcap = `${baseUrl}/test/render-fixtures/matcap.svg`;
  const emission = `${baseUrl}/test/render-fixtures/emission.svg`;
  const mask = `${baseUrl}/test/render-fixtures/mask.svg`;
  const effects = [
    { id: "shadow", name: "Toon shadow", properties: { _Color: [1, 0.78, 0.68, 1], _UseShadow: 1, _ShadowBorder: 0.45, _ShadowBlur: 0.08, _ShadowColor: [0.22, 0.3, 0.55, 1] }, textures: { _MainTex: main } },
    { id: "outline", name: "Outline", shaderVariant: "outline", properties: { _Color: [0.9, 0.52, 0.32, 1], _UseOutline: 1, _OutlineWidth: 0.045, _OutlineColor: [0.03, 0.02, 0.04, 1] }, textures: { _MainTex: main } },
    { id: "normal", name: "Normal map", properties: { _Color: [0.6, 0.85, 1, 1], _UseBumpMap: 1, _BumpScale: 1 }, textures: { _MainTex: main, _BumpMap: normal } },
    { id: "matcap", name: "MatCap", properties: { _Color: [0.78, 0.9, 1, 1], _UseMatCap: 1, _MatCapColor: [1, 1, 1, 1] }, textures: { _MainTex: main, _MatCapTex: matcap } },
    { id: "emission", name: "Emission", properties: { _Color: [0.75, 0.52, 0.35, 1], _UseEmission: 1, _EmissionColor: [1, 0.62, 0.16, 1] }, textures: { _MainTex: main, _EmissionMap: emission } },
    { id: "alpha-mask", name: "Alpha mask", renderMode: "cutout", properties: { _Color: [0.8, 0.9, 1, 1], _Cutoff: 0.5, _AlphaMaskMode: 1, _AlphaMaskScale: 1, _AlphaMaskValue: 0 }, textures: { _MainTex: main, _AlphaMask: mask } },
  ];
  const positions = [[-1.9, 0.95, 0], [0, 0.95, 0], [1.9, 0.95, 0], [-1.9, -0.95, 0], [0, -0.95, 0], [1.9, -0.95, 0]];
  return {
    requestId,
    background: "#14191F",
    meshes: effects.map((effect, index) => ({
      name: effect.name,
      ...geometry,
      matrix: [0.72,0,0,0, 0,0.72,0,0, 0,0,0.72,0, positions[index][0],positions[index][1],positions[index][2],1],
      materialId: effect.id,
    })),
    materials: effects.map(({ id, name, shaderVariant, renderMode, properties, textures }) => ({
      id, name, shaderVariant: shaderVariant ?? null, renderMode: renderMode ?? "opaque", properties, textures, isLilToon: true,
    })),
  };
}
