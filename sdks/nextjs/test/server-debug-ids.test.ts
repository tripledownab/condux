import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, realpath, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { type FetchLike, captureRequestError, register } from "../src/index.ts";
import { processServerBuild } from "../src/server-build.ts";

// Its own file because register() initializes once per process.
test("an error thrown in a stamped server chunk reports the debugId of that chunk's map", async () => {
  // Real path: Node reports the resolved file, and macOS's temp directory is behind a symlink.
  const distDir = await realpath(await mkdtemp(join(tmpdir(), "condux-server-runtime-")));
  await mkdir(join(distDir, "server/chunks"), { recursive: true });
  const chunkPath = join(distDir, "server/chunks/boom.js");
  await writeFile(
    chunkPath,
    'module.exports = function boom() { throw new TypeError("boom"); };\n//# sourceMappingURL=boom.js.map\n',
  );
  await writeFile(`${chunkPath}.map`, JSON.stringify({ version: 3, sources: ["../x.ts"], mappings: "AAAA" }));
  await processServerBuild({ distDir, log: () => {} });
  const debugId = JSON.parse(await readFile(`${chunkPath}.map`, "utf8")).debugId;

  const sent: string[] = [];
  const fetch: FetchLike = async (_url, init) => {
    sent.push(init.body);
    return { status: 202, headers: { get: () => null } };
  };
  register({ dsn: "http://pub123@relay.test/7", fetch });
  // Loaded the way Turbopack's server runtime loads its chunks, so the snippet runs as it would in Next.
  const boom = createRequire(import.meta.url)(chunkPath) as () => void;
  let thrown: unknown;
  try {
    boom();
  } catch (error) {
    thrown = error;
  }
  await captureRequestError(thrown);

  const event = JSON.parse(sent[sent.length - 1]);
  const frames = event.exception.values[0].stacktrace.frames as Array<{ abs_path: string }>;
  const crash = frames[frames.length - 1];
  assert.equal(crash.abs_path, chunkPath);
  assert.deepEqual(event.debug_meta.images, [{ type: "sourcemap", code_file: chunkPath, debug_id: debugId }]);
});
