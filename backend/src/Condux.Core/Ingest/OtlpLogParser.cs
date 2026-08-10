using System.Globalization;
using System.Text.Json;
using Condux.Core.Events;

namespace Condux.Core.Ingest;

/// <summary>
/// Parses an OTLP/HTTP <b>logs</b> export (the proto3-JSON encoding of <c>ExportLogsServiceRequest</c>)
/// into the internal <see cref="Event"/> model, so an app instrumented with OpenTelemetry can report
/// errors to Condux by pointing its OTLP logs exporter at the relay — no Sentry SDK required (#79).
/// One <c>LogRecord</c> becomes one <see cref="Event"/>: the severity maps to a <see cref="Level"/>, the
/// body to the message, and the OpenTelemetry <c>exception.*</c> semantic-convention attributes to an
/// <see cref="ExceptionValue"/> (a V8 stacktrace string is parsed into frames; other runtimes keep
/// type + message). Resource attributes carry <c>service.name</c>/<c>service.version</c>/
/// <c>deployment.environment</c>. JSON only for now (protobuf is a follow-up).
/// </summary>
public static class OtlpLogParser
{
    /// <summary>Parse an OTLP/JSON logs payload into events (one per LogRecord). Empty when the payload
    /// carries no records; throws <see cref="JsonException"/> on malformed JSON (the caller returns 400).</summary>
    public static IReadOnlyList<Event> ParseLogs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var events = new List<Event>();
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("resourceLogs", out var resourceLogs)
            || resourceLogs.ValueKind != JsonValueKind.Array)
        {
            return events;
        }

        foreach (var resourceLog in resourceLogs.EnumerateArray())
        {
            var resource = resourceLog.TryGetProperty("resource", out var res) && res.ValueKind == JsonValueKind.Object
                ? Attributes(res)
                : new Dictionary<string, string>();

            if (!resourceLog.TryGetProperty("scopeLogs", out var scopeLogs)
                || scopeLogs.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var scopeLog in scopeLogs.EnumerateArray())
            {
                if (!scopeLog.TryGetProperty("logRecords", out var records)
                    || records.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var record in records.EnumerateArray())
                {
                    events.Add(BuildEvent(record, resource));
                }
            }
        }

        return events;
    }

    private static Event BuildEvent(JsonElement record, IReadOnlyDictionary<string, string> resource)
    {
        var attributes = Attributes(record);
        var body = AnyValueString(record, "body");
        var exceptions = ParseException(attributes, body);
        // Log-record attributes ride as tags, minus the exception ones already folded into the exception.
        var tags = attributes
            .Where(kv => !kv.Key.StartsWith("exception.", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return new Event
        {
            // OTLP log records have no event id of their own; mint one so the pipeline can key on it.
            EventId = Guid.NewGuid().ToString("N"),
            TimestampUnixMs = UnixNanoToMs(record, "timeUnixNano") ?? UnixNanoToMs(record, "observedTimeUnixNano") ?? 0,
            Platform = resource.GetValueOrDefault("telemetry.sdk.language"),
            Level = LevelFrom(record),
            Message = string.IsNullOrEmpty(body) ? null : body,
            Exceptions = exceptions,
            ServerName = resource.GetValueOrDefault("service.name"),
            Release = resource.GetValueOrDefault("service.version"),
            Environment = resource.GetValueOrDefault("deployment.environment")
                ?? resource.GetValueOrDefault("deployment.environment.name"),
            Tags = tags,
            TraceId = HexString(record, "traceId"),
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
                Stacktrace = ParseV8Stacktrace(stacktrace),
            },
        ];
    }

    // A V8 `error.stack` line: "at fn (file:line:col)" or the anonymous "at file:line:col". Non-matching
    // lines (the leading "Type: message", or a non-V8 runtime's format) are skipped, so a Python/Java
    // stacktrace simply yields no frames (type + message still group the issue).
    private static readonly System.Text.RegularExpressions.Regex V8Frame = new(
        @"^\s*at (?:(?<fn>.+?) \()?(?<file>.+?):(?<line>\d+):(?<col>\d+)\)?\s*$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static Stacktrace? ParseV8Stacktrace(string? stack)
    {
        if (string.IsNullOrEmpty(stack))
        {
            return null;
        }
        var frames = new List<Frame>();
        foreach (var line in stack.Split('\n'))
        {
            var match = V8Frame.Match(line);
            if (!match.Success)
            {
                continue;
            }
            var filename = match.Groups["file"].Value;
            frames.Add(new Frame
            {
                Filename = filename,
                Function = match.Groups["fn"].Success ? match.Groups["fn"].Value : "<anonymous>",
                Lineno = int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture),
                Colno = int.Parse(match.Groups["col"].Value, CultureInfo.InvariantCulture),
                InApp = !filename.Contains("node_modules", StringComparison.Ordinal)
                    && !filename.StartsWith("node:", StringComparison.Ordinal),
            });
        }
        if (frames.Count == 0)
        {
            return null;
        }
        // V8 stacks are newest-first; the model orders oldest (outermost) → newest (crashing).
        frames.Reverse();
        return new Stacktrace { Frames = frames };
    }

    // SeverityNumber ranges are fixed by the OTLP logs data model: 1-4 TRACE, 5-8 DEBUG, 9-12 INFO,
    // 13-16 WARN, 17-20 ERROR, 21-24 FATAL. Fall back to severityText when the number is absent.
    private static Level LevelFrom(JsonElement record)
    {
        var number = record.TryGetProperty("severityNumber", out var n) && n.ValueKind == JsonValueKind.Number
            && n.TryGetInt32(out var value)
            ? value
            : 0;
        return number switch
        {
            >= 21 => Level.Fatal,
            >= 17 => Level.Error,
            >= 13 => Level.Warning,
            >= 9 => Level.Info,
            >= 1 => Level.Debug,
            _ => LevelFromText(GetString(record, "severityText")),
        };
    }

    private static Level LevelFromText(string? text) => text?.ToLowerInvariant() switch
    {
        "fatal" or "critical" => Level.Fatal,
        "error" => Level.Error,
        "warn" or "warning" => Level.Warning,
        "info" => Level.Info,
        "debug" or "trace" => Level.Debug,
        _ => Level.Unspecified,
    };

    // OTLP attributes are a repeated KeyValue: [{ "key": ..., "value": <AnyValue> }].
    private static IReadOnlyDictionary<string, string> Attributes(JsonElement owner)
    {
        var map = new Dictionary<string, string>();
        if (!owner.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Array)
        {
            return map;
        }
        foreach (var attr in attrs.EnumerateArray())
        {
            if (GetString(attr, "key") is { } key && attr.TryGetProperty("value", out var value))
            {
                map[key] = AnyValueToString(value);
            }
        }
        return map;
    }

    private static string? AnyValueString(JsonElement owner, string field) =>
        owner.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Object
            ? AnyValueToString(value)
            : null;

    // An OTLP AnyValue: one of stringValue / intValue (a JSON string) / boolValue / doubleValue /
    // arrayValue / kvlistValue / bytesValue. Rendered to a readable string for storage.
    private static string AnyValueToString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return value.ToString();
        }
        if (value.TryGetProperty("stringValue", out var s) && s.ValueKind == JsonValueKind.String)
        {
            return s.GetString() ?? "";
        }
        if (value.TryGetProperty("intValue", out var i))
        {
            return i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : i.GetRawText();
        }
        if (value.TryGetProperty("boolValue", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return b.GetBoolean() ? "true" : "false";
        }
        if (value.TryGetProperty("doubleValue", out var d) && d.ValueKind == JsonValueKind.Number)
        {
            return d.GetRawText();
        }
        // arrayValue / kvlistValue / bytesValue: keep the raw JSON so nothing is silently lost.
        return value.GetRawText();
    }

    // 64-bit nanosecond timestamps are proto3-JSON-encoded as decimal strings; tolerate a raw number too.
    private static long? UnixNanoToMs(JsonElement record, string field)
    {
        if (!record.TryGetProperty(field, out var value))
        {
            return null;
        }
        long? nanos = value.ValueKind switch
        {
            JsonValueKind.String when long.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            _ => null,
        };
        return nanos is > 0 ? nanos / 1_000_000 : null;
    }

    private static string? HexString(JsonElement record, string field) =>
        GetString(record, field) is { Length: > 0 } hex ? hex : null;

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
