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
