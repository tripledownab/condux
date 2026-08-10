#!/usr/bin/env node
// Prepare the @condux/* npm packages for publishing: stamp the release version onto each and rewrite its
// internal "@condux/*": "link:../x" dependency to the concrete version being published. pnpm/npm rewrite
// "workspace:" on publish but NOT "link:", and these SDKs are not workspace members (each has its own
// lockfile and links its siblings by path for local dev), so we pin them here instead. Run in CI on the
// ephemeral checkout right before publishing; no restore is needed. Usage: node set-version.mjs <semver>
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const version = process.argv[2];
if (!version || !/^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/.test(version)) {
  console.error(`usage: set-version.mjs <semver>  (got: ${version ?? "nothing"})`);
  process.exit(1);
}

const sdksDir = join(dirname(fileURLToPath(import.meta.url)), "..");
// Every publishable @condux/* package, in dependency order (a package follows each sibling it links).
const packages = ["core", "browser", "edge", "js", "nextjs", "react", "express"];
const depFields = ["dependencies", "peerDependencies", "optionalDependencies"];

for (const pkg of packages) {
  const path = join(sdksDir, pkg, "package.json");
  const manifest = JSON.parse(readFileSync(path, "utf8"));
  manifest.version = version;
  for (const field of depFields) {
    const deps = manifest[field];
    if (!deps) continue;
    for (const [name, range] of Object.entries(deps)) {
      if (name.startsWith("@condux/") && String(range).startsWith("link:")) {
        deps[name] = version;
      }
    }
  }
  writeFileSync(path, `${JSON.stringify(manifest, null, 2)}\n`);
  console.log(`set ${manifest.name}@${version}`);
}
