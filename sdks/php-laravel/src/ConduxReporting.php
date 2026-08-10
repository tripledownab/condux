<?php

declare(strict_types=1);

namespace Condux\Laravel;

use Condux\Client;

/**
 * Wires Condux into Laravel's exception reporting pipeline. Framework-agnostic on purpose (it takes the
 * handler as a plain object) so the capture behavior is unit-tested without booting Laravel.
 */
final class ConduxReporting
{
    private function __construct()
    {
    }

    /**
     * Register a Condux capture as a reportable callback on Laravel's exception handler. A no-op when the
     * handler predates reportable callbacks (Laravel < 8) or is a foreign implementation, so installing
     * the package never breaks a host app's error reporting. The callback does not stop Laravel's own
     * logging — Condux reporting is additive. Reported exceptions are marked unhandled: they propagated to
     * the framework's handler rather than being caught (this drives Condux's "unhandled" badge).
     */
    public static function register(Client $client, object $exceptionHandler): void
    {
        if (!method_exists($exceptionHandler, 'reportable')) {
            return;
        }

        $exceptionHandler->reportable(static function (\Throwable $error) use ($client): void {
            $client->captureException($error, handled: false);
        });
    }
}
