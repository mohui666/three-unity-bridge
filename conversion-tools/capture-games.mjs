import { execFileSync, spawn } from "node:child_process";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { chromium } from "../conversion-work/voxel-frontier/node_modules/playwright/index.mjs";

const repositoryRoot = resolve(import.meta.dirname, "..");
const outputRoot = resolve(repositoryRoot, "conversions");
const allGames = [
  {
    id: "voxel-frontier",
    name: "Voxel Frontier",
    url: "https://github.com/Sunwood-ai-labs/threejs-voxel-frontier",
    license: "ISC",
    port: 4311,
    prepare: async (page) => page.waitForTimeout(1000),
  },
  {
    id: "little-cubes",
    name: "LittleCubes",
    url: "https://github.com/paugm/LittleCubes",
    license: "MIT",
    port: 4312,
    prepare: async (page) => {
      await page.click("#start-button");
      await page.waitForTimeout(2500);
    },
  },
  {
    id: "warptracker",
    name: "Warptracker",
    url: "https://github.com/ilrein/warptracker",
    license: "MIT",
    port: 4313,
    prepare: async (page) => {
      await page.waitForSelector('[data-class="sentinel"]', { timeout: 30_000 });
      await page.click('[data-class="sentinel"]');
      await page.click('#wt-class-confirm');
      await page.waitForFunction(() => Boolean(window.__WT), null, { timeout: 30_000 });
      await page.waitForTimeout(1500);
    },
  },
];
const requestedIds = new Set(process.argv.slice(2));
const games = requestedIds.size > 0 ? allGames.filter((game) => requestedIds.has(game.id)) : allGames;

await mkdir(outputRoot, { recursive: true });
const previousResults = await readPreviousResults();
const servers = games.map(startServer);
const results = [];
let browser;

try {
  await Promise.all(games.map((game) => waitForServer(game.port)));
  browser = await chromium.launch({
    headless: true,
    executablePath: process.platform === "win32" ? "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe" : undefined,
  });
  for (const game of games) {
    const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
    const consoleErrors = [];
    page.on("console", (message) => {
      if (message.type() === "error") consoleErrors.push(message.text());
    });
    try {
      await page.goto(`http://127.0.0.1:${game.port}`, { waitUntil: "networkidle", timeout: 30_000 });
      await game.prepare(page);
      await page.waitForFunction(() => typeof window.__exportThreeUnity === "function", null, { timeout: 30_000 });
      const json = await page.evaluate(() => window.__exportThreeUnity());
      const document = JSON.parse(json);
      const output = resolve(outputRoot, `${game.id}.threeunity`);
      const formatted = `${JSON.stringify(document, null, 2)}\n`;
      await writeFile(output, formatted, "utf8");
      results.push({
        id: game.id,
        name: game.name,
        source: game.url,
        commit: readCommit(game.id),
        license: game.license,
        output: `conversions/${game.id}.threeunity`,
        bytes: Buffer.byteLength(formatted),
        nodes: document.nodes.length,
        meshes: document.meshes.length,
        materials: document.materials.length,
        textures: document.textures.length,
        warnings: document.warnings,
        browserConsoleErrors: consoleErrors,
      });
      console.log(`${game.name}: ${document.nodes.length} nodes, ${document.meshes.length} meshes, ${Buffer.byteLength(json)} bytes`);
    } catch (error) {
      console.error(`${game.name} capture failed: ${error.message}`);
      for (const message of consoleErrors) console.error(`[${game.id} console] ${message}`);
      results.push({ id: game.id, name: game.name, source: game.url, commit: readCommit(game.id), license: game.license, error: error.message, browserConsoleErrors: consoleErrors });
    }
    await page.close();
  }
  const merged = new Map(previousResults.map((result) => [result.id, result]));
  for (const result of results) merged.set(result.id, result);
  const orderedResults = allGames.map((game) => merged.get(game.id)).filter(Boolean);
  await writeFile(resolve(outputRoot, "report.json"), `${JSON.stringify({ generatedAt: new Date().toISOString(), results: orderedResults }, null, 2)}\n`, "utf8");
} finally {
  if (browser) await browser.close();
  for (const server of servers) server.kill();
}

async function readPreviousResults() {
  try {
    const report = JSON.parse(await readFile(resolve(outputRoot, "report.json"), "utf8"));
    return Array.isArray(report.results) ? report.results : [];
  } catch {
    return [];
  }
}

function readCommit(id) {
  return execFileSync("git", ["rev-parse", "HEAD"], {
    cwd: resolve(repositoryRoot, "conversion-work", id),
    encoding: "utf8",
    windowsHide: true,
  }).trim();
}

function startServer(game) {
  const vite = resolve(repositoryRoot, "conversion-work", game.id, "node_modules", "vite", "bin", "vite.js");
  const child = spawn(process.execPath, [vite, "preview", "--host", "127.0.0.1", "--port", String(game.port)], {
    cwd: resolve(repositoryRoot, "conversion-work", game.id),
    windowsHide: true,
    stdio: ["ignore", "pipe", "pipe"],
  });
  child.stderr.on("data", (chunk) => process.stderr.write(`[${game.id}] ${chunk}`));
  return child;
}

async function waitForServer(port) {
  const url = `http://127.0.0.1:${port}`;
  for (let attempt = 0; attempt < 100; attempt += 1) {
    try {
      const response = await fetch(url);
      if (response.ok) return;
    } catch {
      // Server is still starting.
    }
    await new Promise((resolvePromise) => setTimeout(resolvePromise, 100));
  }
  throw new Error(`Preview server did not start: ${url}`);
}
