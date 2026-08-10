# The migrations image for the Helm chart's pre-install/pre-upgrade hook Job. It bakes the repo's
# Postgres + ClickHouse migration SQL (single source of truth) and applies both, idempotently.
#
# Build from the repo root (so it can COPY migrations/ + deploy/migrations/run.sh):
#   docker build -f deploy/migrations.Dockerfile -t ghcr.io/condux/migrations:<tag> .
#
# Base on the ClickHouse image (ships clickhouse-client for multi-statement files) and add psql.
FROM clickhouse/clickhouse-server:24.8
RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY migrations /migrations
COPY deploy/migrations/run.sh /usr/local/bin/run-migrations
RUN chmod +x /usr/local/bin/run-migrations
ENTRYPOINT ["/usr/local/bin/run-migrations"]
