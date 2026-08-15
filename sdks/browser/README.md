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

Every event carries the page it happened on: the URL is read from `location.href` at capture time and
sent as `request.url`, so an issue says which page produced it without any wiring. Override it, or add
tags for one event only, with the third argument:

```ts
captureException(error, true, { request: { url: "/checkout" }, tags: { step: "payment" } });
```

## Verify your setup

Silence is what a broken error monitor and a healthy app look like from the outside, so prove the
pipeline once. The command ships with `@condux/node`, so run it without adding a dependency:

```bash
CONDUX_DSN="https://<key>@ingest.condux.ai/<projectId>" npx --package=@condux/node condux test-event
```

Exit code 0 means delivered (the message appears as an info-level issue), 1 means delivery failed and
prints why, 2 means the DSN was missing.

## Enrichment

`setUser` / `setTag` / `setContext` / `addBreadcrumb` are re-exported here; whatever you set rides every
subsequent event. See the [`@condux/node` README](../js/README.md#users-tags-contexts-and-breadcrumbs).

## Develop

```bash
pnpm install --ignore-workspace
pnpm build   # needs ../js (the @condux/node core) built first
pnpm test
```

Zero runtime dependencies beyond the `@condux/node` core. The global handlers are unit-tested with a fake
event target, so no real DOM is needed.
