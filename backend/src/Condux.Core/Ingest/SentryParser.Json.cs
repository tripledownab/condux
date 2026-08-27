using System.Globalization;
using System.Text.Json;
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
