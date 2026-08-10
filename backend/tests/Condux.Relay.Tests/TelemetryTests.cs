using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Xunit;

namespace Condux.Relay.Tests;

/// <summary>
/// Verifies OpenTelemetry is opt-in (#39): the relay hosts with tracing registered only when
/// an OTLP endpoint is configured, so local dev and tests stay dependency-free by default.
/// </summary>
public class TelemetryTests
{
    [Fact]
    public void Telemetry_IsDisabled_WithoutEndpoint()
    {
        using var factory = new WebApplicationFactory<Program>();
        Assert.Null(factory.Services.GetService<TracerProvider>());
    }

    [Fact]
    public void Telemetry_IsEnabled_WhenOtlpEndpointSet()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317"));
        Assert.NotNull(factory.Services.GetService<TracerProvider>());
    }
}
