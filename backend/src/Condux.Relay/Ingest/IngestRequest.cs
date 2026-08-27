using System.Buffers;

namespace Condux.Relay.Ingest;

/// <summary>Reading an inbound ingest request: the credential it carries, and its body. Shared by the
/// Sentry-compatible and OTLP endpoints, which authenticate the same way and are bounded the same way.
/// </summary>
internal static class IngestRequest
{
    /// <summary>Reads the request body, refusing it the moment it passes <paramref name="limit"/> bytes;
    /// null means it did. Bounds the DECOMPRESSED size, which a server request-size limit cannot: a zip
    /// bomb is tiny on the wire and only becomes large after the decompression middleware has run.
    /// </summary>
    public static async Task<byte[]?> ReadBoundedAsync(Stream body, long limit, CancellationToken ct)
    {
        var rented = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            using var buffer = new MemoryStream();
            int read;
            while ((read = await body.ReadAsync(rented, ct)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    return null; // refused before the whole thing is ever materialized
                }
                await buffer.WriteAsync(rented.AsMemory(0, read), ct);
            }
            return buffer.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

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
