using System.Text.Json;
using System.Text.Json.Serialization;

namespace Condux.Core.Text;

/// <summary>
/// Caller text made storable: U+0000 replaced by U+FFFD, the replacement character.
/// </summary>
/// <remarks>
/// U+0000 is valid text and valid JSON (<c>\u0000</c>), so nothing upstream refuses it, but Postgres
/// <c>text</c> cannot hold it and fails the whole statement with 22021. A NUL in an event's exception
/// message reached the issue upsert that way and failed in the consumer's drain, whose loop then pauses
/// before the next message, so one sender could slow ingest for everyone. Replacing rather than refusing
/// keeps the event, and keeps the position of the character visible. A control-plane request is refused
/// instead (the control-plane's <c>UnstorableText</c>), since a person typing a NUL meant nothing by it.
/// </remarks>
public static class StorableText
{
    public const char Replacement = '\uFFFD';

    public static string ToStorable(string text) =>
        text.Contains('\0', StringComparison.Ordinal) ? text.Replace('\0', Replacement) : text;
}

/// <summary>
/// Applies <see cref="StorableText.ToStorable"/> to every string a serializer reads or writes, values and
/// dictionary keys alike, so a payload bound or published through options carrying it holds no U+0000.
/// </summary>
public sealed class StorableStringConverter : JsonConverter<string>
{
    public override bool HandleNull => false;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() is { } text ? StorableText.ToStorable(text) : null;

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(StorableText.ToStorable(value));

    public override string ReadAsPropertyName(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        StorableText.ToStorable(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WritePropertyName(StorableText.ToStorable(value));
}
