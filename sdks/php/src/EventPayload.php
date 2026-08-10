<?php

declare(strict_types=1);

namespace Condux;

/** Builds the Sentry "exception" payload (type / value / mechanism / stacktrace) from a Throwable. */
final class EventPayload
{
    private function __construct()
    {
    }

    /** @return array<string,mixed> */
    public static function exception(\Throwable $error, bool $handled = true): array
    {
        $exception = [
            'type' => $error::class,
            'value' => $error->getMessage(),
            // The relay reads mechanism.handled for the unhandled badge. A direct captureException is a
            // handled capture; a framework adapter reporting an uncaught exception passes handled = false.
            'mechanism' => ['type' => 'generic', 'handled' => $handled],
        ];
        $frames = self::stackFrames($error);
        if ($frames !== []) {
            $exception['stacktrace'] = ['frames' => $frames];
        }

        return $exception;
    }

    /**
     * PHP's getTrace() is newest-first and omits the throw site (which is getFile()/getLine()); prepend
     * the throw site, then reverse to oldest-first (throw site last), the order the relay's fingerprinter
     * and issue detail expect.
     *
     * @return list<array<string,mixed>>
     */
    private static function stackFrames(\Throwable $error): array
    {
        $stack = [self::frame($error->getFile(), $error->getLine(), null)];
        foreach ($error->getTrace() as $trace) {
            $function = isset($trace['class'])
                ? $trace['class'] . ($trace['type'] ?? '::') . ($trace['function'] ?? '')
                : ($trace['function'] ?? '<unknown>');
            $stack[] = self::frame($trace['file'] ?? null, $trace['line'] ?? null, $function);
        }

        return array_reverse($stack);
    }

    /** @return array<string,mixed> */
    private static function frame(?string $file, ?int $line, ?string $function): array
    {
        $frame = ['in_app' => $file !== null && self::inApp($file)];
        if ($file !== null) {
            $frame['filename'] = $file;
        }
        if ($function !== null) {
            $frame['function'] = $function;
        }
        if ($line !== null) {
            $frame['lineno'] = $line;
        }

        return $frame;
    }

    /** Application frames drive grouping + the culprit; Composer dependencies are noise. */
    private static function inApp(string $file): bool
    {
        return !str_contains($file, '/vendor/');
    }
}
