/**
 * Reading the runtime dependency inventory (ADR-0041) on the Node runtime, and only there.
 *
 * `register` is one function that Next runs on BOTH the nodejs and edge runtimes, and reading the
 * installed tree needs a filesystem. This file exists so that fact has somewhere to be explained, and
 * so index.ts stays about wiring Next's hooks rather than about bundler behaviour.
 */

import { setModules } from "@condux/core";

/**
 * Collect and declare the installed packages, on the Node runtime only.
 *
 * <p>Next replaces `process.env.NEXT_RUNTIME` with a string literal while bundling each runtime, so the
 * edge build compiles the guard below to `"edge" !== "nodejs"` and drops the branch, taking the import
 * of `@condux/node` and its `node:fs` with it. Measured against a real Turbopack build: of the edge
 * chunks produced for an app with an edge route, none carries `node:fs` or the collector, while the
 * node side does. The import is dynamic for the same reason, since a static one would be a hard edge in
 * the module graph whatever the branch does.</p>
 *
 * <p>Not awaited, so instrumentation stays synchronous and startup is not held up by a directory walk.
 * The inventory attaches to events captured after it lands, which is every event in practice. A failure
 * is warned about rather than swallowed: an application that expected the inventory should be told it
 * is missing rather than quietly reporting nothing.</p>
 */
export function collectServerModules(): void {
  if (process.env.NEXT_RUNTIME !== "nodejs") {
    return;
  }

  void import("@condux/node")
    .then((node) => setModules(node.collectModules()))
    .catch((error: unknown) => {
      console.warn("Condux: could not read the dependency inventory for this release.", error);
    });
}
