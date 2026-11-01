#!/usr/bin/env bash
# What belongs here: applying the database migrations BEFORE a deploy swaps any application service. Both deploys run
# this file, so there is one statement of that order rather than one per workflow.
#
# Usage:  apply-migrations.sh <compose command...>    e.g.  apply-migrations.sh docker compose -f a.yml
# Exit:   0 when both migration jobs completed, otherwise the failing job's status, before any service
#         that waits on them has been swapped. The databases themselves are started first, or recreated
#         when their own configuration changed (a release bump), as the `up` that follows would do.
#
# WHY IT RUNS FIRST. The services wait on postgres-init and clickhouse-init (service_completed_successfully),
# and `up` recreates a service before it knows whether that wait will succeed: a failed migration left the
# old container removed and its replacement created but never started. So a migration that failed inside
# `up` took down every service waiting on its job that the deploy recreated, until it was fixed: any of
# the dashboard, the API, the workers and the relay for Postgres, all but the relay for ClickHouse. A
# service whose image and configuration did not change keeps running. Run first and on their own, the same jobs
# fail while those services still run the previous release, and the deploy stops there.
#
# Build the images and pull any the host lacks BEFORE running this: a broken build or a failed pull then
# changes nothing, and the previous release runs on the new schema only from the migrations to the end of
# the swap. Both deploys do.
#
# What it does not make safe. The jobs run in order, so when clickhouse-init fails, postgres-init has
# already applied: the previous release goes on serving on the migrated Postgres schema until a deploy
# succeeds, which a migration that drops or renames something can break. And the jobs run again inside the
# `up` that follows, because they are services the others wait on. That pass applies no Postgres file,
# since this one recorded them all (deploy/migrations/apply-postgres.sh), and replays every ClickHouse
# file, which is idempotent and normally changes nothing; but a ClickHouse file that fails there (a
# transient error) brings back the failure this script exists to prevent.
set -euo pipefail

if [ "$#" -eq 0 ]; then
  echo "usage: apply-migrations.sh <compose command...>" >&2
  exit 2
fi

for job in postgres-init clickhouse-init; do
  echo "==> $job"
  "$@" run --rm -T "$job"
done
