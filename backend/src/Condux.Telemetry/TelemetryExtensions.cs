using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Condux.Telemetry;

/// <summary>
/// Shared OpenTelemetry wiring for the Condux services: traces + metrics exported over OTLP.
/// Opt-in — enabled only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set, so local dev and tests
/// stay quiet and dependency-free. HttpClient + .NET runtime instrumentation are wired for every
/// service; web services pass ASP.NET Core instrumentation (or anything else) through the optional
/// callbacks. The exporter reads the standard <c>OTEL_*</c> env vars.
/// </summary>
public static class TelemetryExtensions
{
    public static void AddConduxTelemetry(
        this IHostApplicationBuilder builder,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return; // observability is opt-in via the standard OTLP endpoint env var
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing.AddHttpClientInstrumentation();
                configureTracing?.Invoke(tracing);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddHttpClientInstrumentation().AddRuntimeInstrumentation();
                configureMetrics?.Invoke(metrics);
            })
            .UseOtlpExporter();
    }
}
