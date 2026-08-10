using Microsoft.AspNetCore.Builder;

namespace Condux.Sdk.AspNetCore;

/// <summary>Wires the Condux exception-reporting middleware into the request pipeline.</summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Report unhandled request exceptions to Condux. Register early (before UseRouting). Requires a
    /// <see cref="ConduxClient"/> registered in DI, e.g.
    /// <c>builder.Services.AddSingleton(new ConduxClient(new ConduxOptions { Dsn = "..." }));</c>.
    /// </summary>
    public static IApplicationBuilder UseConduxExceptionReporting(this IApplicationBuilder app) =>
        app.UseMiddleware<ConduxExceptionMiddleware>();
}
