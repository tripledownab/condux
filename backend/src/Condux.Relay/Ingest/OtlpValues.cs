using System.Globalization;
using Condux.Core.Events;
using Condux.Otlp;

namespace Condux.Relay.Ingest;

/// <summary>How OTLP values read as the strings, instants and ids we store, shared by the logs and the
/// spans mappers so the two cannot read one attribute two ways.</summary>
internal static class OtlpValues
{
    // The attributes that say who was affected, from the OpenTelemetry semantic conventions:
    // enduser.* describes the end user of the system and user.* a user generally, so an end user's id is
    // preferred, then a plain id, then the pseudonymous forms. They fill Event.User as a Sentry event's
    // user does, which is what "users affected" counts, and they leave the tags so an identifier is not
    // stored twice.
    private static readonly string[] IdKeys = ["enduser.id", "user.id", "enduser.pseudo.id", "user.hash"];
    private const string EmailKey = "user.email";
    private const string NameKey = "user.name";

    /// <summary>
    /// The decoder hands back the id as the bytes the protocol carries, so the hex spelling is ours to
    /// choose. Lower case matches what every exporter writes in the JSON encoding and what the Sentry
    /// path already stores, and a trace id that changes case by ingest route correlates with nothing.
    /// </summary>
    public static string? ToTraceId(byte[] traceId)
        => traceId.Length == 0 ? null : Convert.ToHexStringLower(traceId);

    public static long? ToMilliseconds(ulong unixNano)
        => unixNano > 0 ? (long)(unixNano / 1_000_000) : null;

    public static IReadOnlyDictionary<string, string> ToAttributeMap(List<KeyValue>? attributes)
    {
        var map = new Dictionary<string, string>();
        if (attributes is null)
        {
            return map;
        }

        foreach (var pair in attributes)
        {
            if (pair.Value is not null)
            {
                map[pair.Key] = ToText(pair.Value);
            }
        }

        return map;
    }

    /// <summary>
    /// Reads a value as the string we store. A scalar reads as its plain text. A composite has no plain
    /// text form, so it keeps its JSON so nothing is silently lost.
    /// </summary>
    public static string ToText(AnyValue value) => value.Kind switch
    {
        AnyValueKind.String => value.StringValue ?? "",
        AnyValueKind.Int => value.IntValue.ToString(CultureInfo.InvariantCulture),
        AnyValueKind.Bool => value.BoolValue ? "true" : "false",
        AnyValueKind.Double => value.DoubleValue.ToString(CultureInfo.InvariantCulture),
        AnyValueKind.Bytes => Convert.ToBase64String(value.BytesValue ?? []),
        AnyValueKind.Array or AnyValueKind.Kvlist => OtlpValueJson.Render(value),
        _ => "",
    };

    // Null when the record names nobody, so an event without these attributes keeps its previous shape.
    public static EventUser? ToUser(IReadOnlyDictionary<string, string> attributes)
    {
        var id = IdKeys
            .Select(key => attributes.GetValueOrDefault(key))
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));
        var email = attributes.GetValueOrDefault(EmailKey) is { Length: > 0 } e ? e : null;
        var name = attributes.GetValueOrDefault(NameKey) is { Length: > 0 } n ? n : null;
        return id is null && email is null && name is null
            ? null
            : new EventUser { Id = id, Email = email, Username = name };
    }

    public static bool IsUserKey(string key) => key is EmailKey or NameKey || IdKeys.Contains(key);
}
