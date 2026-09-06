using Microsoft.AspNetCore.Http;
using Xunit;

namespace Condux.Sdk.AspNetCore.Tests;

// ADR-0044: an exception saying what the CALLER did wrong is rethrown but not filed as a defect.
//
// ASP.NET Core publishes no exception-to-status registry, so unlike the other adapters this rule is a
// list of ours, and a list is only as good as the reasons for each entry. Every case here was measured
// against a real ASP.NET Core 10 app before it was written down; these tests pin the rule so it cannot
// drift, and the real app is what proves the types are the ones the framework actually raises.
public class CallerCausedTests
{
    private static async Task<RecordingTransport> Through<TThrown>(HttpContext context, TThrown thrown)
        where TThrown : Exception
    {
        var handler = new RecordingTransport();
        var client = new ConduxClient(new ConduxOptions { Dsn = "http://pub123@relay.test/7", Transport = handler });
        var middleware = new ConduxExceptionMiddleware(_ => throw thrown, client);

        // The EXACT type, not merely "something threw". Two things ride on that. It pins the requirement
        // that the application still sees its own exception, so installing Condux cannot change what it
        // returns. And it stops a crash inside the caller-caused check from passing: that would satisfy
        // ThrowsAny, leave the client unreached, and make every absence assertion below succeed.
        await Assert.ThrowsAsync<TThrown>(() => middleware.InvokeAsync(context));

        return handler;
    }

    [Fact]
    public async Task An_application_fault_is_still_reported()
    {
        // The control, first: without it every absence below would pass just as well against a recorder
        // that never records, which is the shape that let this whole class of defect ship.
        var handler = await Through(new DefaultHttpContext(), new InvalidOperationException("route boom"));

        Assert.NotNull(handler.LastBody);
    }

    [Fact]
    public async Task A_malformed_request_is_rethrown_but_not_reported()
    {
        // Kestrel's own type derives from this one and is obsolete in its favour, so matching the base
        // catches a body over the size limit, bad framing and a malformed chunked body alike.
        var handler = await Through(new DefaultHttpContext(), new BadHttpRequestException("bad framing"));

        Assert.Null(handler.LastBody);
    }

    [Fact]
    public async Task A_form_past_its_limits_is_not_reported()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";

        var handler = await Through(context, new InvalidDataException("too many form values"));

        Assert.Null(handler.LastBody);
    }

    [Fact]
    public async Task The_same_type_outside_a_form_read_is_still_a_defect()
    {
        // InvalidDataException is a general System.IO type. A corrupt gzip stream in the application's
        // own code raises it too, so ignoring it everywhere would hide real defects. The narrowing to a
        // form content type is what keeps this one honest.
        var handler = await Through(new DefaultHttpContext(), new InvalidDataException("corrupt archive"));

        Assert.NotNull(handler.LastBody);
    }

    [Fact]
    public async Task A_caller_who_disconnected_is_not_reported()
    {
        var context = new DefaultHttpContext();
        var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        context.RequestAborted = aborted.Token;

        var handler = await Through(context, new OperationCanceledException());

        Assert.Null(handler.LastBody);
    }

    [Fact]
    public async Task A_timeout_while_the_caller_is_still_connected_is_a_defect()
    {
        // The load-bearing narrowing. TaskCanceledException derives from OperationCanceledException, so a
        // downstream HttpClient timeout arrives as one; matching on the type alone would swallow every
        // genuine timeout in the application. RequestAborted is what tells them apart.
        var handler = await Through(new DefaultHttpContext(), new TaskCanceledException("upstream timed out"));

        Assert.NotNull(handler.LastBody);
    }
}
