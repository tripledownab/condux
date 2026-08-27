#!/usr/bin/env bash
# Nightly Postgres backup with 14-day rotation, for a deployment running the compose stack on one host.
#
# Run it in place from the repo's deploy/ directory, so redeploying (git reset --hard origin/main) keeps
# it current. It dumps through the postgres container using that container's OWN POSTGRES_* environment,
# so no credentials live here, gzips into ./backups/, and prunes dumps older than 14 days.
#
# Schedule it with cron, pointing at wherever the repository is checked out:
#   30 3 * * * /opt/condux/deploy/backup.sh >> /opt/condux/deploy/backups/backup.log 2>&1
#
# Scope: Postgres only, because it is the store that cannot be rebuilt (orgs, users and sessions,
# projects and their DSN keys, grouped issues, alert rules and channels, repo links, fix runs, the
# encrypted LLM configs, quota and spend). ClickHouse events are large and ARE rebuildable, since the
# issues live in Postgres and new events refill the rest, so they are deliberately not in this nightly.
# To keep event history as well, run ClickHouse's own native BACKUP separately.
#
# This writes to the same host's disk. That protects against a bad migration or an accidental drop, and
# not against losing the host. For that, also enable your provider's snapshots or ship ./backups
# somewhere else.
set -euo pipefail
cd "$(dirname "$0")"

DEST="$PWD/backups"
mkdir -p "$DEST"
STAMP="$(date +%F-%H%M)"
OUT="$DEST/condux-$STAMP.sql.gz"
TMP="$OUT.partial"
# Never leave a half-written dump masquerading as a good backup: write to .partial, promote only on success.
trap 'rm -f "$TMP"' EXIT

docker compose --env-file .env.prod -f docker-compose.yml -f docker-compose.prod.yml exec -T postgres \
  sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB"' | gzip >"$TMP"
mv "$TMP" "$OUT"

find "$DEST" -name 'condux-*.sql.gz' -mtime +14 -delete
