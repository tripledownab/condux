# Changelog

Written for the people who run Condux, not for us. Each `## <version>` section becomes the commit
message and the release notes on the public mirror verbatim, so write it as something a stranger reads
with no other context, and keep production specifics, internal reasoning and commercial detail out of it.

Because this file is tracked, the text is reviewed in a pull request like any other change rather than
being typed into a release box at the moment everyone wants the release out.

## 0.1.0

First public release.

Condux is a source-available error monitoring platform with an AI fix engine. SDKs report errors to a
stateless ingest relay, which authenticates, rate-limits and scrubs PII before buffering through
Redpanda; a consumer fingerprints and groups events into issues and writes time series to ClickHouse,
while issues, orgs, projects, alerts and audit records live in PostgreSQL.

**The Conductor** turns an error into a human-reviewed **draft pull request**. It assembles a scoped,
scrubbed context from the stack trace and your code mappings, sends it to Claude or your own model, and
opens the fix on a branch as a draft PR. It never auto-merges. After a fix merges, a verification loop
watches the issue against its real event stats and resolves it with evidence once it stays quiet.

**Ingestion is Sentry-envelope compatible**, so an existing Sentry SDK works by changing the DSN, and
native OTLP logs are accepted so an OpenTelemetry app can report with no SDK at all. First-party SDKs
ship for JavaScript/Node, browser, edge, Next.js, Python, Go, Ruby, PHP, JVM and .NET.

**Self-hosting** is a single Docker Compose stack, or a Helm chart against your own managed stores.

Source is available under the Functional Source License; every release converts to Apache-2.0 two years
after it ships.
