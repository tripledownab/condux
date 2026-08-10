<?php

declare(strict_types=1);

// Proves the emitted JSON is the Sentry store wire shape (field by field) and the transport is
// resilient, with an injected transport + sleep so there is no real network or waiting. Mirrors the
// JS/Python/Ruby/Go suites. A plain-PHP harness so CI needs only PHP (no PHPUnit / composer install).

namespace Condux;

foreach (['Level', 'SendResult', 'Dsn', 'EventPayload', 'EventTransport', 'Client'] as $class) {
    require __DIR__ . "/../src/$class.php";
}

const DSN = 'https://testkey@ingest.test/proj-uuid';

/**
 * Replays a scripted sequence of responses (the last repeats), records requests + bodies + backoff
 * delays. A response ['error' => msg] makes the transport throw, simulating a network failure.
 */
final class Recorder
{
    /** @var list<array{url:string,headers:array<string,string>}> */
    public array $requests = [];
    /** @var list<string> */
    public array $bodies = [];
    /** @var list<float> */
    public array $delays = [];

    /** @param list<array<string,mixed>> $responses */
    public function __construct(private array $responses)
    {
    }

    public function transport(): \Closure
    {
        return function (string $url, array $headers, string $body): array {
            $this->requests[] = ['url' => $url, 'headers' => $headers];
            $this->bodies[] = $body;
            $response = $this->responses[min(count($this->requests) - 1, count($this->responses) - 1)];
            if (isset($response['error'])) {
                throw new \RuntimeException($response['error']);
            }

            return [$response['status'], $response['headers'] ?? []];
        };
    }

    public function sleep(): \Closure
    {
        return function (float $seconds): void {
            $this->delays[] = $seconds;
        };
    }
}

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

/** @param list<array<string,mixed>> $responses */
function build(array $responses, int $maxRetries = 3): array
{
    $recorder = new Recorder($responses);
    $client = new Client(
        dsn: DSN,
        environment: 'test',
        release: '1.2.3',
        maxRetries: $maxRetries,
        transport: $recorder->transport(),
        sleep: $recorder->sleep(),
        clock: static fn (): float => 1_700_000_000.5,
    );

    return [$client, $recorder];
}

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
check(str_contains($crash['filename'], 'run.php'), 'the throw-site frame is this test file');
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

// 7. Rejects a malformed DSN.
$threw = false;
try {
    new Client(dsn: 'https://ingest.test/no-key');
} catch (\InvalidArgumentException) {
    $threw = true;
}
check($threw, 'a keyless DSN is rejected');

echo "$checks checks, $failures failures\n";
exit($failures === 0 ? 0 : 1);
