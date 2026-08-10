using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Condux.Conductor;

/// <summary>Anthropic API config for the Conductor. The key comes from CONDUX_ANTHROPIC_API_KEY and is
/// used server-side only — it is never sent to GitHub, stored, or logged.</summary>
public sealed record AnthropicOptions(string ApiKey)
{
    public string BaseUrl { get; init; } = "https://api.anthropic.com";
}

/// <summary>
/// A thin <see cref="IModelClient"/> for the Anthropic Messages API — one call: send the fix prompt,
/// get the model's text back. Adaptive thinking is enabled (the fix is a hard reasoning task); thinking
/// blocks in the response are skipped and only text blocks are returned. Kept as a plain HttpClient
/// layer (like Condux.GitHub). Anthropic's endpoint is fixed, so the per-call base URL is ignored.
/// </summary>
public sealed class AnthropicMessagesClient(HttpClient http, AnthropicOptions options) : IModelClient
{
    private const int MaxOutputTokens = 8192;

    public string Provider => "anthropic";

    /// <summary>Send one user message under a system prompt and return the text output + token usage.
    /// The API key is passed per call so a run can use the org's BYO key (#65).</summary>
    public async Task<ModelOutput> CreateAsync(
        string model, string system, string user, string apiKey, string baseUrl,
        CancellationToken ct = default)
    {
        var body = new Request(
            model, MaxOutputTokens, new Thinking("adaptive"), system, [new Message("user", user)]);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl}/v1/messages");
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Anthropic API returned {(int)resp.StatusCode}: {Truncate(detail, 500)}");
        }

        var parsed = await resp.Content.ReadFromJsonAsync<Response>(ct)
            ?? throw new InvalidOperationException("Anthropic API returned an empty response.");
        var text = string.Concat(
            parsed.Content.Where(b => b.Type == "text").Select(b => b.Text));
        return text.Length > 0
            ? new ModelOutput(text, parsed.Usage?.InputTokens ?? 0, parsed.Usage?.OutputTokens ?? 0)
            : throw new InvalidOperationException(
                $"Anthropic API returned no text output (stop_reason: {parsed.StopReason}).");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private sealed record Request(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("thinking")] Thinking Thinking,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("messages")] IReadOnlyList<Message> Messages);

    private sealed record Thinking([property: JsonPropertyName("type")] string Type);

    private sealed record Message(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record Response(
        [property: JsonPropertyName("content")] IReadOnlyList<ContentBlock> Content,
        [property: JsonPropertyName("stop_reason")] string? StopReason,
        [property: JsonPropertyName("usage")] Usage? Usage);

    private sealed record Usage(
        [property: JsonPropertyName("input_tokens")] long InputTokens,
        [property: JsonPropertyName("output_tokens")] long OutputTokens);

    private sealed record ContentBlock(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);
}
