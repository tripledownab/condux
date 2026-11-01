using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Condux.Core.Events;

namespace Condux.Core.Ingest;

/// <summary>Reading the loosely-typed Sentry JSON: scalars, arrays and maps that may be absent, of
/// the wrong kind, or shaped two different ways. Every reader here answers with a default rather than
/// throwing, because one odd field must not lose the whole event.</summary>
public static partial class SentryParser
{
    /// <summary>The string items of an array property; non-string items are skipped.</summary>
    private static IReadOnlyList<string> GetStringArray(JsonElement el, string name)
    {
        if (!el.TryGetMember(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
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

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetMember(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement el, string name) =>
        el.TryGetMember(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static bool GetBool(JsonElement el, string name) =>
        el.TryGetMember(name, out var v) && v.ValueKind == JsonValueKind.True;

    // The envelope spec makes sent_at an RFC 3339 string in UTC, and Sentry's relay reads it with chrono's
    // relaxed RFC 3339 parser, which wants a date, a time and an offset. Anything looser would read a bare
    // date as midnight and shift the event by up to a day, so a value of another shape is not read at all.
    // Sentry refuses the whole envelope over one; dropping only the correction loses less.
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+-]\d{2}:\d{2})$")]
    private static partial Regex Rfc3339();

    // The envelope header is already known to be a JSON object, the reader checked it.
    private static long ParseSentAt(byte[] header)
    {
        using var doc = JsonDocument.Parse(header);
        return doc.RootElement.TryGetMember("sent_at", out var sentAt)
            && sentAt.ValueKind == JsonValueKind.String
            && sentAt.GetString() is { } text
            && Rfc3339().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? at.ToUnixTimeMilliseconds()
                : 0;
    }

    private static long ParseTimestamp(JsonElement el)
    {
        if (!el.TryGetMember("timestamp", out var ts))
        {
            return 0;
        }
        if (ts.ValueKind == JsonValueKind.Number)
        {
            return ToStorableMs(ts.GetDouble() * 1000);
        }
        // TryParse already answers only within DateTimeOffset's range, so the string form needs no bound.
        if (ts.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(
                ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            return dto.ToUnixTimeMilliseconds();
        }
        return 0;
    }

    // 0 is what this parser already answers for a timestamp that is absent or unreadable, so an
    // out-of-range one joins them instead of becoming a third outcome nothing downstream expects. The
    // cast is guarded rather than direct because a large double saturates to long.MaxValue, which is a
    // number, is positive, and is not a time. The comparison is the whole check: NaN and the infinities
    // fail it too, so an explicit test for them would be a branch that never decides anything.
    private static long ToStorableMs(double ms) =>
        ms >= EventTimes.MinStorableMs && ms <= EventTimes.MaxStorableMs ? (long)ms : 0;

    // A level as Sentry's own ingest reads one: the five documented names plus its aliases "log" and
    // "critical", or a Python logging number (10 to 50), which that ingest also reads. Null when absent or
    // unrecognised, because the protocol gives an event and a breadcrumb different defaults.
    private static Level? ParseLevel(JsonElement el)
    {
        if (!el.TryGetMember("level", out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.String => ToLevel(v.GetString()),
            JsonValueKind.Number when v.TryGetInt64(out var n) => ToLevel(n),
            _ => null,
        };
    }

    private static Level? ToLevel(string? name) => name?.ToLowerInvariant() switch
    {
        "debug" => Level.Debug,
        "info" or "log" => Level.Info,
        "warning" => Level.Warning,
        "error" => Level.Error,
        "fatal" or "critical" => Level.Fatal,
        _ => null,
    };

    private static Level? ToLevel(long pythonLevel) => pythonLevel switch
    {
        10 => Level.Debug,
        20 => Level.Info,
        30 => Level.Warning,
        40 => Level.Error,
        50 => Level.Fatal,
        _ => null,
    };


    // "exception"/"breadcrumbs" can be either {values:[...]} or a bare array.
    private static bool TryGetValuesArray(JsonElement root, string name, out JsonElement values)
    {
        values = default;
        if (!root.TryGetMember(name, out var el))
        {
            return false;
        }
        if (el.ValueKind == JsonValueKind.Object && el.TryGetMember("values", out var v) && v.ValueKind == JsonValueKind.Array)
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
        if (!root.TryGetMember(name, out var el) || el.ValueKind != JsonValueKind.Object)
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
        if (!root.TryGetMember(name, out var el) || el.ValueKind != JsonValueKind.Array)
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
