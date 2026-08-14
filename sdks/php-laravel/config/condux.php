<?php

declare(strict_types=1);

use Condux\Laravel\ClientFactory;

// Published to config/condux.php. Leave CONDUX_DSN unset to keep the package inert (no reporting).
return [
    // The project DSN, e.g. https://<key>@ingest.condux.ai/<projectId>.
    'dsn' => env('CONDUX_DSN'),

    // Tags every event with the deployment environment (defaults to Laravel's APP_ENV).
    'environment' => env('CONDUX_ENVIRONMENT', env('APP_ENV')),

    // The release/version this deploy is running, surfaced on issues when set.
    'release' => env('CONDUX_RELEASE'),

    // Delivery attempts after the first (429 / 5xx / network are retried with backoff). Left as the raw
    // env value on purpose: a blank CONDUX_MAX_RETRIES= reaches env() as "", and casting that to int here
    // would silently mean "no retries". ClientFactory validates it and falls back to the default.
    'max_retries' => env('CONDUX_MAX_RETRIES', ClientFactory::DEFAULT_MAX_RETRIES),
];
