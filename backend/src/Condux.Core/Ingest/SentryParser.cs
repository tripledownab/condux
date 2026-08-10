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
public static class SentryParser
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

    // mechanism.handled distinguishes an unhandled crash from a caught-and-reported error; absent = null.
    private static bool? ParseHandled(JsonElement exItem) =>
        exItem.TryGetProperty("mechanism", out var mechanism) && mechanism.ValueKind == JsonValueKind.Object
        && mechanism.TryGetProperty("handled", out var handled)
        && handled.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? handled.GetBoolean()
            : null;

    private static string? ParseTraceId(JsonElement root) =>
        root.TryGetProperty("contexts", out var contexts) && contexts.ValueKind == JsonValueKind.Object
        && contexts.TryGetProperty("trace", out var trace) && trace.ValueKind == JsonValueKind.Object
            ? GetString(trace, "trace_id")
            : null;

    /// <summary>The string items of an array property; non-string items are skipped.</summary>
    private static IReadOnlyList<string> GetStringArray(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(item.GetString() ?? "");
            }
        }
        return list;
    }

    /// <summary>The frame's local variables (Sentry vars), values stringified whatever their JSON
    /// shape so nested structures survive as readable text.</summary>
    private static IReadOnlyDictionary<string, string> ParseVars(JsonElement frame)
    {
        if (!frame.TryGetProperty("vars", out var vars) || vars.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>();
        }
        var map = new Dictionary<string, string>();
        foreach (var property in vars.EnumerateObject())
        {
            map[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? ""
                : property.Value.GetRawText();
        }
        return map;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static bool GetBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long ParseTimestamp(JsonElement el)
    {
        if (!el.TryGetProperty("timestamp", out var ts))
        {
            return 0;
        }
        if (ts.ValueKind == JsonValueKind.Number)
        {
            return (long)(ts.GetDouble() * 1000);
        }
        if (ts.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(
                ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            return dto.ToUnixTimeMilliseconds();
        }
        return 0;
    }

    private static Level ParseLevel(string? s) => s?.ToLowerInvariant() switch
    {
        "debug" => Level.Debug,
        "info" => Level.Info,
        "warning" => Level.Warning,
        "error" => Level.Error,
        "fatal" => Level.Fatal,
        _ => Level.Unspecified,
    };

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

    private static IReadOnlyList<ExceptionValue> ParseExceptions(JsonElement root)
    {
        if (!TryGetValuesArray(root, "exception", out var values))
        {
            return [];
        }

        // A native crash reports its stack on the crashed thread, leaving the exception carrying only a
        // type and value, so an exception with no frames of its own borrows the thread's. Without this
        // an iOS or NDK crash stores with no stack trace at all.
        var threadStack = ParseCrashedThreadStacktrace(root);

        var list = new List<ExceptionValue>();
        foreach (var item in values.EnumerateArray())
        {
            var own = ParseStacktrace(item);
            list.Add(new ExceptionValue
            {
                Type = GetString(item, "type"),
                Value = GetString(item, "value"),
                Module = GetString(item, "module"),
                Stacktrace = own is { Frames.Count: > 0 } ? own : threadStack ?? own,
                Handled = ParseHandled(item),
            });
        }
        return list;
    }

    /// <summary>The stack of the thread flagged <c>crashed</c>, else the first thread carrying frames.</summary>
    private static Stacktrace? ParseCrashedThreadStacktrace(JsonElement root)
    {
        if (!TryGetValuesArray(root, "threads", out var threads))
        {
            return null;
        }

        Stacktrace? firstWithFrames = null;
        foreach (var thread in threads.EnumerateArray())
        {
            if (ParseStacktrace(thread) is not { Frames.Count: > 0 } stack)
            {
                continue;
            }
            if (GetBool(thread, "crashed"))
            {
                return stack;
            }
            firstWithFrames ??= stack;
        }
        return firstWithFrames;
    }

    private static Stacktrace? ParseStacktrace(JsonElement exItem)
    {
        if (!exItem.TryGetProperty("stacktrace", out var st) || st.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!st.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array)
        {
            return new Stacktrace();
        }

        var list = new List<Frame>();
        foreach (var f in frames.EnumerateArray())
        {
            list.Add(new Frame
            {
                Filename = GetString(f, "filename"),
                AbsPath = GetString(f, "abs_path"),
                Function = GetString(f, "function"),
                Module = GetString(f, "module"),
                Lineno = GetInt(f, "lineno"),
                Colno = GetInt(f, "colno"),
                InApp = GetBool(f, "in_app"),
                ContextLine = GetString(f, "context_line"),
                ContextBefore = GetStringArray(f, "pre_context"),
                ContextAfter = GetStringArray(f, "post_context"),
                Vars = ParseVars(f),
                Package = GetString(f, "package"),
                Symbol = GetString(f, "symbol"),
                InstructionAddr = GetString(f, "instruction_addr"),
                ImageAddr = GetString(f, "image_addr"),
                SymbolAddr = GetString(f, "symbol_addr"),
            });
        }
        return new Stacktrace { Frames = list };
    }

    // debug_meta.images[] links a debug id to the built file it identifies, so a frame can later be matched
    // to its uploaded source map (ADR-0028). Defensive: skips a missing/malformed debug_meta or image.
    private static IReadOnlyList<DebugImage> ParseDebugImages(JsonElement root)
    {
        if (!root.TryGetProperty("debug_meta", out var meta) || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<DebugImage>();
        foreach (var img in images.EnumerateArray())
        {
            if (img.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            list.Add(new DebugImage
            {
                Type = GetString(img, "type"),
                CodeFile = GetString(img, "code_file"),
                DebugId = GetString(img, "debug_id"),
            });
        }

        return list;
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

    // "exception"/"breadcrumbs" can be either {values:[...]} or a bare array.
    private static bool TryGetValuesArray(JsonElement root, string name, out JsonElement values)
    {
        values = default;
        if (!root.TryGetProperty(name, out var el))
        {
            return false;
        }
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Array)
        {
            values = v;
            return true;
        }
        if (el.ValueKind == JsonValueKind.Array)
        {
            values = el;
            return true;
        }
        return false;
    }

    private static IReadOnlyDictionary<string, string> ParseStringMap(JsonElement root, string name)
    {
        var dict = new Dictionary<string, string>();
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object)
        {
            return dict;
        }
        foreach (var prop in el.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString() ?? ""
                : prop.Value.GetRawText();
        }
        return dict;
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
            {
                list.Add(s);
            }
        }
        return list;
    }
}
