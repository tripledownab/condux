# @condux/core

The isomorphic engine at the heart of every JavaScript Condux SDK. It emits the Sentry "store" wire
shape and delivers it over a resilient, never-throwing transport, using only `fetch` / `crypto` / `URL`
so it runs unchanged in Node, browsers and edge runtimes.

You usually do not install this directly. Pick the SDK for your runtime, which re-exports this core:

- **`@condux/node`** — Node services.
- **`@condux/browser`** — browsers, with automatic global error capture.
- **`@condux/edge`** — Cloudflare Workers / Vercel Edge / Deno, with a `wrapFetch` boundary.
- **`@condux/nextjs`** — the Next.js App Router adapter over the three above.

## API

```ts
import { init, captureException, captureMessage, Level } from "@condux/core";

init({ dsn: "https://<publicKey>@relay.condux.ai/<projectId>" });

captureException(new Error("boom"));
captureMessage("checkout latency degraded", Level.Warning);
```

Both `capture*` calls return a `Promise<SendResult>` and never throw; a failed delivery reports
`{ ok: false }` rather than crashing the host app.

## Layout

Split by responsibility to keep files small: `types` (options + `Level` + `SendResult`), `dsn`,
`transport` (retry/backoff), `stack` (frame parsing + `normalizeFramePath`), `client` (the capture
entry points). Built with plain `tsc`; tested on the `.ts` source under Node's type-stripping.
