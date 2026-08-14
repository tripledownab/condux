using System.Text.Json;

namespace Condux.Agent;

/// <summary>
/// Turns a model API error body into the one line a person needs. Both providers answer errors as JSON
/// envelopes — Anthropic <c>{"type":"error","error":{"type":...,"message":...}}</c>, OpenAI-compatible
/// <c>{"error":{"message":...,"type":...}}</c> — and dumping the raw blob into an exception made the fix
/// panel read like a wire capture when all it needed to say was "invalid x-api-key". One implementation,
/// because four call sites had already copied the raw-dump pattern.
/// </summary>
internal static class ModelApiError
{
    /// <summary>The error's type and message when the body parses as either envelope, else the raw body
    /// truncated. Never throws: this runs while reporting a failure, and a parse error here would replace
    /// the real problem with a JSON complaint.</summary>
    public static string Describe(string body)
    {
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                var type = error.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;
                return string.IsNullOrEmpty(type) ? message.GetString()! : $"{type}: {message.GetString()}";
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the raw body.
        }

        return Truncate(body, 500);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
