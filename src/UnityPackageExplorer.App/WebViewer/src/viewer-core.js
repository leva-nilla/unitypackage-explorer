export const srgbToLinear = (value) => {
  const channel = Math.max(0, value);
  return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
};

export function toLinearColor(value) {
  if (!Array.isArray(value)) return value;
  return value.map((channel, index) => index === 3 ? channel : srgbToLinear(channel));
}

export function isTextureSemanticColor(semantics, slot) {
  return semantics?.[slot] === "color";
}

export function buildScenePayload(scene) {
  if (!scene || !Number.isSafeInteger(scene.requestId) || !Array.isArray(scene.meshes) || !Array.isArray(scene.materials)) {
    throw new Error("Invalid scene payload");
  }
  const ids = new Set(scene.materials.map((m) => m.id));
  for (const mesh of scene.meshes) {
    if (!ids.has(mesh.materialId)) throw new Error(`Mesh '${mesh.name}' references missing material '${mesh.materialId}'`);
    if (mesh.positions.length % 3 !== 0 || mesh.indices.length % 3 !== 0) throw new Error(`Mesh '${mesh.name}' has invalid triangle data`);
    if (mesh.normals.length && mesh.normals.length !== mesh.positions.length) throw new Error(`Mesh '${mesh.name}' has invalid normals`);
    if (mesh.uvs.length && mesh.uvs.length !== mesh.positions.length / 3 * 2) throw new Error(`Mesh '${mesh.name}' has invalid UVs`);
  }
  return scene;
}

export function fromHostMatrix(three, matrix) {
  if (!Array.isArray(matrix) || matrix.length !== 16) throw new Error("Mesh matrix must have 16 row-major values");
  // A .NET row-major row-vector array is the same sequential data Three.js
  // consumes as a column-major column-vector matrix. fromArray performs the
  // required interpretation; transposing again would undo the conversion.
  return new three.Matrix4().fromArray(matrix);
}
