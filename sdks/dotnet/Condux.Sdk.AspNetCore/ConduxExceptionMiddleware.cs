using Microsoft.AspNetCore.Http;

namespace Condux.Sdk.AspNetCore;

/// <summary>
/// ASP.NET Core middleware that reports an unhandled request exception to Condux (as unhandled) and
/// re-throws, so the app's own error handling still runs. The <see cref="ConduxClient"/> is resolved from
/// DI; register the middleware early (before UseRouting) with <c>app.UseConduxExceptionReporting()</c>.
/// </summary>
public sealed class ConduxExceptionMiddleware(RequestDelegate next, ConduxClient client)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception error)
        {
            await client.CaptureExceptionAsync(error, handled: false);
            throw;
        }
    }
}
