/**
 * Enumerating what is actually installed alongside a Node process, so an event can carry the loaded
 * package versions (ADR-0041). This lives here rather than in @condux/core because it needs a
 * filesystem and the core is deliberately fetch/crypto/URL only.
 *
 * It reads the deployed tree rather than a manifest, which is the entire point: a manifest states
 * ranges, and what answers "am I running the vulnerable version" is what resolution actually put on
 * disk. It reads each package.json rather than parsing directory names, because a name and a version
 * parsed out of a path is a guess, and a guessed version reported as observed is worse than nothing.
 */

import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";

/**
 * A ceiling on how many package.json files one collection will read, so a pathological tree cannot
 * turn startup into a filesystem walk of unbounded length. Well above a large real application, and
 * generous on purpose: the core sorts and caps what actually ships, so this bounds the WORK while
 * that bounds the PAYLOAD.
 */
export const MAX_SCANNED = 5000;

export interface CollectModulesOptions {
  /** Where to start looking for node_modules. Defaults to the process's working directory. */
  cwd?: string;
  /** Override the scan ceiling; only tests should need this. */
  maxScanned?: number;
}

/**
 * The installed packages as a name to version map, empty when there is nothing to read.
 *
 * <p>Never throws. This runs during init in an application that has not asked for a filesystem walk,
 * so an unreadable directory, a permission error or a malformed package.json costs that one entry and
 * nothing else.</p>
 */
export function collectModules(options: CollectModulesOptions = {}): Record<string, string> {
  const found: Record<string, string> = {};
  const budget = { left: options.maxScanned ?? MAX_SCANNED };

  // EVERY node_modules from here to the filesystem root, nearest first, not just the nearest one.
  //
  // Stopping at the first was wrong, and production is where that showed: in a pnpm WORKSPACE the
  // store lives at the workspace root while each package gets its own node_modules holding only its
  // direct dependencies. The dashboard reported 38 packages when the store one level up held 922, so
  // every transitive dependency was missing, which is exactly where advisories live. npm and yarn can
  // nest node_modules for conflicting versions, so walking up is right there too.
  //
  // Nearest first matters: `found[name] ??= version` keeps the first writer, and the nearest copy is
  // the one Node would resolve.
  for (const root of nodeModulesDirectories(options.cwd ?? process.cwd())) {
    readPackages(root, found, budget);

    // pnpm keeps only DIRECT dependencies at a node_modules top level and the rest under .pnpm, each
    // entry holding the real package under its own node_modules.
    for (const entry of list(join(root, ".pnpm"))) {
      readPackages(join(root, ".pnpm", entry, "node_modules"), found, budget);
    }
  }

  return found;
}

/** Every node_modules at or above `start`, nearest first. */
function nodeModulesDirectories(start: string): string[] {
  const roots: string[] = [];
  let directory = start;
  for (;;) {
    const candidate = join(directory, "node_modules");
    if (list(candidate).length > 0) {
      roots.push(candidate);
    }
    const parent = dirname(directory);
    if (parent === directory) {
      return roots;
    }
    directory = parent;
  }
}

/**
 * Read every package directly under one node_modules into `found`. Scoped directories hold the real
 * packages one level down. A name already recorded is kept rather than overwritten, so the shallowest
 * copy wins, which is the one Node would resolve.
 */
function readPackages(directory: string, found: Record<string, string>, budget: { left: number }): void {
  for (const entry of list(directory)) {
    if (budget.left <= 0) {
      return;
    }
    if (entry.startsWith("@")) {
      for (const scoped of list(join(directory, entry))) {
        record(join(directory, entry, scoped), found, budget);
      }
    } else if (!entry.startsWith(".")) {
      record(join(directory, entry), found, budget);
    }
  }
}

function record(packageDirectory: string, found: Record<string, string>, budget: { left: number }): void {
  if (budget.left <= 0) {
    return;
  }
  budget.left -= 1;

  try {
    const manifest = JSON.parse(readFileSync(join(packageDirectory, "package.json"), "utf8"));
    const { name, version } = manifest;
    if (typeof name === "string" && typeof version === "string" && name.length > 0 && version.length > 0) {
      found[name] ??= version;
    }
  } catch {
    // A directory without a readable manifest is not a package. Nothing to report and nothing to fix.
  }
}

/** Directory entries, or none when the path is missing or unreadable. */
function list(directory: string): string[] {
  try {
    return readdirSync(directory);
  } catch {
    return [];
  }
}
