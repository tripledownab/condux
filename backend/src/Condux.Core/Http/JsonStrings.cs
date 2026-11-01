using System.Buffers;
using System.Text.Json;
using System.Text.Unicode;

namespace Condux.Core.Http;

/// <summary>
/// Whether every string and member name in a JSON document is valid text, checked before anything reads
/// one.
/// </summary>
/// <remarks>
/// The JSON parser accepts a document whose strings are not text: a byte that is not UTF-8, or an escape
/// for half a surrogate pair. The runtime throws only when such a string is read, or when an escaped member
/// name is compared during a property lookup, so each of the parser's many reads was a place an
/// <see cref="InvalidOperationException"/> could escape as a 500. Answering the question once, at the
/// entry point, makes every later read safe, and leaves any such exception that remains to be the bug it
/// is. The pass is one more read of a body that is already in memory and has already parsed.
/// </remarks>
public static class JsonStrings
{
    /// <summary>
    /// True when every string and member name decodes to text. Call it on a document that has already
    /// parsed: a reader error here is a <see cref="JsonException"/>, as from the parse.
    /// </summary>
    public static bool AreText(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        byte[]? unescaped = null;
        try
        {
            while (reader.Read())
            {
                if ((reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName)
                    && !IsText(ref reader, ref unescaped))
                {
                    return false;
                }
            }
            return true;
        }
        finally
        {
            if (unescaped is not null)
            {
                ArrayPool<byte>.Shared.Return(unescaped);
            }
        }
    }

    // An unescaped value is its own UTF-8, so it is checked as it stands. An escaped one is unescaped into a
    // pooled buffer, which throws for both kinds of bad text, a byte that is not UTF-8 beside the escape
    // and an escape for half a surrogate pair (measured on .NET 10), so the copy is the check.
    private static bool IsText(ref Utf8JsonReader reader, ref byte[]? unescaped)
    {
        var raw = reader.ValueSpan;
        if (!reader.ValueIsEscaped)
        {
            return Utf8.IsValid(raw);
        }

        if (unescaped is null || unescaped.Length < raw.Length)
        {
            if (unescaped is not null)
            {
                ArrayPool<byte>.Shared.Return(unescaped);
            }
            unescaped = ArrayPool<byte>.Shared.Rent(raw.Length);
        }

        try
        {
            reader.CopyString(unescaped);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
