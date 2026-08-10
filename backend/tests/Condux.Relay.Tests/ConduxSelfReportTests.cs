using Condux.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Condux.Relay.Tests;

// Condux on Condux (#75): self-reporting is opt-in — a ConduxClient is registered only when CONDUX_SELF_DSN
// is set, so dev and tests send nothing. (The web services then wrap their pipeline with it; that middleware
// only rethrows, so the existing relay endpoint tests already prove behavior is otherwise unchanged.)
public class ConduxSelfReportTests
{
    [Fact]
    public void Registers_the_client_when_the_self_dsn_is_set()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["CONDUX_SELF_DSN"] = "https://key@self.condux.test/1";
        builder.AddConduxSelfReporting();

        using var host = builder.Build();

        Assert.NotNull(host.Services.GetService<Condux.Sdk.ConduxClient>());
    }

    [Fact]
    public void Registers_nothing_when_the_self_dsn_is_unset()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddConduxSelfReporting();

        using var host = builder.Build();

        Assert.Null(host.Services.GetService<Condux.Sdk.ConduxClient>());
    }

    // A client disconnect or graceful shutdown cancels in-flight work; Npgsql surfaces query cancellation as
    // OperationCanceledException. The request middleware must treat that as expected noise, not a self-reported
    // fault, but only when the request/host was actually cancelled.
    [Theory]
    [InlineData(true, true)] // aborted request + a cancellation: expected, do not report
    [InlineData(false, false)] // a cancellation with no abort: unexpected, report it
    public void Treats_a_cancellation_as_expected_only_when_cancellation_was_requested(
        bool cancellationRequested, bool expected)
    {
        var cancellation = new OperationCanceledException("Query was cancelled");

        Assert.Equal(expected, ConduxSelfReport.IsExpectedCancellation(cancellation, cancellationRequested));
    }

    [Fact]
    public void A_non_cancellation_exception_is_always_reported_even_during_an_abort()
    {
        var real = new InvalidOperationException("42P08: could not determine data type of parameter $1");

        Assert.False(ConduxSelfReport.IsExpectedCancellation(real, cancellationRequested: true));
    }
}
