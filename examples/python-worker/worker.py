"""A background worker that reports realistic errors to Condux with the condux SDK.

It drains a batch of jobs through config, database and metrics steps. Some jobs fail the same way
(a missing secret, an unreachable database), so they group into one issue with a rising count; one
job divides by zero. Each failure is reported with its real traceback, and a couple of
capture_message calls add non-error signal. Run against a local stack:

    pip install -e ../../sdks/python
    CONDUX_DSN="http://<key>@localhost:9010/<projectId>" python worker.py
"""

import os
import sys

import condux
from condux import Level

RELEASE = "ingest-worker@0.9.3"
# STRIPE_WEBHOOK_SECRET is deliberately absent, so jobs needing it raise KeyError.
SECRETS = {"STRIPE_API_KEY": "sk_live_placeholder"}
REACHABLE_HOSTS = {"db-primary"}


def load_secret(name):
    return SECRETS[name]  # KeyError when a secret is unset


def open_connection(host):
    if host not in REACHABLE_HOSTS:
        raise ConnectionError(f"could not reach {host}:5432")
    return host


def success_rate(succeeded, window):
    return succeeded / window * 100  # ZeroDivisionError when the window is empty


def process(job):
    load_secret(job["secret"])
    open_connection(job["host"])
    success_rate(job["succeeded"], job["window"])


JOBS = [
    {"id": "job-1", "secret": "STRIPE_API_KEY", "host": "db-primary", "succeeded": 98, "window": 100},
    {"id": "job-2", "secret": "STRIPE_WEBHOOK_SECRET", "host": "db-primary", "succeeded": 0, "window": 100},
    {"id": "job-3", "secret": "STRIPE_WEBHOOK_SECRET", "host": "db-primary", "succeeded": 0, "window": 100},
    {"id": "job-4", "secret": "STRIPE_API_KEY", "host": "db-replica-2", "succeeded": 0, "window": 100},
    {"id": "job-5", "secret": "STRIPE_API_KEY", "host": "db-replica-2", "succeeded": 0, "window": 100},
    {"id": "job-6", "secret": "STRIPE_API_KEY", "host": "db-primary", "succeeded": 0, "window": 0},
    {"id": "job-7", "secret": "STRIPE_API_KEY", "host": "db-primary", "succeeded": 71, "window": 80},
]


def main():
    dsn = os.environ.get("CONDUX_DSN")
    if not dsn:
        print("Set CONDUX_DSN to a project DSN, e.g. http://<key>@localhost:9010/<projectId>", file=sys.stderr)
        raise SystemExit(1)
    condux.init(dsn, environment=os.environ.get("CONDUX_ENV", "production"), release=RELEASE)

    delivered = 0
    failed = 0
    for job in JOBS:
        try:
            process(job)
        except Exception as error:  # a worker keeps draining the queue when a job fails
            if condux.capture_exception(error).ok:
                delivered += 1
            failed += 1

    if failed:
        condux.capture_message(f"{failed}/{len(JOBS)} jobs failed this run", Level.WARNING)
    condux.capture_message(f"worker run complete: {len(JOBS) - failed}/{len(JOBS)} ok", Level.INFO)
    print(f"reported {delivered} error events to Condux (release {RELEASE})")


if __name__ == "__main__":
    main()
