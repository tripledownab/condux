using Condux.Storage.Postgres;
using Condux.Telemetry;
using Microsoft.Extensions.Configuration;

namespace Condux.ControlPlane.Endpoints;

/// <summary>Health/readiness/version probes — excluded from the API description (not client-facing).</summary>
internal static class OperationalEndpoints
{
    public static void MapOperationalEndpoints(this IEndpointRouteBuilder app, IConfiguration cfg)
    {
        var version = Environment.GetEnvironmentVariable("CONDUX_VERSION") ?? "0.0.0-dev";
        var commit = Environment.GetEnvironmentVariable("CONDUX_COMMIT") ?? "unknown";

        // Liveness: is the process answering. Deliberately checks nothing, because a container probe that
        // fails on a store outage restarts a healthy process and makes the outage worse.
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
        app.MapGet("/readyz", () => Results.Ok(new { status = "ready" })).ExcludeFromDescription();
        app.MapGet("/version", () => Results.Ok(new { version, commit })).ExcludeFromDescription();

        // Readiness for an external monitor, under /api/ because that is the only prefix the edge routes
        // to this service: /healthz on the public host reaches the dashboard and answers 404, so nothing
        // outside the box could probe this service at all before now.
        //
        // Unlike liveness, this checks the stores. A control-plane that has lost Postgres serves errors
        // to every request while still answering "ok", so a monitor watching liveness alone agrees with
        // the outage rather than catching it. 503 when a store is unreachable, which is what a monitor
        // reads as down.
        app.MapGet("/api/readyz",
                async (HttpContext http, IHttpClientFactory clients) =>
                {
                    var postgres = await StoreReadiness.PostgresAsync(
                        cfg.Require("CONDUX_POSTGRES"), http.RequestAborted);
                    var clickhouse = await StoreReadiness.ClickHouseAsync(
                        clients.CreateClient("readiness"),
                        cfg.Require("CONDUX_CLICKHOUSE_URL"),
                        cfg.Require("CONDUX_CLICKHOUSE_USER"),
                        cfg.Require("CONDUX_CLICKHOUSE_PASSWORD"),
                        http.RequestAborted);

                    // status is one token meaning every check passed, so a monitor can alert on its
                    // absence — which is how most of them work — without enumerating the checks. Matching
                    // on the individual booleans instead is brittle both ways: expecting "true" still
                    // passes when one of two stores is down, and forbidding "false" starts crying wolf the
                    // day a field is added that is legitimately false.
                    //
                    // The per-store booleans stay, because once alerted an operator needs to know which
                    // one to look at, not merely that something is wrong.
                    var ready = postgres && clickhouse;
                    var body = new
                    {
                        status = ready ? "ready" : "degraded",
                        postgres,
                        clickhouse,
                        version,
                        commit,
                    };
                    return ready
                        ? Results.Ok(body)
                        : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
                })
            .ExcludeFromDescription();
    }
}
