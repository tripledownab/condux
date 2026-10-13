using System.Globalization;
using Condux.Core.Events;
using Condux.Otlp;

namespace Condux.Relay.Ingest;

/// <summary>
/// Maps a decoded OTLP logs export onto the internal <see cref="Event"/> model, so an app instrumented
/// with OpenTelemetry can report errors by pointing its logs exporter at the relay with no Sentry SDK
/// (#79). One <c>LogRecord</c> becomes one <see cref="Event"/>.
/// </summary>
/// <remarks>
/// Decoding the wire format is the Condux.Otlp package's job; everything here is ours. The split matters
/// because the mapping is opinionated in ways the protocol is not: how an <c>AnyValue</c> reads as a
/// string, which frames count as in-app, and what a severity number means on our own scale.
/// </remarks>
public static class OtlpEventMapper
{
    // The log-record attributes that say who was affected, from the OpenTelemetry semantic conventions:
    // enduser.* describes the end user of the system and user.* a user generally, so an end user's id is
    // preferred, then a plain id, then the pseudonymous forms. They fill Event.User as a Sentry event's
    // user does, which is what "users affected" counts, and they leave the tags so an identifier is not
    // stored twice.
    private static readonly string[] IdKeys = ["enduser.id", "user.id", "enduser.pseudo.id", "user.hash"];
    private const string EmailKey = "user.email";
    private const string NameKey = "user.name";

    public static IReadOnlyList<Event> ToEvents(ExportLogsServiceRequest request)
    {
        var events = new List<Event>();
        foreach (var resourceLogs in request.ResourceLogs)
        {
            var resource = ToAttributeMap(resourceLogs.Resource?.Attributes);
            foreach (var scopeLogs in resourceLogs.ScopeLogs)
            {
                foreach (var record in scopeLogs.LogRecords)
                {
                    events.Add(BuildEvent(record, resource));
                }
            }
        }

        return events;
    }

    private static Event BuildEvent(LogRecord record, IReadOnlyDictionary<string, string> resource)
    {
        var attributes = ToAttributeMap(record.Attributes);
        var body = record.Body is null ? null : ToText(record.Body);
        var exceptions = ParseException(attributes, body);
        // Log-record attributes ride as tags, minus the exception and user ones already folded in above.
        var tags = attributes
            .Where(kv => !kv.Key.StartsWith("exception.", StringComparison.Ordinal) && !IsUserKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return new Event
        {
            // OTLP log records have no event id of their own; mint one so the pipeline can key on it.
            EventId = Guid.NewGuid().ToString("N"),
            TimestampUnixMs = ToMilliseconds(record.TimeUnixNano) ?? ToMilliseconds(record.ObservedTimeUnixNano) ?? 0,
            Platform = resource.GetValueOrDefault("telemetry.sdk.language"),
            // A record carrying an exception but no severity reads as an error, the default a Sentry event
            // with no level gets, so no stored event is left at Unspecified.
            Level = ToLevel(record) is var level && level == Level.Unspecified && exceptions.Count > 0
                ? Level.Error
                : level,
            Message = string.IsNullOrEmpty(body) ? null : body,
            Exceptions = exceptions,
            ServerName = resource.GetValueOrDefault("service.name"),
            Release = resource.GetValueOrDefault("service.version"),
            Environment = resource.GetValueOrDefault("deployment.environment")
                ?? resource.GetValueOrDefault("deployment.environment.name"),
            Tags = tags,
            User = ToUser(attributes),
            TraceId = ToTraceId(record.TraceId),
        };
    }

    // Null when the record names nobody, so an event without these attributes keeps its previous shape.
    private static EventUser? ToUser(IReadOnlyDictionary<string, string> attributes)
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

    private static bool IsUserKey(string key) => key is EmailKey or NameKey || IdKeys.Contains(key);

    /// <summary>
    /// The decoder hands back the id as the bytes the protocol carries, so the hex spelling is ours to
    /// choose. Lower case matches what every exporter writes in the JSON encoding and what the Sentry
    /// path already stores, and a trace id that changes case by ingest route correlates with nothing.
    /// </summary>
    private static string? ToTraceId(byte[] traceId)
        => traceId.Length == 0 ? null : Convert.ToHexStringLower(traceId);

    private static long? ToMilliseconds(ulong unixNano)
        => unixNano > 0 ? (long)(unixNano / 1_000_000) : null;

    private static IReadOnlyDictionary<string, string> ToAttributeMap(List<KeyValue>? attributes)
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
    private static string ToText(AnyValue value) => value.Kind switch
    {
        AnyValueKind.String => value.StringValue ?? "",
        AnyValueKind.Int => value.IntValue.ToString(CultureInfo.InvariantCulture),
        AnyValueKind.Bool => value.BoolValue ? "true" : "false",
        AnyValueKind.Double => value.DoubleValue.ToString(CultureInfo.InvariantCulture),
        AnyValueKind.Bytes => Convert.ToBase64String(value.BytesValue ?? []),
        AnyValueKind.Array or AnyValueKind.Kvlist => OtlpValueJson.Render(value),
        _ => "",
    };

    private static IReadOnlyList<ExceptionValue> ParseException(
        IReadOnlyDictionary<string, string> attributes, string? body)
    {
        var type = attributes.GetValueOrDefault("exception.type");
        var message = attributes.GetValueOrDefault("exception.message");
        var stacktrace = attributes.GetValueOrDefault("exception.stacktrace");
        if (type is null && message is null && stacktrace is null)
        {
            return [];
        }

        return
        [
            new ExceptionValue
            {
                Type = string.IsNullOrEmpty(type) ? "Error" : type,
                Value = message ?? body,
                Stacktrace = OtlpStacktrace.ParseV8(stacktrace),
            },
        ];
    }

    // SeverityNumber ranges are fixed by the OTLP logs data model: 1-4 TRACE, 5-8 DEBUG, 9-12 INFO,
    // 13-16 WARN, 17-20 ERROR, 21-24 FATAL. Fall back to severityText when the number is absent.
    private static Level ToLevel(LogRecord record) => (int)record.SeverityNumber switch
    {
        >= 21 => Level.Fatal,
        >= 17 => Level.Error,
        >= 13 => Level.Warning,
        >= 9 => Level.Info,
        >= 1 => Level.Debug,
        _ => ToLevel(record.SeverityText),
    };

    private static Level ToLevel(string? text) => text?.ToLowerInvariant() switch
    {
        "fatal" or "critical" => Level.Fatal,
        "error" => Level.Error,
        "warn" or "warning" => Level.Warning,
        "info" => Level.Info,
        "debug" or "trace" => Level.Debug,
        _ => Level.Unspecified,
    };
}
