<?php

declare(strict_types=1);

namespace Condux;

/**
 * Event severity, matching the levels the relay understands. The wire value is the lowercase string.
 */
final class Level
{
    public const DEBUG = 'debug';
    public const INFO = 'info';
    public const WARNING = 'warning';
    public const ERROR = 'error';
    public const FATAL = 'fatal';
}
