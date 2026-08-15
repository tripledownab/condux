# Changelog

Written for the people who run Condux, not for us. Each `## <version>` section becomes the commit
message and the release notes on the public mirror verbatim, so write it as something a stranger reads
with no other context, and keep production specifics, internal reasoning and commercial detail out of it.

Because this file is tracked, the text is reviewed in a pull request like any other change rather than
being typed into a release box at the moment everyone wants the release out.

## 0.2.0

**Security fix, upgrade from 0.1.0.** A privilege escalation in organisation creation let an account
put itself on a paid plan, taking that plan's higher ingest and Conductor limits. Organisations are
now always created on the Free plan, and billing is the only thing that sets a tier. Anyone running
0.1.0 should upgrade. To check your own data, look for a paid tier with no matching subscription
behind it.

**Run fixes on your own infrastructure.** A Condux runner is a small process you deploy inside your
own network. It leases fix work over a single authenticated outbound HTTPS call, runs it with your
model key and your own source host token, and reports the result, so neither credential reaches
Condux and your repository is only ever cloned inside your network. It opens no inbound port, no
database connection and no broker connection. Point an organisation at runner execution and its fix
requests queue for the runner instead of the hosted engine. Dependency bump runs route the same way.

**The Conductor explores the repository now.** A fix run reads what it needs, runs commands in a
sandboxed workspace and iterates, rather than proposing a patch from a fixed handful of files in one
shot, and it is asked for a test that fails without the fix. Runs go through Anthropic or through any
OpenAI compatible endpoint. The result is still only ever a draft pull request, and the model is
still never given a credential that can write to your repository.

**Source maps end to end.** Wrap a Next.js config with `withConduxConfig` and a production build
stamps every client chunk with a debug id, uploads the matching source map with a scoped release
token, and leaves nothing publicly deployed. Minified frames then resolve back to real files, lines
and functions in the issue view. The same wrapper can route events through a same origin path of your
app, so an ad blocker does not quietly drop your browser errors.

**Dependency fixes reach monorepos.** Scanning is no longer tied to a single forge: an OSV backed
scanner covers repositories with no native advisory feed. One advisory affecting several workspace
members is now bumped across all of them in a single draft pull request, following each alert to the
manifest it actually belongs to.

**SAML single sign on**, beside the existing OIDC, on the same per organisation registry. Sign in is
email first, so a user types their address and lands at their own identity provider. Responses are
validated against a certificate you pin and bound to the browser that began the flow, so only sign
ins that start at Condux are accepted.

**SDKs across every language.** Capture never throws. An uninitialised SDK warns once and returns a
failure value instead of raising, which matters because the framework adapters capture inside an
exception handler before re raising, and raising there replaced the application's own exception with
ours. Every SDK gained an enrichment scope, so a user, tags, contexts and breadcrumbs you set once
ride every later event, and a command that sends one test event and exits with a documented code, so
an install is verifiable without opening a browser. A malformed DSN is rejected at init instead of
accepting events and reporting them nowhere.

**Operations.** The relay and the control plane answer a readiness probe that names the build
answering it and reports its datastores, so a deploy can be gated on it rather than on a port opening.
The dashboard serves its own brand fonts rather than fetching them from a third party at build time.
Plan limits are published from the same catalog that enforces them, so a client cannot drift from the
server.

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
