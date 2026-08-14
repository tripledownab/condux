import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { withConduxConfig } from "../src/config.ts";

test("wraps the config: source maps on, everything else preserved", () => {
  const config = withConduxConfig({ reactStrictMode: true });

  assert.equal(config.productionBrowserSourceMaps, true);
  assert.equal(config.reactStrictMode, true);
  assert.equal(typeof config.compiler?.runAfterProductionCompile, "function");
});

test("the post-compile hook stamps the client build output", async () => {
  // Stamp-only: without upload credentials in the environment the hook must not reach for the network.
  delete process.env.CONDUX_RELEASE_TOKEN;
  const projectDir = await mkdtemp(join(tmpdir(), "condux-project-"));
  await mkdir(join(projectDir, ".next/static/chunks"), { recursive: true });
  const jsPath = join(projectDir, ".next/static/chunks/app.js");
  await writeFile(jsPath, "app();\n//# sourceMappingURL=app.js.map\n");
  await writeFile(`${jsPath}.map`, JSON.stringify({ version: 3, sources: [], mappings: "" }));
  const config = withConduxConfig();

  // Next passes distDir relative to the project (the default ".next"); the hook resolves it.
  await config.compiler?.runAfterProductionCompile?.({ projectDir, distDir: ".next" });

  const stamped = await readFile(jsPath, "utf8");
  assert.ok(stamped.includes("_conduxDebugIds"));
  // Stamp-only run: the map stayed, so the chunk keeps pointing at it (the CLI pairs by that pointer).
  assert.ok(stamped.endsWith("//# sourceMappingURL=app.js.map\n"));
});

test("a user's own post-compile hook still runs, before ours", async () => {
  const order: string[] = [];
  const projectDir = await mkdtemp(join(tmpdir(), "condux-empty-"));
  const config = withConduxConfig({
    compiler: {
      async runAfterProductionCompile() {
        order.push("user");
      },
    },
  });

  await config.compiler?.runAfterProductionCompile?.({ projectDir, distDir: ".next" });

  assert.deepEqual(order, ["user"]);
});

test("disable returns the config untouched", () => {
  const original = { reactStrictMode: true };

  assert.equal(withConduxConfig(original, { disable: true }), original);
});
