# Condux Helm chart

Deploys the five Condux services — **relay** (ingest), **control-plane** (API), **consumer**,
**conductor**, and the **dashboard** (web) — on Kubernetes. It does **not** run the data stores: bring
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
  --set ingress.enabled=true \
  --set ingress.appHost=app.example.com \
  --set ingress.ingestHost=ingest.example.com
```

For a quick dev install you can inline the credentials instead of `existingSecret`
(`--set secret.postgresConnectionString=... --set secret.clickhouseUser=... --set secret.clickhousePassword=...`)
and the chart creates the Secret.

## What it renders

| Object | For |
|---|---|
| Deployment ×5 | relay, control-plane, consumer, conductor, web |
| Service ×3 | control-plane, relay, web (workers have none) |
| HorizontalPodAutoscaler | relay + consumer (default on), control-plane (opt-in) |
| Ingress | opt-in: `app` host → dashboard + `/api` → control-plane; `ingest` host → relay |
| Secret | credentials (unless `existingSecret`) |
| Job (hook) | migrations, pre-install/pre-upgrade |

## Migrations

A pre-install/pre-upgrade **hook Job** applies the Postgres + ClickHouse migrations from the `migrations`
image (built from `deploy/migrations.Dockerfile`, which bakes the repo's SQL — single source of truth,
idempotent). It needs native-protocol (`9000`) access to ClickHouse. Set `migrations.enabled=false` to
apply migrations yourself.

## Images

Built from the repo: `backend/Dockerfile` targets `relay`/`controlplane`/`consumer`/`conductor`,
`web/Dockerfile`, and `deploy/migrations.Dockerfile`. Push them to `image.registry` under the names in
`image.*` and set `image.tag`.

## Key values

See [`values.yaml`](values.yaml) for the full list. Highlights: `externalStores.*`, `secret.*`,
`ingress.*`, per-service `replicas`/`resources`/`autoscaling`, `github.*` (opt-in Conductor GitHub App),
`conductor.provider` (`fake`/`simulated-agent`/`anthropic`), and `config.*` (CORS, platform admins,
sampling).
