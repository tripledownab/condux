namespace Condux.Relay.Ingest;

/// <summary>Reading an inbound ingest request's credential, and the challenge a refused one gets. Shared by
/// the Sentry-compatible and OTLP endpoints, which authenticate the same way. The body is read through
/// <c>RequestBodies</c> in Core.
/// </summary>
internal static class IngestRequest
{
    /// <summary>Public key from the Condux SDK header, the Sentry X-Sentry-Auth header, or ?sentry_key.
    /// </summary>
    public static string? ExtractPublicKey(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("x-condux-auth", out var condux) && !string.IsNullOrEmpty(condux))
        {
            return condux.ToString();
        }
        if (ctx.Request.Headers.TryGetValue("X-Sentry-Auth", out var sentry))
        {
            foreach (var part in sentry.ToString().Split(','))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim().EndsWith("sentry_key", StringComparison.OrdinalIgnoreCase))
                {
                    return kv[1].Trim();
                }
            }
        }
        if (ctx.Request.Query.TryGetValue("sentry_key", out var q) && !string.IsNullOrEmpty(q))
        {
            return q.ToString();
        }
        return null;
    }

    /// <summary>
    /// Puts a <c>WWW-Authenticate</c> challenge on every 401 the relay sends, which RFC 9110 15.5.2 requires,
    /// at the response rather than at each ingest route, so a route added later cannot leave it out. The
    /// scheme is <c>Sentry</c>, the name the Sentry SDKs write in <c>X-Sentry-Auth</c>, one of the three
    /// places the DSN key is read from (with <c>x-condux-auth</c> and <c>?sentry_key</c>). It is a
    /// descriptive name rather than a scheme a client answers in <c>Authorization</c>; it is there because
    /// a 401 must carry a challenge.
    /// </summary>
    public static IApplicationBuilder UseDsnChallenge(this IApplicationBuilder app) =>
        app.Use((ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                if (ctx.Response.StatusCode == StatusCodes.Status401Unauthorized)
                {
                    ctx.Response.Headers.WWWAuthenticate = "Sentry realm=\"condux\"";
                }
                return Task.CompletedTask;
            });
            return next(ctx);
        });
}
