import { mkdir, rm, cp } from "node:fs/promises";
import { resolve } from "node:path";
import { watch } from "node:fs";

const dist = resolve("dist");
const wwwroot = resolve("..", "wwwroot");

async function copyToWwwroot() {
  try {
    await rm(resolve(wwwroot, "assets"), { recursive: true, force: true });
    await mkdir(resolve(wwwroot, "assets"), { recursive: true });
    await cp(dist, wwwroot, { recursive: true });
    console.log(`✅ [${new Date().toLocaleTimeString()}] Copied UI build output to API wwwroot/`);
  } catch (err) {
    console.error(`❌ Error copying to wwwroot:`, err);
  }
}

// Copie initiale
await copyToWwwroot();

// Watch mode : surveiller le répertoire dist
const watcher = watch(dist, { recursive: true }, async (eventType, filename) => {
  if (filename) {
    console.log(`📝 Change detected: ${filename}`);
    await copyToWwwroot();
  }
});

console.log("👀 Watching dist/ for changes...");
console.log("   Press Ctrl+C to stop");

// Gérer l'arrêt proprement
process.on("SIGINT", () => {
  console.log("\n🛑 Stopping watch mode...");
  watcher.close();
  process.exit(0);
});

