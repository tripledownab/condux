# @condux/edge

Condux SDK for edge runtimes — **Cloudflare Workers, Vercel Edge Functions, Deno Deploy**. Reports errors
to a Condux relay in the Sentry "store" wire shape. The [`@condux/node`](../js) core is already isomorphic
(it uses only `fetch` / `crypto` / `URL`, no Node built-ins), so it runs unchanged on the edge; this package
re-exports it and adds `wrapFetch` for boundary auto-capture.

## Usage

```ts
import { init, wrapFetch } from "@condux/edge";

init({
  dsn: "https://<key>@ingest.condux.ai/<projectId>",
  environment: "production",
  release: "worker@1.4.2",
});

// Wrap the fetch handler: anything it throws is reported (as unhandled) and rethrown.
export default {
  fetch: wrapFetch(async (request: Request) => {
    return handle(request);
  }),
};
```

Edge runtimes have no `window`, so there is no global auto-capture — wrap the handler (or call
`captureException` manually).

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

Zero runtime dependencies beyond the `@condux/node` core.
