# Migrations

Schema migrations for Condux's two stores.

```
migrations/
  clickhouse/   event store + rollups (applied by the consumer/ops on startup)
  postgres/     control-plane metadata (orgs, projects, DSN keys, issues)
```

## Convention

Files are numbered (`0001_*.sql`, `0002_*.sql`, …) and applied in **filename order**. Every file is
**idempotent** (`CREATE TABLE IF NOT EXISTS`, `CREATE INDEX IF NOT EXISTS`), so re-applying the whole
set is safe. Locally, the compose `postgres-init`/`clickhouse-init` one-shots apply them automatically
when the stack starts (`docker compose -f deploy/docker-compose.yml up -d`); the manual commands below
re-apply them against a running stack.

## ClickHouse

Apply in filename order. For local dev:

```bash
docker compose -f deploy/docker-compose.yml exec -T clickhouse \
  clickhouse-client --multiquery < migrations/clickhouse/0001_init.sql
```

Files are idempotent (`IF NOT EXISTS`). In production, run them from a migration job/step before
rolling out the consumer. Retention (`TTL`) values are the defaults — the control-plane overrides
per-project retention according to the customer's plan tier.

## Postgres

Applied in filename order; the compose `postgres-init` one-shot loops over `postgres/*.sql` when the
stack starts. To (re-)apply against a running stack:

```bash
for f in migrations/postgres/*.sql; do
  docker compose -f deploy/docker-compose.yml exec -T postgres \
    psql -v ON_ERROR_STOP=1 -U condux -d condux < "$f"
done
```

Current files:

- `0001_issues.sql` — grouped issues (the consumer upserts here).
- `0002_orgs_projects_dsns.sql` — control-plane catalog: orgs, projects, dsn_keys.

For production self-host we recommend promoting these to a migration tool with up/down semantics
(`dbmate` or `goose`), run as a Helm pre-install/upgrade hook. The files here are already numbered and
idempotent, so that migration is mechanical.
