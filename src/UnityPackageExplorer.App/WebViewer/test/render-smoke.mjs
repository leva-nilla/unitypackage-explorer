import assert from "node:assert/strict";
import { createServer } from "node:http";
import { mkdir, readFile } from "node:fs/promises";
import { dirname, extname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright-core";
import { createEffectScene } from "./render-fixtures.js";

const viewerRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const fixtureRoot = resolve(viewerRoot, "test/render-fixtures");
const repositoryRoot = resolve(viewerRoot, "../../..");
const artifactRoot = resolve(repositoryRoot, ".artifacts/WebViewer-render-smoke");
const browserExecutable = process.env.BROWSER_EXECUTABLE ?? "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
let markSlowStarted;
const slowStarted = new Promise((resolvePromise) => { markSlowStarted = resolvePromise; });

const server = createServer(async (request, response) => {
  const url = new URL(request.url ?? "/", "http://localhost");
  if (url.pathname === "/favicon.ico") { response.writeHead(204).end(); return; }
  if (url.pathname.startsWith("/scenes/")) {
    if (url.pathname === "/scenes/slow") {
      markSlowStarted();
      setTimeout(() => {
        if (response.destroyed) return;
        response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify(createEffectScene(202, `http://${request.headers.host}`)));
      }, 350);
      return;
    }
    const id = Number(url.searchParams.get("id"));
    const requestId = Number.isSafeInteger(id) ? id : 201;
    response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify(createEffectScene(requestId, `http://${request.headers.host}`)));
    return;
  }
  if (url.pathname.startsWith("/test/render-fixtures/")) {
    const name = url.pathname.slice("/test/render-fixtures/".length);
    if (!["main.svg", "normal.svg", "matcap.svg", "emission.svg", "mask.svg"].includes(name)) { response.writeHead(404).end(); return; }
    try {
      response.writeHead(200, { "content-type": "image/svg+xml", "cache-control": "no-store" });
      response.end(await readFile(resolve(fixtureRoot, name)));
    } catch { response.writeHead(404).end(); }
    return;
  }
  const name = url.pathname === "/" ? "index.html" : url.pathname.slice(1);
  if (!/^(index\.html|viewer\.js|viewer\.css)$/.test(name)) { response.writeHead(404).end(); return; }
  try {
    const body = await readFile(resolve(viewerRoot, "dist", name));
    const contentType = extname(name) === ".js" ? "text/javascript" : extname(name) === ".css" ? "text/css" : "text/html";
    response.writeHead(200, { "content-type": contentType, "cache-control": "no-store" }).end(body);
  } catch { response.writeHead(404).end(); }
});

await mkdir(artifactRoot, { recursive: true });
await new Promise((resolvePromise, reject) => {
  server.once("error", reject);
  server.listen(0, "127.0.0.1", resolvePromise);
});
const address = server.address();
const baseUrl = `http://127.0.0.1:${address.port}`;
let browser;

try {
  browser = await chromium.launch({
    headless: true,
    executablePath: browserExecutable,
    args: ["--enable-webgl", "--use-gl=angle", "--use-angle=swiftshader", "--ignore-gpu-blocklist", "--disable-gpu-sandbox"],
  });
  const page = await browser.newPage({ viewport: { width: 1100, height: 760 }, deviceScaleFactor: 1 });
  const pageErrors = [];
  const consoleErrors = [];
  const failedRequests = [];
  await page.addInitScript(() => {
    window.__upeViewerTestState = { messages: [], scenes: [], clears: [], frames: [], cameras: [] };
    window.__upeViewerTestHooks = {
      sceneLoaded: (scene) => window.__upeViewerTestState.scenes.push(scene),
      sceneCleared: (event) => window.__upeViewerTestState.clears.push(event),
      renderedFrame: (frame) => window.__upeViewerTestState.frames.push(frame),
      cameraUpdated: (camera) => window.__upeViewerTestState.cameras.push(camera),
    };
    window.chrome = { webview: {
      listeners: [],
      addEventListener(type, callback) { if (type === "message") this.listeners.push(callback); },
      postMessage(message) { window.__upeViewerTestState.messages.push(message); },
    } };
  });
  page.on("pageerror", (error) => pageErrors.push(String(error)));
  page.on("console", (message) => { if (message.type() === "error") consoleErrors.push(message.text()); });
  page.on("requestfailed", (request) => {
    const failure = request.failure()?.errorText ?? "failed";
    if (new URL(request.url()).pathname === "/scenes/slow" && failure === "net::ERR_ABORTED") return;
    failedRequests.push(`${request.url()}: ${failure}`);
  });
  await page.goto(baseUrl, { waitUntil: "load" });

  const send = (data) => page.evaluate((message) => window.chrome.webview.listeners.forEach((callback) => callback({ data: message })), data);
  const waitReady = async (requestId) => {
    try {
      await page.waitForFunction((id) => window.__upeViewerTestState.messages.some((message) => (message.type === "sceneReady" || message.type === "sceneError") && message.requestId === id), requestId, { timeout: 10000 });
    } catch (error) {
      console.error("Render smoke page state:", JSON.stringify(await page.evaluate(() => window.__upeViewerTestState)));
      console.error("Browser errors:", JSON.stringify({ pageErrors, consoleErrors, failedRequests }));
      throw error;
    }
  };
  await send({ type: "loadScene", requestId: 201, url: `${baseUrl}/scenes/effects?id=201` });
  await waitReady(201);
  const initialReady = await page.evaluate(() => window.__upeViewerTestState.messages.find((message) => message.requestId === 201));
  assert.equal(initialReady?.type, "sceneReady", `initial scene failed: ${JSON.stringify(initialReady)}`);
  assert.ok(!initialReady.warnings.some((warning) => /render.?mode.*ignored|deprecated/i.test(warning)), `render-mode compatibility warning: ${JSON.stringify(initialReady.warnings)}`);
  await page.waitForFunction(() => window.__upeViewerTestState.scenes.some((scene) => scene.requestId === 201));
  await page.waitForFunction(() => window.__upeViewerTestState.frames.some((frame) => frame.calls > 0));

  const sceneState = await page.evaluate(() => window.__upeViewerTestState.scenes.find((scene) => scene.requestId === 201));
  assert.equal(sceneState.meshCount, 8, "the effect fixture must load eight meshes");
  assert.equal(sceneState.materials.length, 8);
  assert.ok(sceneState.materials.every((material) => material.isLilToon && material.shaderKey), "all fixture surfaces should use generated lilToon shader variants");
  assert.ok(new Set(sceneState.materials.map((material) => material.shaderKey)).size >= 5, "effect toggles should specialize the shader programs");
  const materialById = Object.fromEntries(sceneState.materials.map((material) => [material.id, material]));
  for (const [id, property] of [["shadow", "_UseShadow"], ["outline", "_UseOutline"], ["normal", "_UseBumpMap"], ["matcap", "_UseMatCap"], ["emission", "_UseEmission"], ["alpha-mask", "_AlphaMaskMode"], ["cutout-outline-multi", "_UseOutline"], ["transparent-outline-multi", "_UseOutline"]]) {
    assert.ok(materialById[id], `missing ${id} fixture material`);
    assert.ok(materialById[id].properties[property], `${id} effect toggle was not retained`);
    assert.ok(Array.isArray(materialById[id].warnings), `${id} getWarnings() result was not collected`);
    assert.deepEqual(materialById[id].warnings, [], `${id} fixture has unsupported feature warnings`);
  }
  for (const id of ["outline", "cutout-outline-multi", "transparent-outline-multi"]) {
    assert.deepEqual(materialById[id].warnings, [], `${id} outline variant should not emit render-mode compatibility warnings`);
  }
  for (const [id, slot] of [["normal", "_BumpMap"], ["matcap", "_MatCapTex"], ["emission", "_EmissionMap"], ["alpha-mask", "_AlphaMask"]]) {
    assert.equal(materialById[id].textures[slot], true, `${id} texture should be assigned`);
  }

  let frameCount = await page.evaluate(() => window.__upeViewerTestState.frames.length);
  for (let index = 0; index < 3; index++) {
    await page.mouse.wheel(0, index % 2 ? -28 : 28);
    await page.waitForFunction((previousCount) => window.__upeViewerTestState.frames.length > previousCount, frameCount);
    frameCount = await page.evaluate(() => window.__upeViewerTestState.frames.length);
  }
  const lastFrame = await page.evaluate(() => window.__upeViewerTestState.frames.at(-1));
  assert.ok(lastFrame.calls >= 8, `expected actual WebGL draw calls for all surfaces, got ${lastFrame.calls}`);
  assert.ok(lastFrame.triangles > 100, `expected generated sphere triangles to render, got ${lastFrame.triangles}`);
  assert.ok(lastFrame.programCount >= 5, `expected specialized GPU programs, got ${lastFrame.programCount}`);
  assert.equal(await page.evaluate(() => document.querySelector("canvas").getContext("webgl2").getError()), 0, "WebGL reported a GL error");
  await page.screenshot({ path: resolve(artifactRoot, "effects.png"), fullPage: false });

  const initialCamera = await page.evaluate(() => window.__upeViewerTestState.cameras.at(-1));
  const canvasBox = await page.locator("canvas").boundingBox();
  await page.mouse.move(canvasBox.x + canvasBox.width / 2, canvasBox.y + canvasBox.height / 2);
  await page.mouse.down({ button: "right" });
  await page.mouse.move(canvasBox.x + canvasBox.width / 2 + 45, canvasBox.y + canvasBox.height / 2 - 18);
  const lookedCamera = await page.evaluate(() => window.__upeViewerTestState.cameras.at(-1));
  assert.equal(lookedCamera.mouselook, true, "right mouse should enter fixed-position mouselook");
  assert.ok(Math.hypot(...lookedCamera.position.map((value, index) => value - initialCamera.position[index])) < 1e-6, "RMB drag should change direction without orbiting the camera position");
  assert.ok(Math.hypot(...lookedCamera.forward.map((value, index) => value - initialCamera.forward[index])) > 0.02, "RMB drag did not turn the view");
  const speedBeforeWheel = lookedCamera.movementSpeedScale;
  await page.mouse.wheel(0, -120);
  const spedCamera = await page.evaluate(() => window.__upeViewerTestState.cameras.at(-1));
  assert.ok(spedCamera.movementSpeedScale > speedBeforeWheel, "wheel while holding RMB should increase camera movement speed");
  const beforeMove = spedCamera.position;
  await page.keyboard.down("w"); await page.waitForTimeout(180); await page.keyboard.up("w");
  const normalMoveCamera = await page.evaluate(() => window.__upeViewerTestState.cameras.at(-1));
  const normalDistance = Math.hypot(...normalMoveCamera.position.map((value, index) => value - beforeMove[index]));
  assert.ok(normalDistance > 0.02, "W should move camera while RMB is held");
  const beforeFastMove = normalMoveCamera.position;
  await page.keyboard.down("Shift"); await page.keyboard.down("w"); await page.waitForTimeout(180); await page.keyboard.up("w"); await page.keyboard.up("Shift");
  const fastMoveCamera = await page.evaluate(() => window.__upeViewerTestState.cameras.at(-1));
  const fastDistance = Math.hypot(...fastMoveCamera.position.map((value, index) => value - beforeFastMove[index]));
  assert.ok(fastDistance > normalDistance * 1.8, "Shift should accelerate held-W camera movement");
  await page.mouse.up({ button: "right" });
  const afterLookRelease = await page.evaluate(() => window.__upeViewerTestState.cameras.at(-1));
  const beforeUnheldKey = afterLookRelease.position;
  await page.keyboard.down("w"); await page.waitForTimeout(100); await page.keyboard.up("w");
  const afterUnheldKey = await page.evaluate(() => window.__upeViewerTestState.cameras.at(-1));
  assert.equal(afterUnheldKey.mouselook, false);
  assert.ok(Math.hypot(...afterUnheldKey.position.map((value, index) => value - beforeUnheldKey[index])) < 1e-6, "W should not move camera when RMB is not held");

  await send({ type: "loadScene", requestId: 202, url: `${baseUrl}/scenes/slow` });
  await slowStarted;
  await page.evaluate((message) => {
    window.chrome.webview.listeners.forEach((callback) => callback({ data: { type: "clearScene" } }));
    window.chrome.webview.listeners.forEach((callback) => callback({ data: message }));
  }, { type: "loadScene", requestId: 203, url: `${baseUrl}/scenes/effects?id=203` });
  await waitReady(203);
  assert.ok(await page.evaluate(() => window.__upeViewerTestState.messages.some((message) => message.type === "sceneReady" && message.requestId === 203)), "replacement scene failed");
  await new Promise((resolvePromise) => setTimeout(resolvePromise, 450));
  const loadMessages = await page.evaluate(() => window.__upeViewerTestState.messages.filter((message) => message.type === "sceneReady" || message.type === "sceneError"));
  assert.ok(loadMessages.some((message) => message.type === "sceneReady" && message.requestId === 203), "new request did not become ready");
  assert.ok(!loadMessages.some((message) => message.requestId === 202), "stale request reflected after clear and replacement");
  await send({ type: "clearScene" });
  const clearState = await page.evaluate(() => window.__upeViewerTestState.clears.at(-1));
  assert.equal(clearState.meshCount, 0, "clearScene must remove all scene meshes");
  assert.equal(await page.evaluate(() => document.querySelector("canvas").getContext("webgl2").getError()), 0, "WebGL reported a GL error after scene replacement/clear");
  assert.deepEqual(pageErrors, [], `page errors: ${pageErrors.join(" | ")}`);
  assert.deepEqual(consoleErrors, [], `console errors: ${consoleErrors.join(" | ")}`);
  assert.deepEqual(failedRequests, [], `failed requests: ${failedRequests.join(" | ")}`);
  console.log(`WebGL smoke passed: 6 effects, ${lastFrame.calls} draw calls, ${lastFrame.triangles} triangles, ${lastFrame.programCount} programs; stale scene suppressed. Screenshot: ${resolve(artifactRoot, "effects.png")}`);
} finally {
  await browser?.close();
  await new Promise((resolvePromise) => server.close(resolvePromise));
}
