using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.Sdk.AspNetCore.Tests;

// Wiring is checked when the pipeline is built, not when a request arrives: a missing registration is a
// startup mistake, and discovering it per request would break every request the app serves instead.
public class ApplicationBuilderExtensionsTests
{
    private static ApplicationBuilder Application(params ConduxClient[] client)
    {
        var services = new ServiceCollection();
        foreach (var registered in client)
        {
            services.AddSingleton(registered);
        }

        return new ApplicationBuilder(services.BuildServiceProvider());
    }

    [Fact]
    public void Fails_at_wiring_time_when_no_client_is_registered()
    {
        var app = Application();

        var error = Assert.Throws<InvalidOperationException>(() => app.UseConduxExceptionReporting());

        // The message has to name the fix: this throws during startup, far from the reporting code.
        Assert.Contains("AddSingleton(new ConduxClient(new ConduxOptions", error.Message);
    }

    [Fact]
    public async Task Reports_through_the_registered_client_when_one_is_wired()
    {
        var handler = new RecordingTransport();
        var app = Application(new ConduxClient(
            new ConduxOptions { Dsn = "http://pub123@relay.test/7", Transport = handler }));

        app.UseConduxExceptionReporting();
        app.Run(_ => throw new InvalidOperationException("route boom"));
        var pipeline = app.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline(new DefaultHttpContext()));

        var exception = JsonDocument.Parse(handler.LastBody!).RootElement
            .GetProperty("exception").GetProperty("values")[0];
        Assert.Equal("route boom", exception.GetProperty("value").GetString());
        Assert.False(exception.GetProperty("mechanism").GetProperty("handled").GetBoolean());
    }
}
