<?php

declare(strict_types=1);

// Verifies the framework-agnostic logic (config -> Client, and the reportable hook that captures) against
// the REAL base Condux SDK with a fake transport, so there is no network and no Laravel needed. The
// ServiceProvider is thin glue over these two units (and is syntax-checked separately in CI). A plain-PHP
// harness so the sdk-php-laravel CI job needs only PHP — no composer install, no PHPUnit.

namespace Condux\Laravel;

// The base SDK (monorepo sibling) + this package's testable units.
require __DIR__ . '/../../php/src/autoload.php';
require __DIR__ . '/../src/ClientFactory.php';
require __DIR__ . '/../src/ConduxReporting.php';

use Condux\Client;
use Condux\Level;

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

// 1. Whether the package reports at all is read off the config's DSN — the signal the service provider
// and `condux:test` both branch on.
check(ClientFactory::dsn(['dsn' => ' https://k@ingest.test/1 ']) === 'https://k@ingest.test/1', 'the configured DSN is trimmed');
check(ClientFactory::dsn(['dsn' => '   ']) === null, 'a blank DSN reads as unconfigured');
check(ClientFactory::dsn([]) === null, 'a missing DSN reads as unconfigured');

// 1b. The container gets a Client either way: an app that type-hints one must resolve it before a DSN is
// set, and capture on that client is a local no-op rather than a send to nowhere.
check(
    ClientFactory::fromConfig(['dsn' => 'https://k@ingest.test/1', 'environment' => 'prod', 'release' => '1.0.0']) instanceof Client,
    'a DSN config builds a Client',
);
$inert = ClientFactory::fromConfig([]);
check($inert instanceof Client, 'a config with no DSN still builds a Client to bind');
$dropped = $inert->captureMessage('nobody is listening', Level::INFO);
check(!$dropped->ok, 'an inert capture reports a failure instead of pretending to deliver');
check($dropped->attempts === 1, 'an inert capture makes one local attempt and never retries');
check(str_contains((string) $dropped->error, 'no DSN configured'), 'an inert capture is dropped locally, not sent anywhere');

// 1c. max_retries comes from the environment as a string, so a blank or junk value means "unset", not
// "one attempt, no retries". Attempts are counted against a port that refuses immediately.
$refused = 'http://key@127.0.0.1:9/1';
check(ClientFactory::fromConfig(['dsn' => $refused])->captureMessage('x', Level::INFO)->attempts === 4, 'an absent max_retries falls back to the default of 3 retries');
check(ClientFactory::fromConfig(['dsn' => $refused, 'max_retries' => ''])->captureMessage('x', Level::INFO)->attempts === 4, 'a blank CONDUX_MAX_RETRIES falls back to the default');
check(ClientFactory::fromConfig(['dsn' => $refused, 'max_retries' => 0])->captureMessage('x', Level::INFO)->attempts === 1, 'an explicit zero really means no retries');
check(ClientFactory::fromConfig(['dsn' => $refused, 'max_retries' => '0'])->captureMessage('x', Level::INFO)->attempts === 1, 'a numeric string from the environment is honored');

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
