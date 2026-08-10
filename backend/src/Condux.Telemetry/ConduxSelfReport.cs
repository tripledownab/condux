using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Condux.Telemetry;

/// <summary>
/// Condux on Condux (#75): a service reports its own errors to a Condux relay via the first-party .NET SDK
/// (<c>Condux</c>). Opt-in via <c>CONDUX_SELF_DSN</c> — when set, this registers a singleton
/// <see cref="Condux.Sdk.ConduxClient"/>; the web services then wrap their request pipeline to report unhandled
/// exceptions (see each service's <c>Program</c>). Unset in dev and tests, so nothing is ever sent. The
/// SDK never throws, so a failed self-report cannot affect the host. (The SDK type is fully qualified to
/// avoid a clash with the OpenTelemetry <c>Level</c> in scope elsewhere.)
/// </summary>
public static class ConduxSelfReport
{
    public static void AddConduxSelfReporting(this IHostApplicationBuilder builder)
    {
        var dsn = builder.Configuration["CONDUX_SELF_DSN"];
        Condux.Sdk.ConduxClient? client = null;
        if (!string.IsNullOrWhiteSpace(dsn))
        {
            client = new Condux.Sdk.ConduxClient(new Condux.Sdk.ConduxOptions
            {
                Dsn = dsn,
                Environment = builder.Configuration["CONDUX_ENVIRONMENT"] ?? builder.Environment.EnvironmentName,
                Release = builder.Configuration["CONDUX_RELEASE"],
            });
            builder.Services.AddSingleton(client); // the web services resolve this directly for their middleware
        }

        // Always registered so a background worker can depend on it unconditionally; a no-op when the DSN
        // is unset. The web services keep resolving the raw client for their request-wrapping middleware.
        builder.Services.AddSingleton(new ConduxSelfReporter(client));
    }

    /// <summary>
    /// True for a cancellation the request-wrapping middleware should not report: an
    /// <see cref="OperationCanceledException"/> raised because the request was aborted (the client
    /// disconnected) or the host is shutting down. Npgsql surfaces query cancellation this way, so a client
    /// hangup mid-query would otherwise self-report as a fault. Gated on the caller's own cancellation state,
    /// so a genuine <see cref="OperationCanceledException"/> thrown for any other reason is still reported.
    /// </summary>
    public static bool IsExpectedCancellation(Exception exception, bool cancellationRequested) =>
        exception is OperationCanceledException && cancellationRequested;
}

/// <summary>
/// Reports a background worker's own unhandled error to Condux (#75), fire-and-forget. A no-op when
/// self-reporting is off (<c>CONDUX_SELF_DSN</c> unset), so workers depend on it unconditionally and the
/// SDK — which never throws — cannot affect the host loop.
/// </summary>
public sealed class ConduxSelfReporter(Condux.Sdk.ConduxClient? client)
{
    public void Report(Exception exception) => _ = client?.CaptureExceptionAsync(exception);
}
