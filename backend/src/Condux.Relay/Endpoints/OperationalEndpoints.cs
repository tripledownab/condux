using Condux.Core.Messaging;
using Condux.Messaging;
using Condux.Relay.Setup;
using Condux.Storage.Postgres;

namespace Condux.Relay.Endpoints;

/// <summary>The relay's liveness and readiness probes. They answer different questions on purpose, and
/// mixing them is what makes an outage invisible.</summary>
internal static class OperationalEndpoints
{
    public static void MapOperationalEndpoints(this WebApplication app, RelayOptions options)
    {
        // Liveness: is the process answering. Deliberately checks nothing, because a container probe that
        // fails on a dependency outage restarts a healthy process and makes the outage worse.
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        // Readiness for an uptime monitor. The relay is the one service whose outage loses data rather
        // than merely blocking a page: it answers an SDK "accepted" and then has nowhere to put the event.
        // Liveness alone would report healthy through exactly that, so this checks what ingest actually
        // needs, the broker it publishes to and the catalog it authenticates DSNs against, and answers 503
        // when either is gone.
        app.MapGet("/readyz", async (IEventPublisher publisher, CancellationToken ct) =>
        {
            var broker = publisher is KafkaEventPublisher kafka
                ? kafka.CanReachBroker(StoreReadiness.Timeout)
                : true;

            // Postgres is optional here: without it the relay falls back to the seeded dev store, which is
            // a working configuration rather than a fault, so it is only required when it is configured.
            var catalog = string.IsNullOrEmpty(options.Postgres)
                || await StoreReadiness.PostgresAsync(options.Postgres, ct);

            // Same single token as the control-plane's, so one monitor rule covers both services.
            var ready = broker && catalog;
            var body = new { status = ready ? "ready" : "degraded", broker, catalog };
            return ready
                ? Results.Ok(body)
                : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
    }
}
