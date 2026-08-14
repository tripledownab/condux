#!/usr/bin/env node
/**
 * `npx condux test-event` — prove the pipeline end to end, because an error monitor's failure mode is
 * silence and silence looks exactly like health. Sends one info-level message through the real client
 * and transport and prints the delivery outcome, so "did my DSN/network/relay work" is one command
 * instead of waiting for a production error.
 *
 *   CONDUX_DSN=https://key@ingest.condux.ai/project npx condux test-event
 *   npx condux test-event --dsn https://key@ingest.condux.ai/project --message "hello"
 */

import { captureMessage, init, Level } from "./index.ts";

function argValue(name: string): string | undefined {
  const index = process.argv.indexOf(name);
  return index >= 0 ? process.argv[index + 1] : undefined;
}

async function testEvent(): Promise<number> {
  const dsn = argValue("--dsn") ?? process.env.CONDUX_DSN;
  if (!dsn) {
    console.error("condux test-event: no DSN. Pass --dsn <dsn> or set CONDUX_DSN.");
    return 2;
  }

  init({ dsn, environment: "condux-test" });
  const message = argValue("--message") ?? "Condux test event";
  const result = await captureMessage(message, Level.Info);
  if (result.ok) {
    console.log(
      `Delivered "${message}" (${result.attempts} attempt${result.attempts === 1 ? "" : "s"}). `
        + "Check your project's issues list; a test message appears as an info-level issue.",
    );
    return 0;
  }
  console.error(
    `Delivery FAILED after ${result.attempts} attempt(s): `
      + `${result.error ?? `relay answered ${result.status}`}. `
      + "Check the DSN (Project settings -> DSN keys) and that the ingest host is reachable.",
  );
  return 1;
}

const command = process.argv[2];
if (command === "test-event") {
  testEvent().then((code) => process.exit(code));
} else {
  console.error(`condux: unknown command '${command ?? ""}'. Usage: condux test-event [--dsn <dsn>] [--message <text>]`);
  process.exit(2);
}
