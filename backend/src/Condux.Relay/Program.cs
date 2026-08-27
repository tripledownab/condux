// The relay's entry point: wire the services, then map the ingest surface. Everything it does is one
// line here and the detail is one directory away, in Setup/ for the wiring and Endpoints/ for the
// routes, so this file stays a readable statement of what the process is.
using Condux.Relay.Endpoints;
using Condux.Relay.Setup;
using Condux.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// A crash must end the process. As PID 1 in a container it otherwise survives its own unhandled
// exception and spins, looking healthy while doing nothing.
ProcessTermination.ExitOnUnhandledException();

var options = builder.AddRelayServices();

var app = builder.Build();

// Report the relay's own unhandled request exceptions to a Condux project when self-reporting is enabled.
// Fire-and-forget (the SDK reads the exception synchronously then POSTs on its own HttpClient and never
// throws), so it adds no latency to the error response. First in the pipeline, so it wraps every endpoint.
if (app.Services.GetService<Condux.Sdk.ConduxClient>() is { } conduxSelf)
{
    app.Use(async (context, next) =>
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            // A cancellation from a client disconnect or graceful shutdown is expected noise, not a fault;
            // still report a genuine OperationCanceledException thrown for any other reason.
            if (!ConduxSelfReport.IsExpectedCancellation(exception, context.RequestAborted.IsCancellationRequested))
            {
                _ = conduxSelf.CaptureExceptionAsync(exception);
            }
            throw;
        }
    });
}

// Inside the self-report wrapper above, so a decompression failure is reported like any other fault,
// and ahead of the ingest endpoints, which read an already-decompressed body.
app.UseRequestDecompression();

app.MapOperationalEndpoints(options);
app.MapSentryIngestEndpoints(options);
app.MapOtlpIngestEndpoints(options);

app.Run();

// Exposed so integration tests can host the app via WebApplicationFactory<Program>.
public partial class Program { }
