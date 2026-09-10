# Changelog

Written for the people who run Condux, not for us. Each `## <version>` section becomes the commit
message and the release notes on the public mirror verbatim, so write it as something a stranger reads
with no other context, and keep production specifics, internal reasoning and commercial detail out of it.

Because this file is tracked, the text is reviewed in a pull request like any other change rather than
being typed into a release box at the moment everyone wants the release out.

## 0.4.0

**If you self-host and you have connected Condux to GitHub, this release needs two configuration values
you may not have set.** Read the first section before upgrading. Everything else here is additive.

### Connecting a GitHub installation now proves it is yours

Condux links a GitHub App installation to an organisation so the fix engine can read your repositories
and open draft pull requests on them. The step that wrote that link was a URL anyone could request, and
it took the installation's id from the query string.

An address bar is not evidence. Anyone with an ordinary account could name any installation id and
attach it to their own organisation, taking it from the organisation that was using it, and from there
the product would mint a repository token for it in the normal way. If you run Condux for more than one
organisation, or let people sign themselves up, assume that was reachable.

Linking now happens only after asking GitHub which installations the person in front of it can actually
reach, whether Condux picks the single one it finds or you choose from several. The redirect back from
GitHub writes nothing at all, and separately, an installation another organisation already holds can no
longer be taken by any route: freeing one is still a disconnect.


**What you need to do.** `CONDUX_GITHUB_CLIENT_SECRET` and `CONDUX_GITHUB_OAUTH_REDIRECT_URI` were
optional and are now required whenever the GitHub App is configured. If either is missing the
control-plane refuses to start and names them, rather than falling back to the behaviour above. Set the
redirect URI to your API's `/api/github/oauth/callback`, matching a Callback URL on the app, and make
sure the app has "Request user authorization (OAuth) during installation" checked.

Helm users set `github.clientSecret` and `github.oauthRedirectUri`. A chart install with
`github.enabled` will not render without the redirect URI. It cannot check the client secret when you
bring your own Secret through `secret.existingSecret`, so add `CONDUX_GITHUB_CLIENT_SECRET` to it, as
listed in `values.yaml` beside the other keys that Secret must carry.

Managed cloud already had both, so nothing there changes.

### Also in this release

- Reading an OpenTelemetry exception stack trace is now bounded work. A deliberately malformed one could
  previously cost an ingest thread far more than its size suggested.

## 0.3.0

**If you use the JVM, .NET or Go SDK, this release regroups your existing issues.** Read the first
section before upgrading. Everything else here is additive.

### Your stack traces were mostly not your code

Each SDK marks a frame as belonging to your application or to something underneath it. That flag is not
cosmetic. It decides how events group into issues, which frame is named as the culprit, and which files
the fix engine is allowed to read. Three of our SDKs got it wrong.

Measured against a real Spring application, the JVM SDK reported 53 frames and called 48 of them yours.
One of them actually was. The .NET SDK counted its own reporting middleware as your code, so it rode
along in every report. The Go SDK excluded only the standard library, which left every dependency, and
the SDK itself, looking like your application.

All three now exclude their ecosystem's dependency locations and their own frames.

**The consequence: because grouping is computed from those frames, events arriving after you upgrade
will not match issues created before it.** Expect existing JVM, .NET and Go issues to stop receiving
events, and new issues to appear in their place. Nothing is lost and no action is required, but it will
look like a spike of new issues on the day you upgrade, so it is worth doing deliberately rather than
being surprised by it.

The JVM has no file path to work from, so it decides by package prefix. If your application lives inside
a framework's namespace, tell the SDK explicitly:

```java
ConduxClient condux = ConduxClient.builder(dsn)
    .inAppPackages("com.acme.")
    .build();
```

Keep the trailing dot. Prefixes are matched with `startsWith`, so `com.acme` without it would also
claim `com.acmecorp`. Setting this replaces the built in guess rather than adding to it: what you list
is your application and everything else is not.

### Two-factor authentication

Available to every account on every plan. Charging for account security is not something we intend to
do.

Enrol from Settings. You scan a QR code with any authenticator app and confirm one code, and you are
issued **10 single use recovery codes** at the same time. Store them somewhere other than the device
holding your authenticator, because they are the only way back into an account whose second factor is
lost. Each one works once.

Signing in with a password or with Google now asks for a code. **Signing in through your organisation's
own identity provider does not**, because that provider already decides how you authenticate and how
strongly, and asking twice for the same login is not more secure.

Self-hosting: this needs `CONDUX_SECRET_KEY` to be set, since the shared secret behind each account's
codes is encrypted with it rather than hashed. Without it, enrolling answers `404` with the body
`secret_key_not_configured`, so if enrolment appears to be missing entirely, read the response body
before concluding the build lacks the feature.

### OpenTelemetry exporters that send protobuf now work

The logs endpoint previously accepted only the JSON encoding. It now accepts protobuf as well, which is
what most OpenTelemetry exporters send by default, so pointing one at Condux no longer requires
reconfiguring it.

Failures are also reported properly now. Every error response carries a `google.rpc.Status` in the same
encoding the request arrived in, rather than an empty body or a shape of our own invention, so your
exporter can tell you what went wrong instead of only that something did.

### Installing the JVM SDK no longer needs a token

`ai.condux:condux` is published to Maven Central. Maven and Gradle both resolve it anonymously with no
repository declaration and no credentials, so the snippet in the JVM SDK's README now works as written.

### Also in this release

- Removing someone from an organisation asks for confirmation first, and the role selector no longer
  breaks out of its row on a narrow screen.
- The published source tree explains itself without pointing at documents it does not ship.

## 0.2.1

**If you report errors from a Flask or Django app, upgrade the Python SDK.** Those two integrations
never worked. Both frameworks catch an exception raised in a view, turn it into a 500 and return it, so
nothing escapes the application, and the middleware we shipped wrapped the application from the outside.
It saw nothing and reported nothing, silently, with no error to suggest it was not working. Anyone who
followed our own instructions had an application that looked instrumented and sent no errors at all.

They now hook the signal each framework actually fires:

```python
from condux.integrations.flask import ConduxFlask
ConduxFlask(app)

# Django, in settings.py
MIDDLEWARE = ["condux.integrations.django.ConduxMiddleware", ...]
```

Each reports the URL, the method and the matched route as a tag, using the parameterised form so every
instance of a route groups together, and keeps enrichment isolated to its own request. The old WSGI
middleware is still correct for a bare WSGI application, and it says so now rather than naming two
frameworks it could not serve.

**Spring MVC reported every error under one type.** A servlet container rethrows what a handler threw
wrapped in a `ServletException`, and since issues group on the exception type, every failure in an
application collapsed into a single issue with the real error buried in a message. The filter now
unwraps it. Spring apps with a global `@ExceptionHandler` also reported nothing at all, because the
exception is resolved before a filter can see it, so there is a new `ConduxExceptionResolver` to
register alongside the filter; it never claims an exception, so your own error handling is unchanged.

**The Laravel package would not install on Laravel 13.** The constraint now includes it.

**Where an integration sits decides whether it reports anything**, and two of our own instructions had
it backwards. On ASP.NET Core, register `UseConduxExceptionReporting()` **after** `UseExceptionHandler`,
inside it: middleware only sees an exception as it unwinds, so anything outside a configured handler
never sees one. On Rails, use `config.middleware.use` and nothing else, because it appends and that is
what places the middleware inside `ActionDispatch::ShowExceptions`.

All of this was found by running a real application of each framework and checking what actually arrived
at a relay. Our own tests could not catch any of it: they drove the middleware with a stand-in
application that raises, which assumes the exception escapes, the exact thing that was false. The Python
SDK now tests against real Flask, Django and Starlette, and its build fails if those tests are skipped.

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
