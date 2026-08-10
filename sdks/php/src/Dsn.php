<?php

declare(strict_types=1);

namespace Condux;

/** A parsed DSN: the relay endpoint, the project id path segment, and the public key. */
final class Dsn
{
    public function __construct(
        public readonly string $endpoint,
        public readonly string $projectId,
        public readonly string $publicKey,
    ) {
    }

    public static function parse(string $dsn): self
    {
        $parts = parse_url($dsn);
        if ($parts === false || !isset($parts['scheme'], $parts['host'], $parts['user'])) {
            throw new \InvalidArgumentException('Condux: DSN must be scheme://<key>@<host>/<projectId>');
        }

        $endpoint = $parts['scheme'] . '://' . $parts['host'];
        if (isset($parts['port'])) {
            $endpoint .= ':' . $parts['port'];
        }
        $projectId = ltrim($parts['path'] ?? '', '/');
        if ($projectId === '') {
            throw new \InvalidArgumentException('Condux: DSN is missing the project id path segment');
        }

        return new self($endpoint, $projectId, $parts['user']);
    }

    /** The Sentry store endpoint an event is POSTed to. */
    public function storeUrl(): string
    {
        return "{$this->endpoint}/api/{$this->projectId}/store/";
    }
}
