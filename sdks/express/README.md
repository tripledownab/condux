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

// Register last, after your routes: it reports what reaches Express's error pipeline (as unhandled)
// and passes it on so your own error handling still runs.
app.use(conduxErrorHandler());
```

**Caller-caused failures are passed on but not reported.** An error carrying a 4xx on `status` or
`statusCode` describes what the client sent, not a defect in your application: `express.json()` raises
one for a malformed body and another for a body over its limit, and anyone can send those at will, so
filing them would let a stranger bury your real errors. That is the `http-errors` convention Express
already uses to choose the response status, so throwing `createError(400, ...)` yourself is also how you
mark your own errors as the caller's. Anything without a 4xx status, including a 5xx, is still reported.

Capture manually anywhere with the re-exported `captureException` / `captureMessage`.

Reported events carry the request they happened during: the URL, the method and the query string. The
URL comes from `originalUrl`, so a router mounted with `app.use("/api", router)` still reports the
endpoint the client actually called rather than the path within the router. Headers are on the request
and deliberately not sent, since they carry cookies and authorization and not sending credentials is a
stronger guarantee than scrubbing them later.

Note that the enrichment scope (`setUser`, `setTag`) is **process wide**. Node serves requests
concurrently, so a `setUser` in a route handler can attach that user to a different request's error.
Pass anything request-specific at the capture call instead:

```ts
captureException(error, true, { request: { url: req.originalUrl }, tags: { route: "/checkout/:id" } });
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

Zero runtime dependencies beyond the `@condux/node` core (Express is a peer, not bundled).
