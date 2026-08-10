# Condux examples

Small, realistic apps that report errors to Condux through the first-party SDKs. They exist to
generate lifelike events (grouped exceptions with real stack traces, plus a few message events) so
you can see the ingest → group → dashboard pipeline end to end.

Both read the project DSN from `CONDUX_DSN` (no secrets are baked in). Get one from the dashboard's
project settings, or provision a project with `./scripts/smoke.sh` and copy the DSN it prints. The
relay listens on `localhost:9010` in the local compose stack, so a DSN looks like
`http://<key>@localhost:9010/<projectId>`.

Run the full stack first (`docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d`),
then a sample app, then refresh the Issues page at http://localhost:3000.

## Node — checkout service (`node-checkout/`)

Uses `@condux/node`. Processes a batch of orders; a declining card and an out-of-stock SKU each
recur (so they group), and one order hits a null-dereference bug.

```bash
cd examples/node-checkout
CONDUX_DSN="http://<key>@localhost:9010/<projectId>" ./run.sh
```

`run.sh` installs `@condux/node` on first run (`pnpm install --ignore-workspace`, which keeps this
standalone app out of the repo's pnpm workspace), then runs the app. Or do it by hand:
`pnpm install --ignore-workspace && node checkout.mjs`.

## Python — background worker (`python-worker/`)

Uses `condux`. Drains a batch of jobs; a missing secret and an unreachable database each recur, and
one job divides by zero.

```bash
cd examples/python-worker
CONDUX_DSN="http://<key>@localhost:9010/<projectId>" ./run.sh
```

`run.sh` creates a local `.venv` and installs the `condux` SDK on first run, then runs the worker
(`condux` isn't published — it lives in `../../sdks/python` — and Homebrew Python refuses a bare
`pip install` under PEP 668, so the venv is the clean route). To run it in your own interpreter
without a venv: `PYTHONPATH=../../sdks/python/src python3 worker.py`.

Each run reports several distinct issues; running again bumps their event counts, which is the
grouping working. These apps are not part of CI or the pnpm/dotnet builds — they are here to be run
by hand against a live stack.
