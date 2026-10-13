using System.Buffers;

namespace Condux.Core.Http;

/// <summary>
/// Reading a request body a caller sized. The limit is checked while the bytes arrive, so a body past it is
/// refused before it is held in memory, rather than read in full and then measured. Shared by the relay's
/// ingest endpoints and the control-plane's CI uploads.
/// </summary>
public static class RequestBodies
{
    /// <summary>The body's bytes, or null once it passes <paramref name="limit"/>. Bounds what was read
    /// from the stream, so behind decompression it bounds the DECOMPRESSED size, which a server request-size
    /// limit cannot: a zip bomb is tiny on the wire and only becomes large after the middleware has run.
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
}
