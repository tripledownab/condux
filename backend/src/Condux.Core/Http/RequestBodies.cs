using System.Buffers;

namespace Condux.Core.Http;

/// <summary>
/// Reading a request body a caller sized. The limit is checked while the bytes arrive, so a body past it is
/// refused before it is held in memory, rather than read in full and then measured. Shared by the relay's
/// ingest endpoints, the symbolizer and the control-plane's CI uploads.
///
/// The bytes are gathered in pooled pieces and joined once at the end, so a body is held about twice over,
/// and only while it is joined, where a buffer that grew by doubling and was then copied out held it up to
/// three times.
/// </summary>
public static class RequestBodies
{
    /// <summary>The size asked of the pool for the first piece a body is gathered in. A reader holds one before
    /// the first byte arrives, so under a budget it is charged for it then, at the size the pool gave. Small,
    /// so a connection that sends nothing holds little; each later piece is twice the last, up to
    /// <see cref="MaxPieceBytes"/>, so what is held stays close to what was sent.</summary>
    public const int FirstPieceBytes = 4 * 1024;

    /// <summary>The largest piece a body is gathered in.</summary>
    public const int MaxPieceBytes = 64 * 1024;

    /// <summary>The body's bytes, or null once it passes <paramref name="limit"/>. Bounds what was read
    /// from the stream, so behind decompression it bounds the DECOMPRESSED size, which a server request-size
    /// limit cannot: a zip bomb is tiny on the wire and only becomes large after the middleware has run.
    /// Bytes that do not decode throw <see cref="InvalidDataException"/>, so null always means too large.
    /// </summary>
    public static async Task<byte[]?> ReadBoundedAsync(Stream body, long limit, CancellationToken ct)
    {
        var (content, refusal) = await ReadAsync(body, limit, budget: null, ct);
        return refusal == BodyRefusal.Undecodable
            ? throw new InvalidDataException("The body did not decode as the encoding the request declared.")
            : content;
    }

    /// <summary>
    /// The body read whole under <paramref name="budget"/>, which every reader of its kind shares. What the
    /// read allocates is charged as it is allocated: each piece as it is taken, the first before any byte
    /// arrives, and each byte once more for its place in the joined body. Once joined, only the body stays
    /// charged, until the caller disposes it. So a sender holds the bytes it has sent and the piece it is
    /// sending into. How long it may take to send them is the server's to bound, by a minimum data rate.
    /// </summary>
    public static async Task<BodyRead> ReadHeldAsync(Stream body, long limit, ByteBudget budget, CancellationToken ct)
    {
        var (content, refusal) = await ReadAsync(body, limit, budget, ct);
        return new BodyRead(content is null ? null : new HeldBody(content, budget), refusal);
    }

    private static async Task<(byte[]? Content, BodyRefusal Refusal)> ReadAsync(
        Stream body, long limit, ByteBudget? budget, CancellationToken ct)
    {
        var pieces = new List<byte[]>(); // the last is the one being filled
        var used = 0;
        long length = 0, charged = 0, handedOver = 0;

        // A piece is charged before it is kept, and kept before anything else can fail, so the finally below
        // returns each piece to the pool exactly once and gives back exactly what was charged.
        bool TryTakePiece()
        {
            var size = pieces.Count == 0 ? FirstPieceBytes : Math.Min(2 * pieces[^1].Length, MaxPieceBytes);
            var next = ArrayPool<byte>.Shared.Rent(size);
            if (!TryCharge(next.Length))
            {
                ArrayPool<byte>.Shared.Return(next);
                return false;
            }
            pieces.Add(next);
            return true;
        }

        bool TryCharge(long count)
        {
            if (budget is null)
            {
                return true;
            }
            if (!budget.TryReserve(count))
            {
                return false;
            }
            charged += count;
            return true;
        }

        try
        {
            if (!TryTakePiece())
            {
                return (null, BodyRefusal.OverBudget);
            }
            int read;
            while ((read = await body.ReadAsync(pieces[^1].AsMemory(used), ct)) > 0)
            {
                if (length + read > limit)
                {
                    return (null, BodyRefusal.TooLarge); // refused before the whole thing is ever materialized
                }
                if (!TryCharge(read))
                {
                    return (null, BodyRefusal.OverBudget);
                }
                length += read;
                used += read;
                // Taken once this one is full, before the stream says whether more is coming, so a body that
                // ends exactly where a piece does is charged for one more piece until the read returns.
                if (used == pieces[^1].Length)
                {
                    if (!TryTakePiece())
                    {
                        return (null, BodyRefusal.OverBudget);
                    }
                    used = 0;
                }
            }

            var content = Join(pieces, used, length);
            handedOver = budget is null ? 0 : length;
            return (content, BodyRefusal.None);
        }
        // A decompression stream refusing bytes that are not the encoding they claim: answered rather than
        // thrown, so a sender's broken body is not a server error.
        catch (Exception e) when (IsDecoderRefusal(e))
        {
            return (null, BodyRefusal.Undecodable);
        }
        finally
        {
            foreach (var piece in pieces)
            {
                ArrayPool<byte>.Shared.Return(piece);
            }
            budget?.Release(charged - handedOver);
        }
    }

    /// <summary>Whether the decoder in front of the read refused the bytes. GZipStream and ZLibStream throw
    /// InvalidDataException and BrotliStream throws InvalidOperationException, which the server also throws
    /// for faults of its own, so it counts only when the compression library raised it (its Source is the
    /// throwing assembly's name, measured on .NET 10; the message is localized, so it is not read).</summary>
    private static bool IsDecoderRefusal(Exception e) =>
        e is InvalidDataException
        || (e is InvalidOperationException && e.Source is "System.IO.Compression.Brotli");

    private static byte[] Join(List<byte[]> pieces, int lastUsed, long length)
    {
        var content = new byte[length];
        var offset = 0;
        for (var i = 0; i < pieces.Count - 1; i++)
        {
            pieces[i].CopyTo(content, offset);
            offset += pieces[i].Length;
        }
        pieces[^1].AsSpan(0, lastUsed).CopyTo(content.AsSpan(offset));
        return content;
    }
}
