// The relay's entry point: wire the services, then map the ingest surface. Everything it does is one
// line here and the detail is one directory away, in Setup/ for the wiring and Endpoints/ for the
// routes, so this file stays a readable statement of what the process is.
using Condux.Relay.Endpoints;
using Condux.Relay.Ingest;
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
            // What the CALLER got wrong is not Condux's fault, and anyone can send it at will (ADR-0044): a
            // body past the cap once decompressed, a body cut short, a client that hung up. The SDK's rule.
            if (!Condux.Sdk.AspNetCore.ConduxExceptionMiddleware.IsCallerCaused(context, exception))
            {
                _ = conduxSelf.CaptureExceptionAsync(exception);
            }
            throw;
        }
    });
}

// Ahead of the ingest endpoints, which read an already-decompressed body. Bytes that are not the encoding
// they were sent as fail that read, which answers them 400 (RequestBodies): the sender's broken body, so
// not a fault to report.
app.UseRequestDecompression();

// Every 401 carries a WWW-Authenticate challenge (RFC 9110 15.5.2), whichever ingest route refused the DSN.
app.UseDsnChallenge();

app.MapOperationalEndpoints(options);
app.MapSentryIngestEndpoints(options);
app.MapOtlpIngestEndpoints(options);
app.MapOtlpTraceEndpoints(options);

app.Run();

// Exposed so integration tests can host the app via WebApplicationFactory<Program>.
public partial class Program { }
