import { mkdir, copyFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(fileURLToPath(import.meta.url));
const dist = resolve(root, "dist");
await mkdir(dist, { recursive: true });
await copyFile(resolve(root, "index.html"), resolve(dist, "index.html"));
await copyFile(resolve(root, "viewer.css"), resolve(dist, "viewer.css"));
