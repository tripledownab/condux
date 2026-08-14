<?php

declare(strict_types=1);

namespace Condux\Laravel;

use Condux\Client;

/**
 * Builds a {@see Client} from the resolved `config/condux.php` array, and knows what to do when no DSN is
 * configured: the container still binds a client (an app that type-hints one must resolve it), but that
 * client drops events locally, so installing the package without a DSN leaves it inert.
 */
final class ClientFactory
{
    /** Delivery attempts after the first, when the config does not say otherwise. */
    public const DEFAULT_MAX_RETRIES = 3;

    private function __construct()
    {
    }

    /**
     * The client to bind: a real reporter when a DSN is configured, else an inert one.
     *
     * @param array<string,mixed> $config
     */
    public static function fromConfig(array $config): Client
    {
        return self::configured($config) ?? self::inert();
    }

    /** @param array<string,mixed> $config */
    private static function configured(array $config): ?Client
    {
        $dsn = self::dsn($config);
        if ($dsn === null) {
            return null;
        }

        return new Client(
            dsn: $dsn,
            environment: self::stringOrNull($config['environment'] ?? null),
            release: self::stringOrNull($config['release'] ?? null),
            maxRetries: self::maxRetries($config['max_retries'] ?? null),
        );
    }

    /**
     * A client that drops every event instead of sending it. The DSN is a placeholder the transport never
     * reaches: dropping is what makes it inert, and it reports the reason on the SendResult rather than
     * logging, so an install without a DSN stays silent.
     */
    private static function inert(): Client
    {
        return new Client(
            dsn: 'https://unconfigured@localhost/0',
            maxRetries: 0,
            transport: static fn (): array => throw new \RuntimeException('Condux has no DSN configured; the event was dropped.'),
        );
    }

    /**
     * The configured DSN, or null when there is none.
     *
     * @param array<string,mixed> $config
     */
    public static function dsn(array $config): ?string
    {
        $dsn = is_string($config['dsn'] ?? null) ? trim($config['dsn']) : '';

        return $dsn === '' ? null : $dsn;
    }

    /**
     * An env var reaches config as a string, and a blank or non-numeric one means "not set", not "zero
     * retries" — casting it straight to int would silently turn every delivery into a single attempt.
     */
    private static function maxRetries(mixed $value): int
    {
        if (is_int($value)) {
            return max(0, $value);
        }
        if (is_string($value) && is_numeric(trim($value))) {
            return max(0, (int) trim($value));
        }

        return self::DEFAULT_MAX_RETRIES;
    }

    private static function stringOrNull(mixed $value): ?string
    {
        return is_string($value) && $value !== '' ? $value : null;
    }
}
