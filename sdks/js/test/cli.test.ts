import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { createServer } from "node:http";
import { test } from "node:test";

// The CLI is a real process contract (exit codes are what CI scripts branch on), so run it as one.
function runCli(args: string[], env: Record<string, string> = {}) {
  return spawnSync(
    process.execPath,
    ["--experimental-strip-types", "src/cli.ts", ...args],
    { encoding: "utf8", env: { ...process.env, CONDUX_DSN: "", ...env } },
  );
}

test("test-event without a DSN explains itself and exits 2", () => {
  const result = runCli(["test-event"]);
  assert.equal(result.status, 2);
  assert.match(result.stderr, /no DSN/);
});

test("test-event against an unreachable relay reports the failure and exits 1", () => {
  // 127.0.0.1:9 (discard) refuses immediately; the transport retries then gives up. Proves the
  // command surfaces delivery failure as a nonzero exit instead of pretending health.
  const result = runCli(["test-event", "--dsn", "http://key@127.0.0.1:9/1"]);
  assert.equal(result.status, 1);
  assert.match(result.stderr, /Delivery FAILED/);
});

test("test-event against a live relay delivers and exits 0", async () => {
  // A minimal relay stub accepting the store POST — the full success path, process contract included.
  const server = createServer((request, response) => {
    assert.match(request.url ?? "", /\/api\/1\/store\//);
    response.writeHead(200, { "content-type": "application/json" });
    response.end("{}");
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  const port = typeof address === "object" && address !== null ? address.port : 0;

  try {
    const child = spawn(
      process.execPath,
      ["--experimental-strip-types", "src/cli.ts", "test-event", "--dsn", `http://key@127.0.0.1:${port}/1`],
      { env: { ...process.env, CONDUX_DSN: "" } },
    );
    let stdout = "";
    child.stdout.on("data", (chunk) => {
      stdout += chunk;
    });
    const code = await new Promise<number | null>((resolve) => child.on("close", resolve));

    assert.equal(code, 0);
    assert.match(stdout, /Delivered/);
  } finally {
    server.close();
  }
});

test("an unknown command exits 2 with usage", () => {
  const result = runCli(["frobnicate"]);
  assert.equal(result.status, 2);
  assert.match(result.stderr, /Usage: condux test-event/);
});
