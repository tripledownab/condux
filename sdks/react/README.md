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
pnpm build   # needs ../js and ../browser built first
pnpm test
```

`react` is a peer dependency (not bundled). The boundary is written with `createElement` (no JSX) so the
tests run the `.ts` source directly under Node — no build step or JSX transform needed.
