<?php

declare(strict_types=1);

namespace Condux;

/**
 * `condux test-event` — prove the pipeline end to end.
 *
 * An error monitor's failure mode is silence, and silence looks exactly like health. Run sends one
 * info-level message through the real client and transport and reports the delivery outcome, so "did my
 * DSN / network / relay work" is one command instead of waiting for a production error.
 */
final class TestEvent
{
    public const USAGE = 'Usage: condux test-event [--dsn <dsn>] [--message <text>]';

    private function __construct()
    {
    }

    /**
     * Runs the command and returns the process exit code: 0 delivered, 1 delivery failed, 2 usage error.
     * The streams are parameters so the tests read what a run printed.
     *
     * @param list<string>     $args the arguments after the program name
     * @param resource         $out
     * @param resource         $err
     */
    public static function run(array $args, $out, $err): int
    {
        $command = $args[0] ?? '';
        if ($command !== 'test-event') {
            return self::usage($err, "unknown command '$command'");
        }

        $dsn = self::argument($args, '--dsn') ?? getenv('CONDUX_DSN');
        if (!is_string($dsn) || trim($dsn) === '') {
            return self::usage($err, 'no DSN. Pass --dsn <dsn> or set CONDUX_DSN');
        }

        try {
            $client = new Client(dsn: trim($dsn), environment: 'condux-test');
        } catch (\InvalidArgumentException $e) {
            return self::usage($err, $e->getMessage());
        }

        $message = self::argument($args, '--message') ?? 'Condux test event';

        return self::report($client->captureMessage($message, Level::INFO), $message, $out, $err);
    }

    /**
     * @param resource $out
     * @param resource $err
     */
    private static function report(SendResult $result, string $message, $out, $err): int
    {
        $attempts = $result->attempts . ' attempt' . ($result->attempts === 1 ? '' : 's');
        if ($result->ok) {
            fwrite($out, "Delivered \"$message\" ($attempts). Check your project's issues list; a test "
                . "message appears as an info-level issue.\n");

            return 0;
        }

        $reason = $result->error ?? 'relay answered ' . $result->status;
        fwrite($err, "Delivery FAILED after $attempts: $reason. Check the DSN (Project settings -> DSN "
            . "keys) and that the ingest host is reachable.\n");

        return 1;
    }

    /** @param list<string> $args */
    private static function argument(array $args, string $name): ?string
    {
        $index = array_search($name, $args, true);

        return $index === false ? null : ($args[$index + 1] ?? null);
    }

    /** @param resource $err */
    private static function usage($err, string $reason): int
    {
        fwrite($err, 'condux: ' . $reason . '. ' . self::USAGE . "\n");

        return 2;
    }
}
