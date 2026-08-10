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
stays inert, so it is safe to install ahead of configuring it.

Verify the setup:

```bash
php artisan condux:test
```

To capture something manually, resolve the client:

```php
app(\Condux\Client::class)->captureMessage('cache miss storm', \Condux\Level::WARNING);
```

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
