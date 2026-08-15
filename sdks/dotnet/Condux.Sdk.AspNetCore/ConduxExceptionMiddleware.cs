using Microsoft.AspNetCore.Http;

namespace Condux.Sdk.AspNetCore;

/// <summary>
/// ASP.NET Core middleware that reports an unhandled request exception to Condux (as unhandled) and
/// re-throws, so the app's own error handling still runs. The <see cref="ConduxClient"/> is resolved from
/// DI with <c>app.UseConduxExceptionReporting()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Register it INSIDE your exception handler, not before it.</b> Middleware sees an exception only as
/// it unwinds back out, so whichever one is registered first is outermost and gets it last. A configured
/// <c>UseExceptionHandler</c> handles the exception and returns a response rather than rethrowing, so
/// anything outside it never sees the exception at all:
/// </para>
/// <code>
/// app.UseExceptionHandler("/error");     // first, so it is outermost
/// app.UseConduxExceptionReporting();     // inside it, so this sees the exception
/// </code>
/// <para>
/// Measured against a real application: registered inside, one event with the right error; registered
/// outside, zero events and no indication anything is wrong. The ASP.NET Core templates put
/// <c>UseExceptionHandler</c> first, so following them and adding this line after it is correct.
/// </para>
/// </remarks>
public sealed class ConduxExceptionMiddleware(RequestDelegate next, ConduxClient client)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // A scope per request, so a SetUser in a controller belongs to that request and cannot attach to
        // a concurrent one. The server serves many requests at once from one process, which is exactly
        // why this has to be scoped rather than left to process state.
        using var scope = ConduxScope.BeginRequest();
        try
        {
            await next(context);
        }
        catch (Exception error)
        {
            await client.CaptureExceptionAsync(
                error, handled: false, new CaptureContext { Request = Describe(context.Request) });
            throw;
        }
    }

    /// <summary>
    /// The request, in the shape the relay parses. Headers are available on the context and deliberately
    /// not read: they carry cookies and authorization, and not sending credentials is a stronger
    /// guarantee than scrubbing them after they arrive.
    /// </summary>
    private static ConduxRequest Describe(HttpRequest request) => new()
    {
        // Path and query kept apart, matching the wire shape, rather than glued into one string.
        Url = request.Path.HasValue ? request.Path.Value : "/",
        Method = request.Method,
        QueryString = request.QueryString.HasValue ? request.QueryString.Value?.TrimStart('?') : null,
    };
}
