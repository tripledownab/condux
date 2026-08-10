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

## Develop

```bash
php test/run.php
```

Zero runtime dependencies (`ext-curl` + `ext-json`, both standard). The transport, sleep, and clock are
injectable (constructor `transport:`, `sleep:`, `clock:`), so the tests exercise the retry/backoff with
no real network or timers.
