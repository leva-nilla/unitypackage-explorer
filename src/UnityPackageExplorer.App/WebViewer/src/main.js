import * as THREE from "three";
import { LilToonMaterial, enableLilToon, LILTOON_TEXTURE_SEMANTICS, LILTOON_PROPERTIES } from "@mochiya/three-liltoon";
import { buildScenePayload, toLinearColor, isTextureSemanticColor, fromHostMatrix } from "./viewer-core.js";

const canvas = document.querySelector("#viewer");
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: false, powerPreference: "high-performance" });
renderer.setPixelRatio(Math.min(devicePixelRatio || 1, 2));
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.toneMapping = THREE.NoToneMapping;
renderer.shadowMap.enabled = true;
const releaseLilToon = enableLilToon(renderer);
const scene = new THREE.Scene();
scene.background = new THREE.Color("#14191F");
scene.add(new THREE.HemisphereLight(0xffffff, 0x53515a, 1.25));
const keyLight = new THREE.DirectionalLight(0xffffff, 2.1);
keyLight.position.set(-3, 5, 4);
keyLight.castShadow = true;
keyLight.shadow.mapSize.set(1024, 1024);
scene.add(keyLight);
const camera = new THREE.PerspectiveCamera(35, 1, 0.01, 10000);
const root = new THREE.Group();
scene.add(root);
let active = null;
let pendingController = null;
let loadEpoch = 0;
let raf = 0;
let lastTime = 0;
let needsFrame = false;
let sphere = { target: new THREE.Vector3(), yaw: 0, pitch: 0.12, distance: 3, radius: 1 };
const keys = new Set();
const colorPropertyNames = new Set(LILTOON_PROPERTIES.filter((entry) => entry.type === "Color").map((entry) => entry.name));
let mouselookPosition = null;
let movementSpeedScale = 1;
let dragging = false;
let previousPointer = null;
let pointerMode = "orbit";

function post(message) { globalThis.chrome?.webview?.postMessage(message); }
function scheduleFrame() { needsFrame = true; if (!raf && !document.hidden) raf = requestAnimationFrame(render); }
function reportCamera() {
  globalThis.__upeViewerTestHooks?.cameraUpdated?.({
    position: camera.position.toArray(),
    forward: camera.getWorldDirection(new THREE.Vector3()).toArray(),
    distance: sphere.distance,
    movementSpeedScale,
    mouselook: mouselookPosition !== null,
  });
}
function disposeCurrent() {
  if (!active) return;
  root.clear();
  for (const g of active.geometries) g.dispose();
  for (const m of active.materials) m.dispose();
  for (const t of active.textures) t.dispose();
  active = null;
  scheduleFrame();
}
function resize() {
  const w = Math.max(1, canvas.clientWidth), h = Math.max(1, canvas.clientHeight);
  renderer.setSize(w, h, false); camera.aspect = w / h; camera.updateProjectionMatrix(); scheduleFrame();
}
new ResizeObserver(resize).observe(canvas);

function updateCamera() {
  if (mouselookPosition) {
    camera.position.copy(mouselookPosition);
    const cp = Math.cos(sphere.pitch);
    const direction = new THREE.Vector3(Math.sin(sphere.yaw) * cp, Math.sin(sphere.pitch), -Math.cos(sphere.yaw) * cp);
    camera.lookAt(camera.position.clone().add(direction));
    reportCamera();
    return;
  }
  const { target, yaw, pitch, distance } = sphere;
  const cp = Math.cos(pitch);
  camera.position.set(target.x + distance * cp * Math.sin(yaw), target.y + distance * Math.sin(pitch), target.z + distance * cp * Math.cos(yaw));
  camera.lookAt(target);
  reportCamera();
}
function frameScene() {
  mouselookPosition = null;
  const box = new THREE.Box3().setFromObject(root);
  if (box.isEmpty()) return;
  const size = box.getSize(new THREE.Vector3());
  sphere.target.copy(box.getCenter(new THREE.Vector3()));
  sphere.radius = Math.max(size.length() * 0.5, 0.01);
  sphere.distance = sphere.radius / Math.sin(THREE.MathUtils.degToRad(camera.fov * 0.5)) * 1.15;
  sphere.pitch = 0.12; sphere.yaw = 0; updateCamera(); scheduleFrame();
}
function render(time) {
  raf = 0;
  if (document.hidden) return;
  const dt = lastTime ? Math.min((time - lastTime) / 1000, 0.05) : 0;
  lastTime = time;
  if (keys.size) {
    const fast = keys.has("Shift");
    const speed = sphere.radius * (fast ? 2.4 : 0.8) * movementSpeedScale * dt;
    const forward = new THREE.Vector3(); camera.getWorldDirection(forward); forward.normalize();
    const right = new THREE.Vector3().crossVectors(forward, camera.up).normalize();
    const target = mouselookPosition ?? sphere.target;
    if (keys.has("w")) target.addScaledVector(forward, speed);
    if (keys.has("s")) target.addScaledVector(forward, -speed);
    if (keys.has("d")) target.addScaledVector(right, speed);
    if (keys.has("a")) target.addScaledVector(right, -speed);
    if (keys.has("e")) target.y += speed;
    if (keys.has("q")) target.y -= speed;
    updateCamera(); needsFrame = true;
  }
  renderer.render(scene, camera);
  globalThis.__upeViewerTestHooks?.renderedFrame?.({
    calls: renderer.info.render.calls,
    triangles: renderer.info.render.triangles,
    programCount: renderer.info.programs?.length ?? 0,
  });
  if (needsFrame || keys.size || active?.animated) { needsFrame = false; raf = requestAnimationFrame(render); }
}
function clearScene() {
  loadEpoch++; pendingController?.abort(); pendingController = null; active?.controller.abort(); disposeCurrent(); keys.clear();
  globalThis.__upeViewerTestHooks?.sceneCleared?.({ epoch: loadEpoch, meshCount: root.children.length });
}

async function loadScene(url, requestId) {
  const epoch = ++loadEpoch;
  pendingController?.abort();
  active?.controller.abort();
  disposeCurrent();
  const controller = new AbortController();
  pendingController = controller;
  let stagingResources = null;
  try {
    const response = await fetch(url, { signal: controller.signal, credentials: "omit", cache: "no-store" });
    if (!response.ok) throw new Error(`Scene request failed (${response.status})`);
    const data = buildScenePayload(await response.json());
    if (epoch !== loadEpoch || data.requestId !== requestId) return;
    const started = performance.now();
    const materialData = new Map(data.materials.map((m) => [m.id, m]));
    const textures = new Set(), materials = new Set(), geometries = new Set();
    stagingResources = { textures, materials, geometries };
    const warnings = [];
    const loader = new THREE.TextureLoader();
    const texturePromises = new Map();
    const loadTexture = (slot, url) => {
      const key = `${slot}\n${url}`;
      if (!texturePromises.has(key)) texturePromises.set(key, new Promise((resolve, reject) => loader.load(url, resolve, undefined, reject)));
      return texturePromises.get(key);
    };
    const makeMaterial = async (item) => {
      const textureMap = {};
      for (const [slot, textureUrl] of Object.entries(item.textures ?? {})) {
        try {
          const texture = await loadTexture(slot, textureUrl);
          if (isTextureSemanticColor(LILTOON_TEXTURE_SEMANTICS, slot)) texture.colorSpace = THREE.SRGBColorSpace;
          else texture.colorSpace = THREE.NoColorSpace;
          texture.flipY = false;
          textures.add(texture); textureMap[slot] = texture;
        } catch { warnings.push(`${item.name}: failed to load texture ${slot}`); }
      }
      if (item.isLilToon) {
        const props = { ...(item.properties ?? {}) };
        // Unity .mat color values are authored in sRGB; shader uniforms expect linear values.
        for (const [key, value] of Object.entries(props)) {
          if (Array.isArray(value) && colorPropertyNames.has(key)) props[key] = toLinearColor(value);
        }
        const multi = item.shaderVariant === "multi" || item.shaderVariant === "multi-outline";
        const outline = item.shaderVariant === "outline" || item.shaderVariant === "multi-outline";
        if (outline) props._UseOutline = 1;
        const shaderVariant = outline ? "Hidden/lilToonOutline" : multi ? null : item.shaderVariant;
        if (multi) warnings.push(`${item.name}: lilToon Multi is converted to the corresponding regular mode; Multi-specific behavior may differ.`);
        const materialOptions = { name: item.name, renderMode: item.renderMode ?? "opaque", properties: props, textures: textureMap };
        if (typeof shaderVariant === "string" && shaderVariant.length > 0) materialOptions.shaderVariant = shaderVariant;
        const mat = new LilToonMaterial(materialOptions);
        warnings.push(...mat.getWarnings().map((warning) => `${item.name}: ${warning.message}`));
        materials.add(mat); return mat;
      }
      warnings.push(`${item.name}: unsupported shader; using standard material`);
      const color = item.properties?._Color;
      const renderMode = item.renderMode;
      const mat = new THREE.MeshStandardMaterial({
        name: item.name,
        color: Array.isArray(color) && colorPropertyNames.has("_Color") ? new THREE.Color(...toLinearColor(color).slice(0, 3)) : 0xffffff,
        opacity: renderMode === "transparent" && Array.isArray(color) ? color[3] ?? 1 : 1,
        transparent: renderMode === "transparent",
        alphaTest: renderMode === "cutout" && typeof item.properties?._Cutoff === "number" ? item.properties._Cutoff : 0,
        roughness: 0.85,
      });
      const mainTexture = textureMap._MainTex;
      if (mainTexture) mat.map = mainTexture;
      materials.add(mat); return mat;
    };
    const mats = new Map(await Promise.all(data.materials.map(async (m) => [m.id, await makeMaterial(m)])));
    if (epoch !== loadEpoch) { for (const g of geometries) g.dispose(); for (const m of materials) m.dispose(); for (const t of textures) t.dispose(); return; }
    for (const item of data.meshes) {
      const g = new THREE.BufferGeometry(); geometries.add(g);
      g.setAttribute("position", new THREE.Float32BufferAttribute(item.positions, 3));
      g.setIndex(item.indices);
      if (item.normals?.length) g.setAttribute("normal", new THREE.Float32BufferAttribute(item.normals, 3)); else g.computeVertexNormals();
      if (item.uvs?.length) g.setAttribute("uv", new THREE.Float32BufferAttribute(item.uvs, 2));
      const mesh = new THREE.Mesh(g, mats.get(item.materialId));
      mesh.name = item.name; mesh.castShadow = true; mesh.receiveShadow = true;
      // fromArray consumes Three.js column-major elements; host intentionally sends row-major.
      mesh.matrix.copy(fromHostMatrix(THREE, item.matrix)); mesh.matrixAutoUpdate = false; mesh.matrixWorldNeedsUpdate = true;
      root.add(mesh);
    }
    if (epoch !== loadEpoch) { disposeCurrent(); return; }
    active = { controller, geometries, materials, textures, animated: data.materials.some((m) => Object.entries(m.properties ?? {}).some(([key, value]) => key.endsWith("_ScrollRotate") && Array.isArray(value) && value.slice(0, 3).some((v) => Math.abs(v) > 0))) };
    pendingController = null;
    scene.background = new THREE.Color(data.background ?? "#14191F");
    frameScene();
    const triangleCount = data.meshes.reduce((count, mesh) => count + mesh.indices.length / 3, 0);
    globalThis.__upeViewerTestHooks?.sceneLoaded?.({
      requestId,
      meshCount: root.children.length,
      materials: data.materials.map((materialData) => {
        const material = mats.get(materialData.id);
        return {
          id: materialData.id,
          isLilToon: material.isLilToonMaterial === true,
          shaderKey: material.isLilToonMaterial ? material.shaderKey : null,
          properties: material.isLilToonMaterial ? material.lilToonProperties : {},
          textures: material.isLilToonMaterial ? Object.fromEntries(Object.entries(material.lilToonTextures).map(([key, texture]) => [key, Boolean(texture)])) : {},
          warnings: material.isLilToonMaterial ? material.getWarnings().map((warning) => warning.message) : [],
        };
      }),
    });
    post({ type: "sceneReady", requestId, meshCount: data.meshes.length, triangleCount, warnings, durationMs: performance.now() - started });
  } catch (error) {
    if (pendingController === controller) pendingController = null;
    if (stagingResources && epoch === loadEpoch) {
      for (const g of stagingResources.geometries) g.dispose();
      for (const m of stagingResources.materials) m.dispose();
      for (const t of stagingResources.textures) t.dispose();
    }
    if (error?.name !== "AbortError" && epoch === loadEpoch) post({ type: "sceneError", requestId, message: String(error?.message ?? error) });
  }
}

globalThis.chrome?.webview?.addEventListener("message", (event) => {
  const message = event.data;
  if (!message || typeof message !== "object") return;
  if (message.type === "clearScene") clearScene();
  else if (message.type === "frameScene") frameScene();
  else if (message.type === "loadScene" && Number.isSafeInteger(message.requestId) && typeof message.url === "string") loadScene(message.url, message.requestId);
});

canvas.addEventListener("contextmenu", (event) => event.preventDefault());
canvas.addEventListener("pointerdown", (event) => {
  if (event.button !== 2 && event.button !== 0) return;
  dragging = true; pointerMode = event.button === 2 ? "look" : "orbit"; previousPointer = { x: event.clientX, y: event.clientY };
  if (pointerMode === "look") {
    mouselookPosition = camera.position.clone();
    const direction = camera.getWorldDirection(new THREE.Vector3()).normalize();
    sphere.yaw = Math.atan2(direction.x, -direction.z);
    sphere.pitch = Math.asin(THREE.MathUtils.clamp(direction.y, -1, 1));
    reportCamera();
  }
  canvas.setPointerCapture(event.pointerId); canvas.focus();
});
canvas.addEventListener("pointermove", (event) => {
  if (!dragging || !previousPointer) return;
  const dx = event.clientX - previousPointer.x, dy = event.clientY - previousPointer.y;
  previousPointer = { x: event.clientX, y: event.clientY };
  if (pointerMode === "look") { sphere.yaw += dx * 0.006; sphere.pitch = THREE.MathUtils.clamp(sphere.pitch - dy * 0.006, -1.52, 1.52); }
  else { sphere.yaw -= dx * 0.006; sphere.pitch = THREE.MathUtils.clamp(sphere.pitch + dy * 0.006, -1.52, 1.52); }
  updateCamera(); scheduleFrame();
});
const releasePointer = () => {
  if (dragging && pointerMode === "look" && mouselookPosition) {
    const direction = camera.getWorldDirection(new THREE.Vector3()).normalize();
    sphere.target.copy(camera.position).addScaledVector(direction, sphere.distance);
    const offset = camera.position.clone().sub(sphere.target);
    sphere.yaw = Math.atan2(offset.x, offset.z);
    sphere.pitch = Math.asin(THREE.MathUtils.clamp(offset.y / sphere.distance, -1, 1));
    mouselookPosition = null;
    updateCamera();
  }
  dragging = false; previousPointer = null; keys.clear();
};
canvas.addEventListener("pointerup", releasePointer); canvas.addEventListener("pointercancel", releasePointer); canvas.addEventListener("lostpointercapture", releasePointer);
canvas.addEventListener("wheel", (event) => {
  event.preventDefault();
  if (dragging && pointerMode === "look") { movementSpeedScale = THREE.MathUtils.clamp(movementSpeedScale * Math.exp(-Math.sign(event.deltaY) * Math.min(Math.abs(event.deltaY), 240) * 0.004), 0.1, 10); reportCamera(); }
  else { sphere.distance *= Math.exp(Math.sign(event.deltaY) * Math.min(Math.abs(event.deltaY), 240) * 0.001 * (dragging && pointerMode === "orbit" ? 2.2 : 1)); sphere.distance = THREE.MathUtils.clamp(sphere.distance, sphere.radius * 0.03, sphere.radius * 100); updateCamera(); }
  scheduleFrame();
}, { passive: false });
canvas.tabIndex = 0;
window.addEventListener("keydown", (event) => {
  const key = event.key.length === 1 ? event.key.toLowerCase() : event.key;
  if (dragging && pointerMode === "look" && ["w", "a", "s", "d", "q", "e", "Shift"].includes(key)) { keys.add(key); event.preventDefault(); scheduleFrame(); }
  if (key === "f") frameScene();
});
window.addEventListener("keyup", (event) => keys.delete(event.key.length === 1 ? event.key.toLowerCase() : event.key));
window.addEventListener("blur", () => { keys.clear(); releasePointer(); });
document.addEventListener("visibilitychange", () => {
  if (document.hidden) { cancelAnimationFrame(raf); raf = 0; keys.clear(); releasePointer(); }
  else scheduleFrame();
});
window.addEventListener("unload", () => { clearScene(); releaseLilToon(); renderer.dispose(); });
resize();
