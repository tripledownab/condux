<?php

declare(strict_types=1);

namespace Condux\Laravel;

use Condux\CaptureContext;
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
        $config = is_array($config) ? $config : [];

        // The container always resolves a Client, so code that type-hints one keeps working before a DSN
        // is set (it would otherwise fail to resolve, turning "monitoring is off" into a broken app).
        $client = ClientFactory::fromConfig($config);
        $this->app->instance(Client::class, $client);
        if (ClientFactory::dsn($config) === null) {
            return; // no DSN configured — the bound client drops events until CONDUX_DSN is set
        }

        // The Laravel-aware half: resolve the current request lazily, at report time, so there is no
        // request to hold onto during boot and a console command reports with no request at all.
        // Headers are available here and deliberately not read.
        ConduxReporting::register(
            $client,
            $this->app->make(ExceptionHandler::class),
            function (): ?CaptureContext {
                $request = $this->app->bound('request') ? $this->app->make('request') : null;
                if ($request === null || !method_exists($request, 'getRequestUri')) {
                    return null;
                }

                return CaptureContext::forRequest($request->getRequestUri(), $request->getMethod());
            }
        );
    }
}
