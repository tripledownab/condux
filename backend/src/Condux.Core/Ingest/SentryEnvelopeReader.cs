using System.Text.Json;
using Condux.Core.Http;

namespace Condux.Core.Ingest;

/// <summary>
/// Reads Sentry's newline-delimited envelope framing and returns the first <c>event</c> item.
/// </summary>
/// <remarks>
/// Works on bytes rather than text, and honours the per-item <c>length</c> header when the SDK sends
/// one. Both matter: an attachment payload is raw binary that may contain newlines, so scanning for the
/// next newline instead of trusting <c>length</c> desyncs every following item and loses the event.
/// The mobile SDKs always send <c>length</c>, and attach binary items to crash reports.
/// </remarks>
internal static class SentryEnvelopeReader
{
    private const byte NewLine = (byte)'\n';

    /// <summary>Finds the payload of the envelope's first <c>event</c> item, and the envelope's own
    /// header line.</summary>
    internal static ParseOutcome TryFindEventPayload(ReadOnlySpan<byte> body, out byte[] payload, out byte[] header)
    {
        payload = [];
        header = [];
        var cursor = 0;

        // Line 0 is the envelope header; a body not starting with one is not an envelope at all.
        if (!TryReadLine(body, ref cursor, out var envelopeHeader) || !IsJsonObject(envelopeHeader))
        {
            return ParseOutcome.Malformed;
        }
        header = envelopeHeader.ToArray();

        while (cursor < body.Length)
        {
            if (!TryReadLine(body, ref cursor, out var itemHeader))
            {
                break;
            }
            if (itemHeader.IsEmpty)
            {
                continue; // tolerate a doubled or trailing newline between items
            }

            if (!TryReadItemHeader(itemHeader, out var type, out var length)
                || !TryReadItemPayload(body, ref cursor, length, out var itemPayload))
            {
                return ParseOutcome.Malformed;
            }

            if (type == "event")
            {
                payload = itemPayload.ToArray();
                return ParseOutcome.Parsed;
            }
        }

        return ParseOutcome.NoEvent;
    }

    private static bool TryReadLine(ReadOnlySpan<byte> body, ref int cursor, out ReadOnlySpan<byte> line)
    {
        if (cursor >= body.Length)
        {
            line = default;
            return false;
        }

        var rest = body[cursor..];
        var end = rest.IndexOf(NewLine);
        if (end < 0)
        {
            line = rest;
            cursor = body.Length;
            return true;
        }

        line = rest[..end];
        cursor += end + 1;
        return true;
    }

    private static bool TryReadItemHeader(ReadOnlySpan<byte> header, out string? type, out int? length)
    {
        type = null;
        length = null;
        try
        {
            using var doc = JsonDocument.Parse(header.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !JsonStrings.AreText(header))
            {
                return false;
            }
            if (doc.RootElement.TryGetMember("type", out var t) && t.ValueKind == JsonValueKind.String)
            {
                type = t.GetString();
            }
            if (doc.RootElement.TryGetMember("length", out var l) && l.ValueKind == JsonValueKind.Number
                && l.TryGetInt32(out var declared))
            {
                length = declared;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadItemPayload(
        ReadOnlySpan<byte> body, ref int cursor, int? length, out ReadOnlySpan<byte> payload)
    {
        // No declared length: the payload runs to the next newline, which is safe only because an SDK
        // that omits length is sending JSON.
        if (length is not { } declared)
        {
            return TryReadLine(body, ref cursor, out payload);
        }

        // A correct length always lands on the separator before the next item, or on the end of the
        // body. When it does not the header is lying, and trusting it would swallow the following
        // items, so fall back to newline scanning rather than silently losing them. The length is compared
        // with what is left before it is added to the cursor: the sender chooses it, and near int.MaxValue
        // the sum wraps below zero.
        if (declared < 0 || declared > body.Length - cursor)
        {
            return TryReadLine(body, ref cursor, out payload);
        }
        var end = cursor + declared;
        if (end < body.Length && body[end] != NewLine)
        {
            return TryReadLine(body, ref cursor, out payload);
        }

        payload = body.Slice(cursor, declared);
        cursor = end < body.Length ? end + 1 : end;
        return true;
    }

    // The envelope header is read again later for its sent_at, so its text is checked here, once.
    private static bool IsJsonObject(ReadOnlySpan<byte> line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            return doc.RootElement.ValueKind == JsonValueKind.Object && JsonStrings.AreText(line);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
