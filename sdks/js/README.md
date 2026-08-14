# @condux/node

Report errors from a Node.js app to a Condux relay. A thin re-export of
[`@condux/core`](https://www.npmjs.com/package/@condux/core) under the Node package name: emits the
Sentry "store" wire shape, so the relay normalizes it exactly like an official Sentry SDK — point it at a
project DSN and it works.

Delivery is resilient (429 / 5xx / network failures retry with backoff, honoring `Retry-After`) and
**never throws** — a failed send resolves to a `SendResult`, it does not crash the host app.

## Install

```bash
npm install @condux/node
# or: pnpm add @condux/node
```

## Usage

```ts
import { init, captureException, captureMessage, Level } from "@condux/node";

init({
  dsn: "https://<key>@ingest.condux.ai/<projectId>",
  environment: "production",
  release: "app@1.0.0",
});

try {
  doWork();
} catch (error) {
  captureException(error);
  throw error;
}

// or a bare message
captureMessage("cache miss storm", Level.Warning);
```

For Express, add [`@condux/express`](https://www.npmjs.com/package/@condux/express). See the
[Condux SDK docs](https://github.com/tripledownab/condux/tree/main/sdks/js#readme).

## Verify it works

An error monitor's failure mode is silence, and silence looks like health. Prove the pipeline before
waiting for a real error:

```bash
CONDUX_DSN=https://<key>@ingest.condux.ai/<projectId> npx condux test-event
```

Delivers one info-level test message through the real client and transport and prints the outcome
(nonzero exit on failure, so it can gate CI).

## Users, tags, contexts and breadcrumbs

```js
import { addBreadcrumb, setContext, setTag, setUser } from "@condux/node";

setUser({ id: "u-1", email: "person@example.com" }); // setUser(null) on sign-out
setTag("plan", "business");
setContext("subscription", { seats: 12 });
addBreadcrumb({ message: "job started", category: "worker" });
```

Everything set here rides every subsequent event; the relay scrubs it at ingest and derives the
pseudonymous users-affected count from the user fields.
