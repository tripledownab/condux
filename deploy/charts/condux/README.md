# Condux Helm chart

Deploys the Condux services on Kubernetes: **relay** (ingest), **control-plane** (API), **consumer**,
**conductor**, the **dashboard** (web) and the **symbolizer** (native frame resolution: it holds no
credential, and its NetworkPolicy, where the cluster enforces one, admits only the control-plane and the
consumer). It does **not** run the data stores: bring
your own managed **Postgres, ClickHouse, Kafka/Redpanda and Valkey/Redis** (the recommended production
posture) and point the chart at them.

## Install

```sh
# 1. Create the credentials Secret (recommended for prod) with these keys:
kubectl create secret generic condux-secrets \
  --from-literal=CONDUX_POSTGRES='Host=pg;Username=condux;Password=...;Database=condux;SSL Mode=Require' \
  --from-literal=CONDUX_CLICKHOUSE_USER=condux \
  --from-literal=CONDUX_CLICKHOUSE_PASSWORD=...

# 2. Install, pointing at your external stores.
helm install condux deploy/charts/condux \
  --set secret.existingSecret=condux-secrets \
  --set externalStores.clickhouse.url=http://clickhouse:8123 \
  --set externalStores.kafka.bootstrap=redpanda:9092 \
  --set externalStores.valkey.url=valkey:6379 \
  --set config.ingestHost=ingest.example.com \
  --set ingress.enabled=true \
  --set ingress.appHost=app.example.com
```

`config.ingestHost` is required and the render fails without it. It is the public relay address the
control-plane writes into every DSN it mints, and the code's own default is the dev `localhost:9010`,
so an unset host mints DSNs that reach no relay while every pod still reports healthy. The ingress rule
for the relay reads the same value, since an SDK arrives carrying the host from its DSN. Build the web
image with a matching `NEXT_PUBLIC_CONDUX_INGEST_URL` too, which is what the dashboard rebuilds a key's
displayed DSN from.

The migrations hook Job reaches both stores with a different client than the services use, so it
translates `CONDUX_POSTGRES` into libpq variables for `psql` and reads the scheme of
`externalStores.clickhouse.url` to decide whether to encrypt. `SSL Mode` is carried across in all six
of its values, along with `Root Certificate`, `SSL Certificate`, `SSL Key`, `SSL Password`,
`Channel Binding`, `Kerberos Service Name`, `Passfile` and `Search Path`. A security setting it
cannot translate **fails the install** rather than being dropped: libpq defaults to `sslmode=prefer`,
which falls back to plaintext whenever the server offers it, so a dropped setting would apply the
whole schema over an unencrypted connection and report success. `Options` is refused too, on its own
grounds: it can carry a `search_path` of its own, and quietly choosing between that and the
`Search Path` setting is how a schema ends up somewhere nobody looks. Settings that only shape a pooled
client, such as pool sizes and timeouts, are ignored, as is `Trust Server Certificate`, which Npgsql
itself now ignores.
An `https://` ClickHouse URL selects the secure native port, 9440, which the Job must be able to
reach; an `http://` one selects 9000.

For a quick dev install you can inline the credentials instead of `existingSecret`
(`--set secret.postgresConnectionString=... --set secret.clickhouseUser=... --set secret.clickhousePassword=...`)
and the chart creates the Secret.

## What it renders

| Object | For |
|---|---|
| Deployment | one per service: relay, control-plane, consumer, conductor, web, symbolizer |
| Service | control-plane, relay, web, symbolizer (the consumer and conductor have none) |
| NetworkPolicy | the symbolizer's: no egress, ingress from the control-plane and the consumer only |
| HorizontalPodAutoscaler | relay + consumer (default on), control-plane (opt-in) |
| Ingress | opt-in: `app` host → dashboard, with `/api` and `/.well-known/mcp` (the MCP server card) → control-plane; `ingest` host → relay |
| Secret | credentials (unless `existingSecret`) |
| Job (hook) | migrations, pre-install/pre-upgrade |

## Migrations

A pre-install/pre-upgrade **hook Job** applies the Postgres + ClickHouse migrations from the `migrations`
image (built from `deploy/migrations.Dockerfile`, which bakes the repo's SQL — single source of truth,
idempotent). It needs native-protocol access to ClickHouse: port `9440` when
`externalStores.clickhouse.url` is `https`, otherwise `9000`. Set `migrations.enabled=false` to
apply migrations yourself.

## Images

Built from the repo: `backend/Dockerfile` targets `relay`/`controlplane`/`consumer`/`conductor`/`symbolizer`,
`web/Dockerfile`, and `deploy/migrations.Dockerfile`. Push them to `image.registry` under the names in
`image.*` and set `image.tag`.

## Key values

See [`values.yaml`](values.yaml) for the full list. Highlights: `externalStores.*`, `secret.*`,
`ingress.*`, per-service `replicas`/`resources`/`autoscaling`, `github.*` (opt-in Conductor GitHub App),
`conductor.provider` (`fake`/`simulated-agent`/`anthropic`/`managed-agents`), and `config.*` (CORS,
platform admins, sampling).

The conductor's tuning values are rendered only when set, so an empty one keeps the conductor's own
default: `conductor.pollSeconds` and `conductor.maxPolls` (the poll cadence, read under every provider and
by the sweep for stale runs), and for `managed-agents` `conductor.maxSessionUsd` (the hard budget for each
session) and the pins `conductor.agentId`/`conductor.environmentId`. A pinned environment or agent is used
exactly as configured, so give a pinned environment limited networking with no allowed hosts, and switch
`read`, `web_fetch` and `web_search` off on a pinned agent.

`sso.skipDomainVerification` deserves its own line. It accepts an org's claimed email domain without the
DNS TXT record that proves the org controls it, and it exists for a single-tenant install on an internal
domain with no public DNS to publish into. On an installation serving more than one organization it is
what stops an org claiming a domain it does not own, so leave it false.
