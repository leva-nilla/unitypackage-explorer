import test from "node:test";
import assert from "node:assert/strict";
import * as THREE from "three";
import { srgbToLinear, toLinearColor, isTextureSemanticColor, buildScenePayload, fromHostMatrix, resolveLilToonShaderVariant } from "../src/viewer-core.js";

test("converts RGB channels from sRGB while preserving alpha", () => {
  assert.deepEqual(toLinearColor([0, 0.5, 1, 0.25]), [0, srgbToLinear(0.5), 1, 0.25]);
});

test("uses package texture semantics rather than guessed slot names", () => {
  assert.equal(isTextureSemanticColor({ _MainTex: "color", _BumpMap: "normal", _Mask: "data" }, "_MainTex"), true);
  assert.equal(isTextureSemanticColor({ _BumpMap: "normal" }, "_BumpMap"), false);
});

test("selects outline shader variants that match opaque, cutout, and transparent materials", () => {
  assert.equal(resolveLilToonShaderVariant("opaque", "outline"), "Hidden/lilToonOutline");
  assert.equal(resolveLilToonShaderVariant("cutout", "outline"), "Hidden/lilToonCutoutOutline");
  assert.equal(resolveLilToonShaderVariant("transparent", "outline"), "Hidden/lilToonTransparentOutline");
});

test("maps regular and Multi shader variants to the matching supported render mode", () => {
  assert.equal(resolveLilToonShaderVariant("opaque", null), "lilToon");
  assert.equal(resolveLilToonShaderVariant("cutout", null), "Hidden/lilToonCutout");
  assert.equal(resolveLilToonShaderVariant("transparent", null), "Hidden/lilToonTransparent");
  assert.equal(resolveLilToonShaderVariant("cutout", "multi-outline"), "Hidden/lilToonCutoutOutline");
  assert.equal(resolveLilToonShaderVariant("transparent", "multi-outline"), "Hidden/lilToonTransparentOutline");
  assert.equal(resolveLilToonShaderVariant("transparent", "multi"), "Hidden/lilToonTransparent");
});

test("validates the host scene contract and material references", () => {
  const good = { requestId: 2, meshes: [{ name: "x", positions: [0,0,0, 1,0,0, 0,1,0], normals: [], uvs: [], indices: [0,1,2], matrix: Array(16).fill(0), materialId: "m" }], materials: [{ id: "m" }] };
  assert.equal(buildScenePayload(good), good);
  assert.throws(() => buildScenePayload({ ...good, meshes: [{ ...good.meshes[0], materialId: "missing" }] }), /missing material/);
});

test("interprets .NET row-major scale and translation as the equivalent Three matrix", () => {
  const matrix = fromHostMatrix(THREE, [2,0,0,0, 0,3,0,0, 0,0,4,0, 10,20,30,1]);
  assert.deepEqual(new THREE.Vector3(1, 1, 1).applyMatrix4(matrix).toArray(), [12, 23, 34]);
});
