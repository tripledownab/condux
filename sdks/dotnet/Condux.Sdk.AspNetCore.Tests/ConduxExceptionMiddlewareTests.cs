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
    public async Task Reports_the_request_url_and_method_but_never_the_headers()
    {
        var handler = new RecordingTransport();
        RequestDelegate next = _ => throw new InvalidOperationException("boom");
        var context = new DefaultHttpContext();
        context.Request.Path = "/checkout";
        context.Request.Method = "POST";
        context.Request.QueryString = new QueryString("?step=2");
        context.Request.Headers["Cookie"] = "session=supersecret";
        context.Request.Headers["Authorization"] = "Bearer tok";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ConduxExceptionMiddleware(next, Client(handler)).InvokeAsync(context));

        var root = JsonDocument.Parse(handler.LastBody!).RootElement;
        var request = root.GetProperty("request");
        Assert.Equal("/checkout", request.GetProperty("url").GetString());
        Assert.Equal("POST", request.GetProperty("method").GetString());
        // The leading "?" belongs to the QueryString type, not to the wire field.
        Assert.Equal("step=2", request.GetProperty("query_string").GetString());

        // The relay scrubs sensitive header keys, but the stronger guarantee is not sending them at all.
        // Asserted on the whole body so a copy anywhere in the event would still fail this.
        Assert.DoesNotContain("supersecret", handler.LastBody);
        Assert.DoesNotContain("Bearer", handler.LastBody);
    }

    [Fact]
    public async Task Opens_a_request_scope_so_enrichment_does_not_outlive_the_request()
    {
        var handler = new RecordingTransport();
        RequestDelegate next = _ =>
        {
            // What a controller would do after authenticating.
            ConduxScope.SetUser(new ConduxUser { Id = "u-1" });
            throw new InvalidOperationException("boom");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ConduxExceptionMiddleware(next, Client(handler)).InvokeAsync(new DefaultHttpContext()));

        // The reported event carries the user the request set.
        Assert.Equal("u-1", JsonDocument.Parse(handler.LastBody!).RootElement
            .GetProperty("user").GetProperty("id").GetString());

        // But it is gone once the request ends, so the next request cannot inherit it.
        var after = new RecordingTransport();
        await Client(after).CaptureMessageAsync("later");
        Assert.False(JsonDocument.Parse(after.LastBody!).RootElement.TryGetProperty("user", out _));
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
