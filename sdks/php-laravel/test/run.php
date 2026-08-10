<?php

declare(strict_types=1);

// Verifies the framework-agnostic logic (config -> Client, and the reportable hook that captures) against
// the REAL base Condux SDK with a fake transport, so there is no network and no Laravel needed. The
// ServiceProvider is thin glue over these two units (and is syntax-checked separately in CI). A plain-PHP
// harness so the sdk-php-laravel CI job needs only PHP — no composer install, no PHPUnit.

namespace Condux\Laravel;

// The base SDK (monorepo sibling) + this package's testable units.
foreach (['Level', 'SendResult', 'Dsn', 'EventPayload', 'EventTransport', 'Client'] as $class) {
    require __DIR__ . "/../../php/src/$class.php";
}
require __DIR__ . '/../src/ClientFactory.php';
require __DIR__ . '/../src/ConduxReporting.php';

use Condux\Client;

$failures = 0;
$checks = 0;

function check(bool $condition, string $message): void
{
    global $failures, $checks;
    ++$checks;
    if (!$condition) {
        ++$failures;
        fwrite(STDERR, "FAIL: $message\n");
    }
}

// 1. A DSN config builds a Client; a missing/blank DSN stays inert (no crash on install).
check(
    ClientFactory::tryFromConfig(['dsn' => 'https://k@ingest.test/1', 'environment' => 'prod', 'release' => '1.0.0']) instanceof Client,
    'a DSN config builds a Client',
);
check(ClientFactory::tryFromConfig([]) === null, 'missing DSN yields no client (inert install)');
check(ClientFactory::tryFromConfig(['dsn' => '   ']) === null, 'a blank DSN yields no client');

// 2. ConduxReporting registers a reportable callback that captures exceptions through the base SDK.
$captured = [];
$client = new Client(
    dsn: 'https://testkey@ingest.test/proj',
    transport: static function (string $url, array $headers, string $body) use (&$captured): array {
        $captured[] = ['url' => $url, 'headers' => $headers, 'body' => $body];

        return [200, []];
    },
);

// A stand-in for Laravel's exception handler: records the reportable callback it is given.
$handler = new class {
    /** @var callable|null */
    public $callback = null;

    public function reportable(callable $callback): void
    {
        $this->callback = $callback;
    }
};

ConduxReporting::register($client, $handler);
check($handler->callback !== null, 'a reportable callback is registered on the handler');

($handler->callback)(new \InvalidArgumentException('laravel boom'));
check(count($captured) === 1, 'reporting an exception sends exactly one event');
$event = json_decode($captured[0]['body'], true);
check(($event['level'] ?? '') === 'error', 'the captured event is error level');
$exception = $event['exception']['values'][0] ?? [];
check(($exception['type'] ?? '') === 'InvalidArgumentException', 'the captured exception type is preserved');
check(($exception['value'] ?? '') === 'laravel boom', 'the captured exception message is preserved');
check(($exception['mechanism']['handled'] ?? null) === false, 'the exception is reported unhandled (it reached the framework handler)');
check(($captured[0]['headers']['x-condux-auth'] ?? '') === 'testkey', 'it authenticates with the DSN key');
check($captured[0]['url'] === 'https://ingest.test/api/proj/store/', 'it posts to the configured relay');

// 3. A handler without reportable() (Laravel < 8 or a foreign impl) is a safe no-op.
$plain = new class {
    public bool $touched = false;
};
ConduxReporting::register($client, $plain);
check($plain->touched === false, 'a handler without reportable() is a no-op, never crashes the app');

echo "$checks checks, $failures failures\n";
exit($failures === 0 ? 0 : 1);
