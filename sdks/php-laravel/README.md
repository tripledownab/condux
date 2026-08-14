# Condux for Laravel

Automatic exception reporting to a Condux relay for Laravel apps. A thin integration over the base
[`condux/condux`](../php) SDK: it registers a Condux capture on Laravel's exception handler, so every
reported exception is sent in the Sentry "store" wire shape (the relay normalizes it like an official
Sentry SDK). Delivery is resilient and **never throws** — reporting can't crash your app.

## Install

```bash
composer require condux/condux-laravel
```

The service provider is auto-discovered. Set a DSN and you're done:

```dotenv
CONDUX_DSN=https://<key>@ingest.condux.ai/<projectId>
```

Uncaught exceptions (anything Laravel reports) now show up in Condux automatically, marked **unhandled** —
no code changes and, unlike some SDKs, **no edit to `bootstrap/app.php`**. Without `CONDUX_DSN` the package
stays inert: nothing is reported, `\Condux\Client` still resolves from the container, and captures on it
are dropped locally. So it is safe to install ahead of configuring it, and safe to type-hint the client in
code that ships before the DSN does.

## Verify it works

An error monitor's failure mode is silence, and silence looks like health. Prove the pipeline before
waiting for a real error:

```bash
php artisan condux:test
```

It sends one test event through the real client and transport and reports the outcome (a nonzero exit
when it is not configured or delivery fails, so it can gate a deploy script).

To capture something manually, resolve the client:

```php
app(\Condux\Client::class)->captureMessage('cache miss storm', \Condux\Level::WARNING);
```

## Users, tags, contexts and breadcrumbs

Enrichment is ambient: set it wherever you know it (middleware, a job, a listener) and every subsequent
event carries it, with nothing to thread through your capture calls.

```php
use Condux\Level;
use Condux\Scope;

Scope::setUser(['id' => (string) $request->user()?->id]); // setUser(null) on sign-out
Scope::setTag('tenant', $tenant->slug);                   // a null value removes a tag
Scope::setContext('subscription', ['seats' => 12]);
Scope::addBreadcrumb('checkout started', category: 'order', level: Level::INFO);
```

The relay scrubs all of it at ingest and derives the pseudonymous users-affected count from the user
fields. The trail keeps the newest 30 breadcrumbs. A request resets the scope by ending; in a queue worker
or under Octane the process is reused, so call `Scope::clear()` between jobs.

Optionally publish the config to tune the environment, release, and retries:

```bash
php artisan vendor:publish --tag=condux-config
```

## Develop

```bash
php test/run.php
```

The capture logic (`ClientFactory`, `ConduxReporting`) is unit-tested against the real base SDK with a
fake transport — no network, no Laravel, no `composer install`. The `ConduxServiceProvider` is thin glue
over those units and is syntax-checked in CI. Zero dependencies beyond the base SDK and `illuminate/support`.
