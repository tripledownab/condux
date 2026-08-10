# @condux/react

Condux SDK for React. Reports errors to a Condux relay, built on [`@condux/browser`](../browser) (init +
automatic capture of uncaught errors and unhandled rejections) and adds a **`ConduxErrorBoundary`** for
**render errors** — React swallows those into the nearest error boundary, so `window.onerror` never sees
them.

## Usage

```tsx
import { init, ConduxErrorBoundary } from "@condux/react";

init({
  dsn: "https://<key>@ingest.condux.ai/<projectId>",
  environment: "production",
  release: "app@1.4.2",
});

export function Root() {
  return (
    <ConduxErrorBoundary fallback={<SomethingWentWrong />}>
      <App />
    </ConduxErrorBoundary>
  );
}
```

`init` also installs the global handlers (from `@condux/browser`); the boundary additionally catches React
render errors and reports them as unhandled. Capture manually anywhere with the re-exported
`captureException` / `captureMessage`.

## Develop

```bash
pnpm install --ignore-workspace
pnpm build   # needs ../js and ../browser built first
pnpm test
```

`react` is a peer dependency (not bundled). The boundary is written with `createElement` (no JSX) so the
tests run the `.ts` source directly under Node — no build step or JSX transform needed.
