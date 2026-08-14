<?php

declare(strict_types=1);

// Shared scaffolding for the suites run.php loads: the SDK itself, a recording transport, and the check
// counter. A plain-PHP harness so CI needs only PHP (no PHPUnit / composer install).

namespace Condux;

require __DIR__ . '/../src/autoload.php';

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

/** The event one capture put on the wire, decoded. */
function captureWith(callable $capture): array
{
    [$client, $recorder] = build([['status' => 200]]);
    $capture($client);

    return json_decode($recorder->bodies[0], true);
}
