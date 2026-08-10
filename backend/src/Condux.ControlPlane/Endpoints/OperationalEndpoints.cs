namespace Condux.ControlPlane.Endpoints;

/// <summary>Health/readiness/version probes — excluded from the API description (not client-facing).</summary>
internal static class OperationalEndpoints
{
    public static void MapOperationalEndpoints(this IEndpointRouteBuilder app)
    {
        var version = Environment.GetEnvironmentVariable("CONDUX_VERSION") ?? "0.0.0-dev";
        var commit = Environment.GetEnvironmentVariable("CONDUX_COMMIT") ?? "unknown";

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
        app.MapGet("/readyz", () => Results.Ok(new { status = "ready" })).ExcludeFromDescription();
        app.MapGet("/version", () => Results.Ok(new { version, commit })).ExcludeFromDescription();
    }
}
