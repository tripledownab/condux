<?php

declare(strict_types=1);

// Proves the emitted JSON is the Sentry store wire shape (field by field) and the transport is
// resilient, with an injected transport + sleep so there is no real network or waiting. Built like
// the JS/Python/Ruby/Go suites.

namespace Condux;

// 1. captureException emits the Sentry store shape.
[$client, $recorder] = build([['status' => 200]]);
try {
    throw new \InvalidArgumentException('boom from php');
} catch (\Throwable $e) {
    $result = $client->captureException($e);
}
check($result->ok, 'captureException reports ok on a 200');
$event = json_decode($recorder->bodies[0], true);
check((bool) preg_match('/^[0-9a-f]{32}$/', $event['event_id']), 'event_id is 32 lowercase hex');
check(is_float($event['timestamp']), 'timestamp is epoch seconds (float)');
check($event['platform'] === 'php', 'platform is php');
check($event['level'] === 'error', 'captureException level is error');
check($event['environment'] === 'test', 'environment rides the event');
check($event['release'] === '1.2.3', 'release rides the event');
$exception = $event['exception']['values'][0];
check($exception['type'] === 'InvalidArgumentException', 'exception type is the class');
check($exception['value'] === 'boom from php', 'exception value is the message');
check($exception['mechanism']['type'] === 'generic', 'mechanism type is generic');
check($exception['mechanism']['handled'] === true, 'mechanism handled is true');
$frames = $exception['stacktrace']['frames'];
check($frames !== [], 'frames are captured');
$crash = $frames[count($frames) - 1]; // oldest-first: the throw site is last
check($crash['in_app'] === true, 'the throw-site frame is in-app');
check(str_contains($crash['filename'], 'event_test.php'), 'the throw-site frame is this test file');
check($recorder->requests[0]['headers']['x-condux-auth'] === 'testkey', 'auth header carries the DSN key');
check($recorder->requests[0]['url'] === 'https://ingest.test/api/proj-uuid/store/', 'store URL is built from the DSN');

// 1b. captureException can mark an exception unhandled (framework adapters do this for uncaught errors).
[$client, $recorder] = build([['status' => 200]]);
try {
    throw new \RuntimeException('uncaught');
} catch (\Throwable $e) {
    $client->captureException($e, false);
}
$event = json_decode($recorder->bodies[0], true);
check($event['exception']['values'][0]['mechanism']['handled'] === false, 'captureException(handled: false) marks it unhandled');

// 2. captureMessage emits a message without an exception.
[$client, $recorder] = build([['status' => 200]]);
$client->captureMessage('disk almost full', Level::WARNING);
$event = json_decode($recorder->bodies[0], true);
check($event['level'] === 'warning', 'captureMessage carries the level');
check($event['message'] === 'disk almost full', 'captureMessage carries the message');
check(!isset($event['exception']), 'captureMessage has no exception');

// 3. Retries a 429, honoring Retry-After.
[$client, $recorder] = build([['status' => 429, 'headers' => ['Retry-After' => '3']], ['status' => 200]]);
$result = $client->captureMessage('hi', Level::INFO);
check($result->ok, '429 then 200 succeeds');
check($result->attempts === 2, '429 retry took two attempts');
check($recorder->delays === [3.0], '429 honored Retry-After of 3s');

// 4. Retries 5xx with capped exponential backoff.
[$client, $recorder] = build([['status' => 503], ['status' => 503], ['status' => 200]]);
$result = $client->captureMessage('hi', Level::INFO);
check($result->ok, '5xx eventually succeeds');
check($result->attempts === 3, '5xx retried to the third attempt');
check($recorder->delays === [0.2, 0.4], '5xx backoff is 200ms then 400ms');

// 5. Does not retry a client error.
[$client, $recorder] = build([['status' => 400]]);
$result = $client->captureMessage('hi', Level::INFO);
check(!$result->ok, '400 is not ok');
check($result->attempts === 1, '400 is not retried');
check($result->status === 400, '400 status is reported');
check($recorder->delays === [], '400 caused no backoff');

// 6. Reports a network error without throwing.
[$client, $recorder] = build([['error' => 'connection refused']], maxRetries: 1);
$result = $client->captureMessage('hi', Level::INFO);
check(!$result->ok, 'a network failure is not ok');
check($result->attempts === 2, 'a network failure exhausts the retries');
check($result->status === null, 'a network failure has no status');
check($result->error !== null, 'a network failure reports the error');

// 7. Rejects a malformed DSN. Setup errors stay loud: a typo in a DSN is developer time, not runtime.
$threw = false;
try {
    new Client(dsn: 'https://ingest.test/no-key');
} catch (\InvalidArgumentException) {
    $threw = true;
}
check($threw, 'a keyless DSN is rejected');

// 8. Capture never throws, whatever the event holds. An exception message carrying invalid UTF-8 cannot
// be serialized, and throwing here would turn a handled error into an unhandled one in the very code
// path that is already dealing with a failure. This is the first capture in the suite that fails to
// serialize, so it also proves the warning is logged once rather than per dropped event.
$log = tempnam(sys_get_temp_dir(), 'condux-warn-');
$previousLog = ini_get('error_log');
ini_set('error_log', $log);

[$client, $recorder] = build([['status' => 200]]);
$result = $client->captureException(new \RuntimeException("bad bytes: \xB1\x31"));
$client->captureMessage("more bad bytes: \xB1\x31", Level::INFO);

ini_set('error_log', $previousLog === false ? '' : $previousLog);
check(!$result->ok, 'an unserializable event reports a failure instead of throwing');
check($result->error !== null, 'the failure carries the reason');
check($recorder->bodies === [], 'an unserializable event never reaches the transport');
check(substr_count((string) file_get_contents($log), 'Condux:') === 1, 'the drop warning is logged once, not once per event');
unlink($log);
