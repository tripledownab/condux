using Condux.Core.Messaging;
using Condux.Core.Plans;
using Condux.Core.Projects;
using Condux.Core.Quotas;
using Condux.Core.RateLimiting;
using Condux.Messaging;
using Condux.Storage.Postgres;
using Condux.Storage.Quotas;
using Condux.Storage.RateLimiting;
using Condux.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using StackExchange.Redis;

namespace Condux.Relay.Setup;

/// <summary>All of the relay's service wiring, so its entry point stays a readable list of what the
/// process does rather than how each dependency is built. Everything here is chosen by environment, and
/// each choice has a working default so a bare `dotnet run` still serves.</summary>
internal static class ServiceCollectionExtensions
{
    public static RelayOptions AddRelayServices(this WebApplicationBuilder builder)
    {
        var options = RelayOptions.FromEnv(builder.Configuration);

        // OpenTelemetry (traces + metrics over OTLP; opt-in via OTEL_EXPORTER_OTLP_ENDPOINT).
        builder.AddConduxTelemetry("condux-relay",
            tracing => tracing.AddAspNetCoreInstrumentation(),
            metrics => metrics.AddAspNetCoreInstrumentation());

        builder.AddRateLimiting(options);
        builder.AddProjectStore(options);

        // Instance-wide spike protection: a hard events/sec ceiling that sheds load before parsing,
        // protecting a single relay (and the pipeline) even when every project is within its own budget.
        // Rate <= 0 disables it.
        builder.Services.AddSingleton(new SpikeGuard(options.SpikeRatePerSecond, options.SpikeBurst));

        // Publish normalized events to Kafka/Redpanda. Tests override this with an in-memory publisher.
        builder.Services.AddSingleton<IEventPublisher>(_ => new KafkaEventPublisher(options.KafkaBootstrap));

        // Most stock Sentry SDKs compress the request body: sentry-java sets Content-Encoding: gzip
        // unconditionally, and sentry-dart compresses by default, so without this their events never parse.
        builder.Services.AddRequestDecompression();
        builder.WebHost.ConfigureKestrel(
            kestrel => kestrel.Limits.MaxRequestBodySize = options.MaxIngestBytes);

        // Condux on Condux (#75): opt-in self-error reporting via the Condux .NET SDK (CONDUX_SELF_DSN).
        builder.AddConduxSelfReporting();

        return options;
    }

    // Per-project rate limiter and monthly quota meter. Valkey-backed when CONDUX_VALKEY is set (one
    // budget shared across all relay replicas); otherwise in-process (dev/single-relay). The rate and
    // burst are sourced from the project's plan tier per request, not fixed here.
    private static void AddRateLimiting(this WebApplicationBuilder builder, RelayOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Valkey))
        {
            builder.Services.AddSingleton<IRateLimiter>(new InMemoryRateLimiter());
            builder.Services.AddSingleton<IQuotaMeter>(new InMemoryQuotaMeter());
            return;
        }

        builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(options.Valkey));
        builder.Services.AddSingleton<IRateLimiter>(sp =>
            new ValkeyRateLimiter(sp.GetRequiredService<IConnectionMultiplexer>()));
        builder.Services.AddSingleton<IQuotaMeter>(sp =>
            new ValkeyQuotaMeter(sp.GetRequiredService<IConnectionMultiplexer>()));
    }

    // Project/DSN auth store. Postgres-backed (behind a short-TTL cache so the hot path rarely hits the
    // DB) when CONDUX_POSTGRES is set; otherwise a seeded in-memory dev store (project "1" / key "devkey").
    private static void AddProjectStore(this WebApplicationBuilder builder, RelayOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Postgres))
        {
            builder.Services.AddSingleton<IProjectStore>(new InMemoryProjectStore(
            [
                ("1", "devkey", Tier.Free),
            ]));
            return;
        }

        builder.Services.AddSingleton<IProjectStore>(
            new CachingProjectStore(new PostgresProjectStore(options.Postgres)));
    }
}
