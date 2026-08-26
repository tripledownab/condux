using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Condux.Otlp;

namespace Condux.Relay.Ingest;

/// <summary>
/// Renders an OTLP array or key-value list as compact JSON, which is how a composite attribute is stored.
/// </summary>
/// <remarks>
/// The protocol has no text form for a composite, so this is our choice rather than its rule. It stays
/// JSON because that is what the previous JSON-only ingest path stored, and a facet that already reads
/// these values should not change shape under it.
/// <para>
/// No depth guard: every value here comes from the decoder, which refuses a payload nested deeper than
/// its own limit, so the recursion is already bounded before it arrives.
/// </para>
/// </remarks>
internal static class OtlpValueJson
{
    internal static string Render(AnyValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(Utf8JsonWriter writer, AnyValue? value)
    {
        switch (value?.Kind)
        {
            case AnyValueKind.String:
                writer.WriteStringValue(value.StringValue ?? "");
                break;
            case AnyValueKind.Bool:
                writer.WriteBooleanValue(value.BoolValue);
                break;
            case AnyValueKind.Int:
                writer.WriteNumberValue(value.IntValue);
                break;
            case AnyValueKind.Double:
                // JSON has no literal for NaN or either infinity, so those three travel as strings.
                if (double.IsFinite(value.DoubleValue))
                {
                    writer.WriteNumberValue(value.DoubleValue);
                }
                else
                {
                    writer.WriteStringValue(value.DoubleValue.ToString(CultureInfo.InvariantCulture));
                }

                break;
            case AnyValueKind.Bytes:
                writer.WriteStringValue(Convert.ToBase64String(value.BytesValue ?? []));
                break;
            case AnyValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.ArrayValue?.Values ?? [])
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            case AnyValueKind.Kvlist:
                writer.WriteStartObject();
                foreach (var pair in value.KvlistValue?.Values ?? [])
                {
                    writer.WritePropertyName(pair.Key);
                    Write(writer, pair.Value);
                }

                writer.WriteEndObject();
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
