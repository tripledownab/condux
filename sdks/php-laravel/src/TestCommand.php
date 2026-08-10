<?php

declare(strict_types=1);

namespace Condux\Laravel;

use Condux\Client;
use Condux\Level;
use Illuminate\Console\Command;

/**
 * `php artisan condux:test` — send a test event to the relay to verify the DSN and connectivity, and
 * report the outcome. Mirrors Sentry's `sentry:test`.
 */
final class TestCommand extends Command
{
    protected $signature = 'condux:test';
    protected $description = 'Send a test event to Condux to verify the DSN and connectivity.';

    public function handle(): int
    {
        if (!$this->laravel->bound(Client::class)) {
            $this->error('Condux is not configured — set CONDUX_DSN in your environment.');

            return self::FAILURE;
        }

        $this->info('Sending a test event to Condux...');
        /** @var Client $client */
        $client = $this->laravel->make(Client::class);
        $result = $client->captureMessage('Condux test event', Level::INFO);

        if ($result->ok) {
            $this->info('Delivered — the relay accepted the event (HTTP ' . $result->status . '). Check your Condux issues.');

            return self::SUCCESS;
        }

        $detail = $result->status !== null ? 'HTTP ' . $result->status : ($result->error ?? 'unknown error');
        $this->error('Failed to deliver the test event (' . $detail . '). Check CONDUX_DSN and network access.');

        return self::FAILURE;
    }
}
