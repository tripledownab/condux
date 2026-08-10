# @condux/browser

Condux SDK for browsers. Reports errors to a Condux relay in the Sentry "store" wire shape (the relay
normalizes it like an official Sentry SDK), and **automatically captures uncaught errors and unhandled
promise rejections** via the global `error` / `unhandledrejection` events. Built on the isomorphic
[`@condux/node`](../js) core, so delivery is the same resilient, never-throwing transport.

## Usage

```ts
import { init, captureException, Level } from "@condux/browser";

init({
  dsn: "https://<key>@ingest.condux.ai/<projectId>",
  environment: "production",
  release: "app@1.4.2",
});

// Uncaught errors + unhandled rejections are now reported automatically (as unhandled).
// Capture something manually:
try {
  risky();
} catch (error) {
  captureException(error);
}
```

Pass `captureGlobalErrors: false` to `init` to opt out of the automatic handlers and report only manually.

## Develop

```bash
pnpm install --ignore-workspace
pnpm build   # needs ../js (the @condux/node core) built first
pnpm test
```

Zero runtime dependencies beyond the `@condux/node` core. The global handlers are unit-tested with a fake
event target, so no real DOM is needed.
