using Microsoft.AspNetCore.Http;

namespace Condux.Sdk.AspNetCore;

/// <summary>
/// ASP.NET Core middleware that reports an unhandled request exception to Condux (as unhandled) and
/// re-throws, so the app's own error handling still runs. The <see cref="ConduxClient"/> is resolved from
/// DI with <c>app.UseConduxExceptionReporting()</c>. An exception describing what the CALLER sent is
/// re-thrown without being reported: see <see cref="IsCallerCaused"/>.
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
            if (!IsCallerCaused(context, error))
            {
                await client.CaptureExceptionAsync(
                    error, handled: false, new CaptureContext { Request = Describe(context.Request) });
            }
            // Rethrown either way. Installing Condux must never change what the application returns.
            throw;
        }
    }

    /// <summary>
    /// True when the exception says what the CALLER did wrong, rather than naming a defect in the
    /// application. Filing those lets anyone with network access bury the real ones, which is an
    /// availability problem for the error store rather than untidiness. See ADR-0044.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ASP.NET Core is the one ecosystem with no registry to ask, so unlike the other adapters this is a
    /// list. Each entry was measured against a real app rather than taken from a doc page.
    /// </para>
    /// <para>
    /// <b>BadHttpRequestException</b> covers a body past <c>MaxRequestBodySize</c>, bad framing, a
    /// malformed chunked body and a caller who hangs up mid-body. Matching the
    /// <c>Microsoft.AspNetCore.Http</c> type rather than Kestrel's catches both: Kestrel's derives from
    /// it (measured) and is marked obsolete in favour of it.
    /// </para>
    /// <para>
    /// <b>InvalidDataException</b> only while reading a form, because it is a general
    /// <c>System.IO</c> type the application's own code raises over a corrupt stream too, and ignoring it
    /// everywhere would hide real defects. ASP.NET Core raises it for a form past the
    /// <c>FormOptions</c> limits.
    /// </para>
    /// <para>
    /// <b>OperationCanceledException</b> only when the caller actually disconnected. This narrowing is
    /// load-bearing rather than cautious: <c>TaskCanceledException</c> derives from it (measured), so a
    /// downstream HttpClient timeout arrives here as one, and matching the type alone would swallow every
    /// genuine timeout in the application.
    /// </para>
    /// </remarks>
    private static bool IsCallerCaused(HttpContext context, Exception error) => error switch
    {
        BadHttpRequestException => true,
        InvalidDataException => context.Request.HasFormContentType,
        OperationCanceledException => context.RequestAborted.IsCancellationRequested,
        _ => false,
    };

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
