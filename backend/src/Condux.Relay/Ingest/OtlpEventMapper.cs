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
    public static IReadOnlyList<Event> ToEvents(ExportLogsServiceRequest request)
    {
        var events = new List<Event>();
        foreach (var resourceLogs in request.ResourceLogs)
        {
            var resource = OtlpValues.ToAttributeMap(resourceLogs.Resource?.Attributes);
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
        var attributes = OtlpValues.ToAttributeMap(record.Attributes);
        var body = record.Body is null ? null : OtlpValues.ToText(record.Body);
        var exceptions = ParseException(attributes, body);
        // Log-record attributes ride as tags, minus the exception and user ones already folded in above.
        var tags = attributes
            .Where(kv => !kv.Key.StartsWith("exception.", StringComparison.Ordinal) && !OtlpValues.IsUserKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return new Event
        {
            // OTLP log records have no event id of their own; mint one so the pipeline can key on it.
            EventId = Guid.NewGuid().ToString("N"),
            TimestampUnixMs = OtlpValues.ToMilliseconds(record.TimeUnixNano) ?? OtlpValues.ToMilliseconds(record.ObservedTimeUnixNano) ?? 0,
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
            User = OtlpValues.ToUser(attributes),
            TraceId = OtlpValues.ToTraceId(record.TraceId),
        };
    }

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
