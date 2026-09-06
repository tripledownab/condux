<?php

declare(strict_types=1);

// Runtime dependency inventory (ADR-0041). The relay reads a top-level "modules" string map, so the
// wire key is asserted directly: a renamed or nested key would be dropped silently by the parser
// rather than rejected. An event that carries no inventory must keep its exact previous shape.

namespace Condux;

/**
 * A client whose clock the test controls, so the repeat interval is testable without waiting.
 *
 * @param list<array<string,mixed>> $responses
 * @return array{0:Client,1:Recorder}
 */
function modulesClient(array $responses, callable $clock, bool $sendModules = true): array
{
    $recorder = new Recorder($responses);
    $client = new Client(
        dsn: DSN,
        environment: 'test',
        release: '1.2.3',
        maxRetries: 3,
        transport: $recorder->transport(),
        sleep: $recorder->sleep(),
        clock: $clock,
        sendModules: $sendModules,
    );

    return [$client, $recorder];
}

/** @return array<string,mixed> */
function eventAt(Recorder $recorder, int $index): array
{
    return json_decode($recorder->bodies[$index], true);
}

$ok = [['status' => 202]];

// Declared modules ride the wire under the Sentry top-level key, sorted.
Modules::reset();
[$client, $recorder] = modulesClient($ok, static fn (): float => 1_000_000.0, sendModules: false);
Modules::set(['monolog/monolog' => '2.3.0', 'acme/lib' => '1.0.0']);
$client->captureMessage('hi', Level::INFO);
$event = eventAt($recorder, 0);
check(
    ($event['modules'] ?? null) === ['acme/lib' => '1.0.0', 'monolog/monolog' => '2.3.0'],
    'declared modules ride the wire sorted, under the top-level modules key'
);

// An event with no inventory keeps its exact previous shape.
Modules::reset();
[$client, $recorder] = modulesClient($ok, static fn (): float => 1_000_000.0, sendModules: false);
$client->captureMessage('hi', Level::INFO);
check(!array_key_exists('modules', eventAt($recorder, 0)), 'no modules key when none were declared');

// The interval is what makes the feature affordable: the server deduplicates the inventory to one row
// per package per day, so repeating the map on every event spends bytes for nothing.
Modules::reset();
$now = 1_000_000.0;
[$client, $recorder] = modulesClient($ok, static function () use (&$now): float { return $now; }, sendModules: false);
Modules::set(['acme/lib' => '1.0.0']);
$client->captureMessage('one', Level::INFO);
$client->captureMessage('two', Level::INFO);
$client->captureMessage('three', Level::INFO);
$carrying = 0;
foreach ($recorder->bodies as $body) {
    if (array_key_exists('modules', json_decode($body, true))) {
        ++$carrying;
    }
}
check($carrying === 1, 'the inventory rides the first event only, until the interval elapses');

// Repeating matters as much as skipping: the event carrying the inventory can be dropped by a rate
// limit before anything parses it, so one attempt per process would lose that day's inventory.
$now += Modules::INTERVAL_SECONDS;
$client->captureMessage('later', Level::INFO);
check(
    array_key_exists('modules', eventAt($recorder, 3)),
    'the inventory rides again once the interval has elapsed'
);

// A fresh declaration is news and does not wait out the previous interval.
Modules::reset();
Modules::set(['acme/lib' => '1.0.0']);
check(Modules::fields(500.0) !== [], 'the first call carries the inventory');
Modules::set(['acme/lib' => '2.0.0']);
check(
    (Modules::fields(501.0)['modules'] ?? null) === ['acme/lib' => '2.0.0'],
    'a fresh declaration is sent without waiting out the previous interval'
);

// Blank entries are dropped rather than reported as versions.
Modules::reset();
Modules::set(['good' => '1.0.0', 'blank' => '', '' => '2.0.0']);
check(
    (Modules::fields(1.0)['modules'] ?? null) === ['good' => '1.0.0'],
    'blank names and versions are dropped rather than reported'
);

// Clearing removes the key entirely.
Modules::reset();
Modules::set(['acme/lib' => '1.0.0']);
Modules::set(null);
check(Modules::fields(1.0) === [], 'clearing removes the modules key entirely');

// Collection degrades honestly. This harness runs without a Composer autoloader, so the runtime API is
// absent and the answer is an empty inventory rather than a fatal error. That is the same path an
// application packaged without Composer takes, and unknown is the truthful answer there.
check(
    !class_exists(\Composer\InstalledVersions::class) ? Modules::collect() === [] : Modules::collect() !== [],
    'collect returns empty without the Composer runtime API, and a real inventory with it'
);

// The client wires collection in: with sendModules on, the constructor declares whatever collect found.
// Under this harness that is nothing, so the observable contract is that it does not throw and leaves
// no stale inventory behind from a previous client.
Modules::reset();
Modules::set(['stale/pkg' => '9.9.9']);
[$client, $recorder] = modulesClient($ok, static fn (): float => 1_000_000.0, sendModules: false);
$client->captureMessage('hi', Level::INFO);
check(
    !array_key_exists('modules', eventAt($recorder, 0)),
    'constructing with sendModules off clears an inventory a previous client declared'
);

// --- The collection path itself -------------------------------------------------------------------
//
// Everything above drives Modules::set directly. None of it reaches Modules::collect, because this
// harness has no Composer autoloader, so class_exists is false and collect returns immediately. That
// left the whole of the actual collection untested. The stub supplies the runtime API so the real path
// runs; it must be reset at the end, since later suites construct clients of their own.

require __DIR__ . '/composer_stub.php';

\Composer\InstalledVersions::$packages = [
    'monolog/monolog' => '2.3.0',
    'acme/lib' => '1.0.0',
    'acme/blank' => '',      // a package with no resolvable version
    'acme/null' => null,     // getPrettyVersion legitimately returns null for a package without one
];

$collected = Modules::collect();
check(
    $collected === ['monolog/monolog' => '2.3.0', 'acme/lib' => '1.0.0'],
    'collect reads Composer installed set and drops entries with no usable version'
);

// The opt-out, now that collection can actually return something. Before the stub this check could not
// fail: collect always returned an empty array, so sendModules true and false were indistinguishable.
Modules::reset();
[$client, $recorder] = modulesClient([['status' => 202]], static fn (): float => 1_000_000.0, sendModules: false);
$client->captureMessage('hi', Level::INFO);
check(
    !array_key_exists('modules', eventAt($recorder, 0)),
    'sendModules false attaches nothing even when Composer has an inventory to report'
);

// And the other side of it: on by default, the constructor collects and the event carries it.
Modules::reset();
[$client, $recorder] = modulesClient([['status' => 202]], static fn (): float => 1_000_000.0);
$client->captureMessage('hi', Level::INFO);
check(
    (eventAt($recorder, 0)['modules'] ?? null) === ['acme/lib' => '1.0.0', 'monolog/monolog' => '2.3.0'],
    'a client with the inventory on carries what Composer reports, sorted'
);

// Reset so the suites after this one see no inventory, as they did before the stub was loaded.
\Composer\InstalledVersions::$packages = [];
Modules::reset();
