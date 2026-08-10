<?php

declare(strict_types=1);

namespace Condux\Laravel;

use Condux\Client;
use Illuminate\Contracts\Debug\ExceptionHandler;
use Illuminate\Support\ServiceProvider;

/**
 * Registers the Condux Laravel integration. Auto-discovered via composer `extra.laravel.providers`, so a
 * host app only needs to `composer require condux/condux-laravel` and set `CONDUX_DSN`. Thin glue over the
 * unit-tested {@see ClientFactory} and {@see ConduxReporting}.
 */
final class ConduxServiceProvider extends ServiceProvider
{
    public function register(): void
    {
        $this->mergeConfigFrom(__DIR__ . '/../config/condux.php', 'condux');
    }

    public function boot(): void
    {
        if ($this->app->runningInConsole()) {
            $this->publishes([__DIR__ . '/../config/condux.php' => $this->app->configPath('condux.php')], 'condux-config');
            $this->commands([TestCommand::class]);
        }

        $config = $this->app['config']->get('condux', []);
        $client = ClientFactory::tryFromConfig(is_array($config) ? $config : []);
        if ($client === null) {
            return; // no DSN configured — the install stays inert until CONDUX_DSN is set
        }

        $this->app->instance(Client::class, $client);
        ConduxReporting::register($client, $this->app->make(ExceptionHandler::class));
    }
}
