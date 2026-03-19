import { mkdir, rm, cp } from "node:fs/promises";
import { resolve } from "node:path";

const dist = resolve("dist");
const wwwroot = resolve("..", "wwwroot");

await rm(resolve(wwwroot, "assets"), { recursive: true, force: true });
await mkdir(resolve(wwwroot, "assets"), { recursive: true });

await cp(dist, wwwroot, { recursive: true });
console.log("Copied UI build output to API wwwroot/");
