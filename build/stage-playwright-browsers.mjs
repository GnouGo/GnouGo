import { execFileSync } from "node:child_process";
import { constants, cpSync, existsSync, mkdirSync, mkdtempSync, renameSync, rmSync } from "node:fs";
import { basename, isAbsolute, join } from "node:path";

// Ask the pinned installer for the assets selected on this machine. It also
// checks its usual installation markers in the published directory afterwards.
const [cli, destination] = process.argv.slice(2);
const manifest = execFileSync(process.execPath, [cli, "install", "--dry-run", "chromium"], { encoding: "utf8" });
mkdirSync(destination, { recursive: true });

for (const line of manifest.split(/\r?\n/)) {
    const match = /^\s*Install location:\s+(.+)$/.exec(line);
    if (!match) continue;
    const source = match[1].trim();
    if (!isAbsolute(source) || !existsSync(join(source, "INSTALLATION_COMPLETE"))) continue;
    const target = join(destination, basename(source));
    if (existsSync(target)) continue;

    const staging = mkdtempSync(join(destination, ".stage-"));
    try {
        const copy = join(staging, "browser");
        cpSync(source, copy, { recursive: true, verbatimSymlinks: true, mode: constants.COPYFILE_FICLONE });
        renameSync(copy, target);
        console.log(`Staged Playwright's installed ${basename(target)} into the published package.`);
    } catch (error) {
        if (!existsSync(target))
            console.log(`Playwright will install ${basename(target)} in the published package (${error.code}).`);
    } finally {
        rmSync(staging, { recursive: true, force: true });
    }
}
