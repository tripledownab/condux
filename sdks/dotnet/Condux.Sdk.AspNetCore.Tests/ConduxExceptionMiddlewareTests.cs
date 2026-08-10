using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Condux.Sdk.AspNetCore.Tests;

public class ConduxExceptionMiddlewareTests
{
    // Records the request body the SDK sends, and responds 202 — so a test can assert what reached the relay.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private static ConduxClient Client(HttpMessageHandler transport) =>
        new(new ConduxOptions { Dsn = "http://pub123@relay.test/7", Transport = transport });

    [Fact]
    public async Task Reports_an_unhandled_request_exception_and_rethrows()
    {
        var handler = new RecordingHandler();
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
        var handler = new RecordingHandler();
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
