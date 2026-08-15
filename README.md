# Condux

Condux is a source-available error monitoring platform with a built-in AI fix engine.

[![An issue in Condux, with its stack trace, source context, volume chart and triage actions](https://condux.ai/screenshots/issue-detail-light.jpg)](https://condux.ai)

SDKs report errors to a stateless ingest relay, which authenticates the DSN, applies per-plan rate
limits and quotas, and scrubs PII before buffering through Redpanda. A consumer fingerprints and groups
events into issues, writes time series to ClickHouse and issue state to PostgreSQL. A Next.js dashboard
reads that back as grouped issues with stack traces, breadcrumbs, tag facets and per-hour charts, and
alerting fires on new and regressed issues to email, Slack, Discord and webhooks.

## The Conductor

The fix engine takes an issue to a draft pull request. It assembles a scoped, scrubbed context from the
stack trace and the project's code mappings, sends it to Claude or to a model the organisation supplies,
and applies the returned changes on a new branch as a **draft** pull request. Fixes are never merged
automatically.

[![A Conductor fix run on an issue, showing its status, the model token usage and the draft pull request it opened](https://condux.ai/screenshots/fix-run-light.jpg)](https://condux.ai)

Repository access is a GitHub App with short-lived installation tokens; the model is not given the
token. After a fix PR merges, a verification loop compares the issue against its subsequent event
counts and resolves it once it stays silent for a configured window.

Open Dependabot findings on a connected repository can be turned into dependency-bump draft PRs through
the same engine.

Each run is metered against the organisation's monthly allowance and a spend ceiling, with per-run cost
derived from recorded model token usage.

## Compatibility

Ingestion accepts the **Sentry envelope and store formats**, so an existing Sentry SDK reports to Condux
by changing its DSN. **OTLP/HTTP logs** are also accepted, which lets an OpenTelemetry-instrumented
application report without an SDK.

First-party SDKs are published for JavaScript/Node, browser, edge, Next.js, Python, Go, Ruby, PHP, JVM
and .NET, with framework adapters for React, Express, Flask/Django/FastAPI, Rails, ASP.NET Core and
Spring Boot. The SDKs are Apache-2.0; the server is FSL.

## MCP endpoint

A read-only Model Context Protocol endpoint lets an AI agent debug against real production errors.
Point Claude, Cursor or any MCP client at a project with a scoped token and it can list issues, read
stack traces and page through occurrences. There are no write tools, and a token resolves to exactly
one project.

[![The MCP tab of a project, where a scoped read-only token is minted with its client configuration snippet](https://condux.ai/screenshots/mcp-tab-light.jpg)](https://condux.ai)

## Architecture

.NET 10 services and a Next.js / React / TypeScript dashboard.

```
 SDKs ──envelope──▶ [Relay] ──▶ [Redpanda] ──▶ [Consumer] ──▶ ClickHouse (events, time series)
                                                          └──▶ PostgreSQL (issues, orgs, audit)
 Dashboard ──▶ [ControlPlane] ──▶ [Conductor] ──▶ draft PR
```

Valkey backs rate limiting and monthly quotas. The relay holds no state. Raw events expire per plan
through a column-driven ClickHouse TTL rather than a scheduled job.

## Repository layout

| Path | Contents |
|---|---|
| `backend/` | .NET solution: `Condux.Relay` (ingest), `Condux.ControlPlane` (API), `Condux.Consumer`, `Condux.Conductor` (fix engine), `Condux.Core` (scrubbing, grouping, plans, quotas) |
| `web/` | Next.js dashboard |
| `sdks/` | Client SDKs and framework adapters |
| `packages/` | Shared frontend packages |
| `proto/` | Protobuf contracts |
| `migrations/` | PostgreSQL and ClickHouse schema, numbered and idempotent |
| `contracts/` | Exported OpenAPI document and Postman collection |
| `deploy/` | Docker Compose stack and Helm chart |
| `examples/` | Sample applications that report errors |

## Requirements

- .NET SDK 10.0 or later
- Node 22 or later, with pnpm
- Docker and Compose

## Running it

```bash
cp deploy/.env.example deploy/.env
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d
```

The dashboard listens on port 3000 and the API on 8080.

Building and testing from source:

```bash
cd backend && dotnet build Condux.sln -c Release
cd backend && dotnet test Condux.sln --filter "Category!=Integration"
cd web && pnpm install && pnpm build
```

Integration tests start real datastores through Testcontainers and bind the same ports as the Compose
stack, so run them with that stack stopped.

## Deployment

`deploy/docker-compose.yml` runs the whole platform on a single host. `deploy/charts/condux` is a Helm
chart that deploys the services against externally managed PostgreSQL, ClickHouse and a Kafka-API
broker rather than running datastores in the cluster.

## Security

Report suspected vulnerabilities privately by email as described in [`SECURITY.md`](SECURITY.md), rather
than in a public issue or pull request.

## License

Condux is source-available under the **Functional Source License (FSL-1.1-ALv2)**. It may be read,
self-hosted and modified for any purpose other than offering a competing commercial service, and each
release converts to Apache-2.0 two years after it is published.

See [`LICENSE`](LICENSE) for the terms and [`LICENSING.md`](LICENSING.md) for a plain-language summary.
The SDKs under `sdks/` are Apache-2.0.
