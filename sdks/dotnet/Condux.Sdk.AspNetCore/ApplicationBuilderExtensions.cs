using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Condux.Sdk.AspNetCore;

/// <summary>Wires the Condux exception-reporting middleware into the request pipeline.</summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Report unhandled request exceptions to Condux. <b>Register it AFTER <c>UseExceptionHandler</c>,
    /// so it sits inside your exception handling</b>: middleware sees an exception only as it unwinds
    /// back out, and a configured exception handler returns a response rather than rethrowing, so
    /// anything registered before it never sees the exception and reports nothing. Requires a
    /// <see cref="ConduxClient"/> registered in DI, e.g.
    /// <c>builder.Services.AddSingleton(new ConduxClient(new ConduxOptions { Dsn = "..." }));</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No <see cref="ConduxClient"/> is registered. A missing
    /// registration is a wiring mistake, so it stops the app at startup: left to the middleware's own
    /// per-request resolution it would instead break every request the app serves, and the first symptom
    /// would be an unrelated-looking failure in code that has nothing to do with reporting.</exception>
    public static IApplicationBuilder UseConduxExceptionReporting(this IApplicationBuilder app)
    {
        var client = app.ApplicationServices.GetService<ConduxClient>()
            ?? throw new InvalidOperationException(
                "UseConduxExceptionReporting() requires a ConduxClient in dependency injection. Register one "
                + "before building the app: builder.Services.AddSingleton(new ConduxClient(new ConduxOptions "
                + "{ Dsn = \"https://<key>@ingest.condux.ai/<projectId>\" }));");

        // The verified instance is handed to the middleware, so the client the check passed on is exactly
        // the client that reports.
        return app.UseMiddleware<ConduxExceptionMiddleware>(client);
    }
}
