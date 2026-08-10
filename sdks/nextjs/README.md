# @condux/nextjs

Condux SDK for Next.js (App Router). Reports errors from all three Next runtimes — **server, edge, and
client** — to a Condux relay, with a few lines and no config beyond a DSN. Thin glue over the SDKs it
builds on: [`@condux/node`](../js) (server + edge; the core is isomorphic, so one init serves both the
`nodejs` and `edge` runtimes) and [`@condux/browser`](../browser) (client, with global error handlers).

## Setup

Server + edge — capture every uncaught server error:

```ts
// instrumentation.ts
export { register } from "@condux/nextjs";
export { captureRequestError as onRequestError } from "@condux/nextjs";
```

Client — capture uncaught errors + unhandled rejections in the browser:

```ts
// instrumentation-client.ts
import { initClient } from "@condux/nextjs";

initClient();
```

Configure with env vars (both inert until set, so installing ahead of configuring never breaks a build):

```dotenv
CONDUX_DSN=https://<key>@ingest.condux.ai/<projectId>              # server + edge (kept server-only)
NEXT_PUBLIC_CONDUX_DSN=https://<key>@ingest.condux.ai/<projectId>  # client (exposed to the browser)
```

Server errors reach `onRequestError` and are reported **unhandled**; `captureRequestError` is a safe no-op
until `register` has run. Capture manually anywhere with the re-exported `captureException` /
`captureMessage`.

## Develop

```bash
pnpm install --ignore-workspace
pnpm build   # needs ../js and ../browser built first
pnpm test
```

Zero runtime dependencies beyond the `@condux/node` + `@condux/browser` packages it composes. This SDK is
dogfooded by the Condux dashboard itself (`web/instrumentation.ts`).
