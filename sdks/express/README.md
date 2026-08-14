# @condux/express

Condux SDK for Express. Reports errors to a Condux relay via an error-handling middleware, built on the
isomorphic [`@condux/node`](../js) core (same Sentry "store" wire shape + resilient, never-throwing
transport).

## Usage

```ts
import express from "express";
import { init, conduxErrorHandler } from "@condux/express";

init({
  dsn: "https://<key>@ingest.condux.ai/<projectId>",
  environment: "production",
  release: "api@1.4.2",
});

const app = express();

app.get("/", (_req, _res) => {
  throw new Error("boom");
});

// Register last, after your routes: it reports anything that reaches Express's error pipeline
// (as unhandled) and passes it on so your own error handling still runs.
app.use(conduxErrorHandler());
```

Capture manually anywhere with the re-exported `captureException` / `captureMessage`.

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

Zero runtime dependencies beyond the `@condux/node` core (Express is a peer, not bundled).
