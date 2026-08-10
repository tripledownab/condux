#!/usr/bin/env bash
# Turnkey runner: install @condux/node on first run, then run the checkout example.
# `--ignore-workspace` keeps this standalone app out of the repo's pnpm workspace, so its `file:`
# link to @condux/node installs into its own node_modules.
#
# Usage: CONDUX_DSN="http://<key>@localhost:9010/<projectId>" ./run.sh
set -euo pipefail
cd "$(dirname "$0")"

if [ ! -d node_modules ]; then
  echo "First run: installing @condux/node..."
  pnpm install --ignore-workspace
fi

exec node checkout.mjs "$@"
