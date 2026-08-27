using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Realtime;
using Condux.Core.Auth;

namespace Condux.ControlPlane.Endpoints;

/// <summary>The live per-project Server-Sent Events stream (ADR-0030). Not part of the REST contract, so
/// it is excluded from the OpenAPI doc and the TS client, and it is the one endpoint here that holds a
/// connection open rather than answering and closing.</summary>
internal static class ProjectEventStreamEndpoints
{
    // How long a quiet stream waits before writing a heartbeat. Long enough to be cheap, short enough
    // that an idle proxy does not decide the connection is dead and close it.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    public static void MapProjectEventStreamEndpoints(this IEndpointRouteBuilder app)
    {
        // A generic "this project changed" channel the dashboard's nav badges listen on to refetch their
        // counts the instant a new or regressed issue lands, fed by the consumer's NOTIFY over Postgres
        // LISTEN/NOTIFY. The browser's EventSource speaks this directly.
        app.MapGet("/api/projects/{projectId:long}/events",
                async (long projectId, HttpContext http, ProjectEventHub hub) =>
                {
                    var ct = http.RequestAborted;
                    http.Response.Headers.ContentType = "text/event-stream";
                    http.Response.Headers.CacheControl = "no-cache";
                    http.Response.Headers["X-Accel-Buffering"] = "no";
                    using var subscription = hub.Subscribe(projectId);
                    await http.Response.WriteAsync(": connected\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            using var beat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            beat.CancelAfter(HeartbeatInterval);
                            try
                            {
                                await subscription.Reader.WaitToReadAsync(beat.Token);
                                while (subscription.Reader.TryRead(out _)) { }
                                // An unnamed event, so the browser's EventSource.onmessage fires. The
                                // payload is a generic "project changed, refetch" nudge, not data.
                                await http.Response.WriteAsync("data: 1\n\n", ct);
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                await http.Response.WriteAsync(": ping\n\n", ct); // heartbeat tick
                            }
                            await http.Response.Body.FlushAsync(ct);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // The client disconnected (or the server is shutting down); nothing to do.
                    }
                })
            .WithName("projectEvents").ExcludeFromDescription()
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));
    }
}
