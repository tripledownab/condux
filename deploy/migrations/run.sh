#!/usr/bin/env bash
# Apply the Condux database migrations (baked into the image from the repo). Idempotent — safe to
# re-run on every install/upgrade. Run by the Helm migrations hook Job; config comes from the same
# env the services use (CONDUX_POSTGRES, CONDUX_CLICKHOUSE_URL/USER/PASSWORD).
set -euo pipefail

# Parse the Npgsql-style CONDUX_POSTGRES ("Key=Value;...") into libpq PG* env for psql.
IFS=';' read -ra _parts <<< "${CONDUX_POSTGRES}"
for kv in "${_parts[@]}"; do
  key="$(printf '%s' "${kv%%=*}" | tr '[:upper:]' '[:lower:]' | tr -d ' ')"
  val="${kv#*=}"
  case "$key" in
    host|server) export PGHOST="$val" ;;
    port) export PGPORT="$val" ;;
    username|userid|user) export PGUSER="$val" ;;
    password) export PGPASSWORD="$val" ;;
    database) export PGDATABASE="$val" ;;
  esac
done

echo "==> Postgres migrations"
for f in $(ls /migrations/postgres/*.sql | sort); do
  echo "    $f"
  psql -v ON_ERROR_STOP=1 -f "$f"
done

# ClickHouse via clickhouse-client (native protocol, multi-statement files). The host is taken from the
# HTTP URL the services use; the native port (9000) must be reachable from this Job.
CH_HOST="$(printf '%s' "${CONDUX_CLICKHOUSE_URL}" | sed -E 's#^https?://##; s#[:/].*$##')"
echo "==> ClickHouse migrations (host ${CH_HOST})"
for f in $(ls /migrations/clickhouse/*.sql | sort); do
  echo "    $f"
  clickhouse-client --host "${CH_HOST}" --user "${CONDUX_CLICKHOUSE_USER}" \
    --password "${CONDUX_CLICKHOUSE_PASSWORD}" --multiquery < "$f"
done

echo "==> Migrations complete"
