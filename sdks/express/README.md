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

## Develop

```bash
pnpm install --ignore-workspace
pnpm build   # needs ../js (the @condux/node core) built first
pnpm test
```

Zero runtime dependencies beyond the `@condux/node` core (Express is a peer, not bundled).
