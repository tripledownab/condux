using System.Globalization;
using System.Text;
using System.Text.Json;
using Condux.Core.Events;

namespace Condux.Core.Ingest;

/// <summary>
/// Parses Sentry-SDK payloads into the internal <see cref="Event"/> model — both the
/// classic <c>/store/</c> single-event JSON and the newline-delimited <c>/envelope/</c>
/// format — so existing Sentry SDKs work by only swapping the DSN.
/// </summary>
/// <remarks>Both entry points report a <see cref="ParseOutcome"/> rather than a nullable event, so the
/// caller can tell "carried nothing to store" from "could not be parsed" and answer each correctly.
/// Collapsing the two loses malformed events silently.</remarks>
public static partial class SentryParser
{
    /// <summary>Parse a <c>/store/</c> payload: one JSON event object.</summary>
    public static ParseResult ParseStore(ReadOnlySpan<byte> json) => FromPayload(json.ToArray());

    /// <inheritdoc cref="ParseStore(ReadOnlySpan{byte})"/>
    public static ParseResult ParseStore(string json) => FromPayload(Encoding.UTF8.GetBytes(json));

    /// <summary>Parse an <c>/envelope/</c> payload, returning its first event item.</summary>
    public static ParseResult ParseEnvelope(ReadOnlySpan<byte> body)
    {
        var outcome = SentryEnvelopeReader.TryFindEventPayload(body, out var payload);
        return outcome switch
        {
            ParseOutcome.Parsed => FromPayload(payload),
            ParseOutcome.Malformed => ParseResult.Malformed,
            _ => ParseResult.NoEvent,
        };
    }

    /// <inheritdoc cref="ParseEnvelope(ReadOnlySpan{byte})"/>
    public static ParseResult ParseEnvelope(string body) => ParseEnvelope(Encoding.UTF8.GetBytes(body));

    private static ParseResult FromPayload(byte[] payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? ParseResult.Parsed(FromRoot(doc.RootElement))
                : ParseResult.Malformed;
        }
        catch (JsonException)
        {
            return ParseResult.Malformed;
        }
    }

    private static Event FromRoot(JsonElement root)
    {
        string? sdkName = null;
        string? sdkVersion = null;
        if (root.TryGetProperty("sdk", out var sdk) && sdk.ValueKind == JsonValueKind.Object)
        {
            sdkName = GetString(sdk, "name");
            sdkVersion = GetString(sdk, "version");
        }

        return new Event
        {
            EventId = GetString(root, "event_id") ?? "",
            TimestampUnixMs = ParseTimestamp(root),
            Platform = GetString(root, "platform"),
            Level = ParseLevel(GetString(root, "level")),
            Logger = GetString(root, "logger"),
            ServerName = GetString(root, "server_name"),
            Release = GetString(root, "release"),
            Environment = GetString(root, "environment"),
            Transaction = GetString(root, "transaction"),
            Message = ParseMessage(root),
            Exceptions = ParseExceptions(root),
            Breadcrumbs = ParseBreadcrumbs(root),
            Tags = ParseStringMap(root, "tags"),
            Extra = ParseStringMap(root, "extra"),
            Fingerprint = ParseStringArray(root, "fingerprint"),
            SdkName = sdkName,
            SdkVersion = sdkVersion,
            User = ParseUser(root),
            Request = ParseRequest(root),
            Contexts = ParseContexts(root),
            Dist = GetString(root, "dist"),
            TraceId = ParseTraceId(root),
            Modules = ParseStringMap(root, "modules"),
            DebugImages = ParseDebugImages(root),
        };
    }

    private static EventUser? ParseUser(JsonElement root)
    {
        if (!root.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new EventUser
        {
            Id = GetString(user, "id"),
            Username = GetString(user, "username"),
            Email = GetString(user, "email"),
            IpAddress = GetString(user, "ip_address"),
        };
    }

    private static RequestInfo? ParseRequest(JsonElement root)
    {
        if (!root.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new RequestInfo
        {
            Url = GetString(request, "url"),
            Method = GetString(request, "method"),
            QueryString = GetString(request, "query_string"),
            Headers = ParseStringMap(request, "headers"),
        };
    }

    // Flatten the well-known contexts to facet-friendly "name version" strings; trace is handled apart.
    private static IReadOnlyDictionary<string, string> ParseContexts(JsonElement root)
    {
        var dict = new Dictionary<string, string>();
        if (!root.TryGetProperty("contexts", out var contexts) || contexts.ValueKind != JsonValueKind.Object)
        {
            return dict;
        }

        foreach (var name in (string[])["browser", "os", "runtime", "device"])
        {
            if (contexts.TryGetProperty(name, out var ctx) && ctx.ValueKind == JsonValueKind.Object)
            {
                var label = GetString(ctx, "name") ?? GetString(ctx, "model");
                var version = GetString(ctx, "version");
                if (!string.IsNullOrEmpty(label))
                {
                    dict[name] = string.IsNullOrEmpty(version) ? label : $"{label} {version}";
                }
            }
        }

        return dict;
    }

    private static string? ParseTraceId(JsonElement root) =>
        root.TryGetProperty("contexts", out var contexts) && contexts.ValueKind == JsonValueKind.Object
        && contexts.TryGetProperty("trace", out var trace) && trace.ValueKind == JsonValueKind.Object
            ? GetString(trace, "trace_id")
            : null;

    private static string? ParseMessage(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var m))
        {
            return null;
        }
        if (m.ValueKind == JsonValueKind.String)
        {
            return m.GetString();
        }
        if (m.ValueKind == JsonValueKind.Object)
        {
            return GetString(m, "formatted") ?? GetString(m, "message");
        }
        return null;
    }
    private static IReadOnlyList<Breadcrumb> ParseBreadcrumbs(JsonElement root)
    {
        if (!TryGetValuesArray(root, "breadcrumbs", out var values))
        {
            return [];
        }

        var list = new List<Breadcrumb>();
        foreach (var item in values.EnumerateArray())
        {
            list.Add(new Breadcrumb
            {
                TimestampUnixMs = ParseTimestamp(item),
                Type = GetString(item, "type"),
                Category = GetString(item, "category"),
                Message = GetString(item, "message"),
                Level = ParseLevel(GetString(item, "level")),
                Data = ParseStringMap(item, "data"),
            });
        }
        return list;
    }
}
