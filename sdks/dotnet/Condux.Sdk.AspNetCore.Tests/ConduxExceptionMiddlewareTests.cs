using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Condux.Sdk.AspNetCore.Tests;

public class ConduxExceptionMiddlewareTests
{
    private static ConduxClient Client(HttpMessageHandler transport) =>
        new(new ConduxOptions { Dsn = "http://pub123@relay.test/7", Transport = transport });

    [Fact]
    public async Task Reports_an_unhandled_request_exception_and_rethrows()
    {
        var handler = new RecordingTransport();
        RequestDelegate next = _ => throw new InvalidOperationException("route boom");
        var middleware = new ConduxExceptionMiddleware(next, Client(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(new DefaultHttpContext()));

        var exception = JsonDocument.Parse(handler.LastBody!).RootElement
            .GetProperty("exception").GetProperty("values")[0];
        Assert.Equal("route boom", exception.GetProperty("value").GetString());
        Assert.False(exception.GetProperty("mechanism").GetProperty("handled").GetBoolean());
    }

    [Fact]
    public async Task Passes_a_successful_request_through_and_reports_nothing()
    {
        var handler = new RecordingTransport();
        var reached = false;
        RequestDelegate next = _ =>
        {
            reached = true;
            return Task.CompletedTask;
        };

        await new ConduxExceptionMiddleware(next, Client(handler)).InvokeAsync(new DefaultHttpContext());

        Assert.True(reached);
        Assert.Null(handler.LastBody);
    }
}
