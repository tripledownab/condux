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

// Pass the DSN explicitly: Next inlines NEXT_PUBLIC_* only where YOUR code references the variable,
// so a bare initClient() cannot see it from inside this package and client reporting stays off
// (initClient warns when that happens).
initClient({ dsn: process.env.NEXT_PUBLIC_CONDUX_DSN });
```

Configure with env vars (both inert until set, so installing ahead of configuring never breaks a build):

```dotenv
CONDUX_DSN=https://<key>@ingest.condux.ai/<projectId>              # server + edge (kept server-only)
NEXT_PUBLIC_CONDUX_DSN=https://<key>@ingest.condux.ai/<projectId>  # client (exposed to the browser)
```

React **render** errors never reach the global handlers — they go to error boundaries. Wrap your app
(or any subtree) in the re-exported boundary so they are reported too:

```tsx
import { ConduxErrorBoundary } from "@condux/nextjs/react";

<ConduxErrorBoundary fallback={<p>Something went wrong.</p>}>{children}</ConduxErrorBoundary>
```

Attach who and what to every event, and the trail leading up to it:

```ts
import { addBreadcrumb, setContext, setTag, setUser } from "@condux/nextjs";

setUser({ id: user.id, email: user.email }); // null on sign-out
setTag("plan", org.plan);
setContext("subscription", { seats: 12 });
addBreadcrumb({ message: "opened checkout", category: "navigation" });
```

Verify the pipeline end to end before waiting for a real error:

```bash
CONDUX_DSN=https://<key>@ingest.condux.ai/<projectId> npx condux test-event
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
