<?php

declare(strict_types=1);

namespace Condux\Laravel;

use Condux\Client;

/**
 * Builds a {@see Client} from the resolved `config/condux.php` array. Returns null when no DSN is
 * configured, so installing the package without a DSN leaves it inert instead of throwing at boot.
 */
final class ClientFactory
{
    private function __construct()
    {
    }

    /** @param array<string,mixed> $config */
    public static function tryFromConfig(array $config): ?Client
    {
        $dsn = is_string($config['dsn'] ?? null) ? trim($config['dsn']) : '';
        if ($dsn === '') {
            return null;
        }

        return new Client(
            dsn: $dsn,
            environment: self::stringOrNull($config['environment'] ?? null),
            release: self::stringOrNull($config['release'] ?? null),
            maxRetries: is_int($config['max_retries'] ?? null) ? $config['max_retries'] : 3,
        );
    }

    private static function stringOrNull(mixed $value): ?string
    {
        return is_string($value) && $value !== '' ? $value : null;
    }
}
