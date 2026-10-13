namespace Condux.Relay.Ingest;

/// <summary>Reading an inbound ingest request's credential. Shared by the Sentry-compatible and OTLP
/// endpoints, which authenticate the same way. The body is read through <c>RequestBodies</c> in Core.
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
}
