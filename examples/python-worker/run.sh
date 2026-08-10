#!/usr/bin/env bash
# Turnkey runner: create a local venv and install the condux SDK on first run, then run the worker.
# `condux` is not published (it lives in ../../sdks/python), and Homebrew Python refuses a bare
# `pip install` (PEP 668), so the venv is the clean way to make `worker.py` just work.
#
# Usage: CONDUX_DSN="http://<key>@localhost:9010/<projectId>" ./run.sh
set -euo pipefail
cd "$(dirname "$0")"

VENV=.venv
if [ ! -d "$VENV" ]; then
  echo "First run: creating $VENV and installing the condux SDK..."
  python3 -m venv "$VENV"
  "$VENV/bin/pip" install --quiet --upgrade pip
  "$VENV/bin/pip" install --quiet -r requirements.txt
fi

exec "$VENV/bin/python" worker.py "$@"
