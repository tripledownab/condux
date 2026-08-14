# Condux SDK for PHP

Report errors from a PHP app to a Condux relay. Emits the Sentry "store" wire shape, so the relay
normalizes it exactly like an official Sentry SDK — point it at a project DSN and it works.

Delivery is resilient (429 / 5xx / network failures retry with backoff, honoring `Retry-After`) and
**never throws** — a failed send returns a `SendResult`, it does not crash the caller.

## Install

```bash
composer require condux/condux
```

## Usage

```php
<?php

use Condux\Client;
use Condux\Level;

$condux = new Client(
    dsn: 'https://<key>@ingest.condux.ai/<projectId>',
    environment: 'production',
    release: '1.4.2',
);

try {
    do_work();
} catch (\Throwable $e) {
    $condux->captureException($e); // captures the exception's stack trace
    throw $e;
}

// or a bare message
$condux->captureMessage('cache miss storm', Level::WARNING);
```

## Verify it works

An error monitor's failure mode is silence, and silence looks like health. Prove the pipeline before
waiting for a real error:

```bash
CONDUX_DSN=https://<key>@ingest.condux.ai/<projectId> vendor/bin/condux test-event
```

Delivers one info-level test message through the real client and transport and prints the outcome
(exit 0 delivered, 1 delivery failed, 2 usage error — so it can gate a deploy script).

## Users, tags, contexts and breadcrumbs

```php
use Condux\Level;
use Condux\Scope;

Scope::setUser(['id' => 'u-1', 'email' => 'person@example.com']); // setUser(null) on sign-out
Scope::setTag('plan', 'business');                                // a null value removes a tag
Scope::setContext('subscription', ['seats' => 12]);
Scope::addBreadcrumb('job started', category: 'worker', level: Level::INFO);
```

Everything set here rides every subsequent event; the relay scrubs it at ingest and derives the
pseudonymous users-affected count from the user fields. The trail keeps the newest 30 breadcrumbs.

The scope is static, which is what makes it ambient — the code that sets a user does not have to reach
the client that reports. PHP tears the process down between requests, so it resets on its own; in a
long-lived worker (queue, Octane) call `Scope::clear()` between jobs.

## Develop

```bash
php test/run.php
```

Zero runtime dependencies (`ext-curl` + `ext-json`, both standard). The transport, sleep, and clock are
injectable (constructor `transport:`, `sleep:`, `clock:`), so the tests exercise the retry/backoff with
no real network or timers. `test/run.php` loads every `test/*_test.php` suite and reports the total.
