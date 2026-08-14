"""``python -m condux.test_event`` — prove the pipeline end to end.

An error monitor's failure mode is silence, and silence looks exactly like health. This sends one
info-level message through the real client and transport and reports the delivery outcome, so "did my
DSN / network / relay work" is one command instead of waiting for a production error.

    CONDUX_DSN=https://key@ingest.condux.ai/project python -m condux.test_event
    python -m condux.test_event --dsn https://key@ingest.condux.ai/project --message "hello"
"""

from __future__ import annotations

import os
import sys
from typing import List, Optional

import condux
from condux import Level

USAGE = "Usage: python -m condux.test_event [--dsn <dsn>] [--message <text>]"


def _arg_value(argv: List[str], name: str) -> Optional[str]:
    if name in argv:
        index = argv.index(name)
        if index + 1 < len(argv):
            return argv[index + 1]
    return None


def run(argv: List[str]) -> int:
    """Send the test event; returns the process exit code (0 delivered, 1 failed, 2 usage)."""
    for argument in argv:
        if argument.startswith("-") and argument not in ("--dsn", "--message"):
            print(f"condux test-event: unknown option '{argument}'. {USAGE}", file=sys.stderr)
            return 2

    dsn = _arg_value(argv, "--dsn") or os.environ.get("CONDUX_DSN")
    if not dsn:
        print("condux test-event: no DSN. Pass --dsn <dsn> or set CONDUX_DSN.", file=sys.stderr)
        return 2

    try:
        condux.init(dsn, environment="condux-test")
    except ValueError as error:
        print(f"condux test-event: {error}", file=sys.stderr)
        return 2

    message = _arg_value(argv, "--message") or "Condux test event"
    result = condux.capture_message(message, Level.INFO)
    if result.ok:
        attempts = f"{result.attempts} attempt{'' if result.attempts == 1 else 's'}"
        print(
            f'Delivered "{message}" ({attempts}). '
            "Check your project's issues list; a test message appears as an info-level issue."
        )
        return 0

    reason = result.error or f"relay answered {result.status}"
    print(
        f"Delivery FAILED after {result.attempts} attempt(s): {reason}. "
        "Check the DSN (Project settings -> DSN keys) and that the ingest host is reachable.",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    sys.exit(run(sys.argv[1:]))
