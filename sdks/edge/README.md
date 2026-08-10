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

## Develop

```bash
pnpm install --ignore-workspace
pnpm build   # needs ../js (the @condux/node core) built first
pnpm test
```

Zero runtime dependencies beyond the `@condux/node` core.
