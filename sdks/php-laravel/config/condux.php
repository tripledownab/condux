<?php

declare(strict_types=1);

// Published to config/condux.php. Leave CONDUX_DSN unset to keep the package inert (no reporting).
return [
    // The project DSN, e.g. https://<key>@ingest.condux.ai/<projectId>.
    'dsn' => env('CONDUX_DSN'),

    // Tags every event with the deployment environment (defaults to Laravel's APP_ENV).
    'environment' => env('CONDUX_ENVIRONMENT', env('APP_ENV')),

    // The release/version this deploy is running, surfaced on issues when set.
    'release' => env('CONDUX_RELEASE'),

    // Delivery attempts after the first (429 / 5xx / network are retried with backoff).
    'max_retries' => (int) env('CONDUX_MAX_RETRIES', 3),
];
