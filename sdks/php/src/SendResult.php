<?php

declare(strict_types=1);

namespace Condux;

/**
 * Outcome of a delivery attempt sequence. Never thrown — inspect ->ok.
 */
final class SendResult
{
    public function __construct(
        public readonly bool $ok,
        public readonly int $attempts,
        public readonly ?int $status = null,
        public readonly ?string $error = null,
    ) {
    }
}
