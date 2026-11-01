# Migrations

Schema migrations for Condux's two stores.

```
migrations/
  clickhouse/   event store + rollups
  postgres/     control-plane metadata (orgs, projects, DSN keys, issues)
```

## Convention

Files are numbered (`0001_*.sql`, `0002_*.sql`, …) and applied in **filename order**. Every file is
**idempotent** (`CREATE TABLE IF NOT EXISTS`, `CREATE INDEX IF NOT EXISTS`), so re-applying the whole
set is safe. The compose `postgres-init`/`clickhouse-init` one-shots apply them when the stack starts
(`docker compose -f deploy/docker-compose.yml up -d`) and on every deploy: ClickHouse runs every file
each time, Postgres only what its ledger has not recorded (below).

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

Applied in filename order by `deploy/migrations/apply-postgres.sh`, which both compose's `postgres-init`
one-shot and the Helm migrations job run. It records each file it applies, with the SHA-256 of its SQL
(whole-line `--` comments left out, so rewording one is not an edit; a blank line still is), in
`condux_migrations`. An unchanged database runs nothing, and new files that sort after every
recorded one run alone; an edit to a recorded file, a new file sorting between recorded ones or a recorded
file removed replays every file from the first, as every deploy used to, and a replay that fails part way
is replayed again by the next run. So every file must still be idempotent. To apply against a running
stack:

```bash
docker compose -f deploy/docker-compose.yml run --rm -T postgres-init
```

Current files:

- `0001_issues.sql` — grouped issues (the consumer upserts here).
- `0002_orgs_projects_dsns.sql` — control-plane catalog: orgs, projects, dsn_keys.

The Helm chart applies both stores from a pre-install and pre-upgrade hook Job (`deploy/migrations/run.sh`).
There are no down migrations.
