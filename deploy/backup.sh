#!/usr/bin/env bash
# Nightly Postgres backup with 14-day rotation for the single-VPS prod deploy
# (docs/runbooks/backup-restore.md, docs/runbooks/production-deploy.md).
#
# Versioned + run in place from the repo's deploy/ dir (~/condux/deploy on the box), so a deploy
# (git reset --hard origin/main) keeps it current. Dumps via the postgres container using that container's
# OWN POSTGRES_* env (no creds hardcoded here), gzips to ./backups/, and prunes dumps older than 14 days.
# Installed cron (see the runbook): 30 3 * * * /root/condux/deploy/backup.sh >> /root/condux/deploy/backups/backup.log 2>&1
#
# Scope: Postgres only. It is the critical, non-rebuildable store (orgs/users/sessions, projects + DSN
# keys, grouped issues, alert rules + channels, repo links, fix runs, encrypted llm_configs, quota/spend).
# ClickHouse events are large + rebuildable (issues live in Postgres and new events refill), so they are
# NOT in this nightly; if you need event history, run the runbook's native ClickHouse BACKUP separately.
#
# NOTE: writes to the box's OWN disk, which protects against bad migrations / accidental drops but NOT box
# loss. For box-loss durability also enable your provider's server snapshots or ship ./backups off-box
# (see runbook).
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
