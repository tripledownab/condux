using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Condux.Conductor;

/// <summary>
/// A thin <see cref="IModelClient"/> for any OpenAI-compatible Chat Completions endpoint (OpenAI, Azure
/// OpenAI, vLLM, Ollama, LiteLLM, ...): one call, the system + user prompt in, the assistant text +
/// token usage out. The base URL (through <c>/v1</c>) and Bearer key come per call from the org's BYO
/// config (#66) — the key is decrypted in-process, never on the wire.
/// </summary>
public sealed class OpenAiCompatClient(HttpClient http) : IModelClient
{
    private const int MaxOutputTokens = 8192;

    public string Provider => "openai-compat";

    public async Task<ModelOutput> CreateAsync(
        string model, string system, string user, string apiKey, string baseUrl, CancellationToken ct = default)
    {
        var body = new Request(
            model, MaxOutputTokens, [new Message("system", system), new Message("user", user)]);
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"OpenAI-compatible API returned {(int)resp.StatusCode}: {Truncate(detail, 500)}");
        }

        var parsed = await resp.Content.ReadFromJsonAsync<Response>(ct)
            ?? throw new InvalidOperationException("OpenAI-compatible API returned an empty response.");
        var text = parsed.Choices.FirstOrDefault()?.Message?.Content ?? "";
        return text.Length > 0
            ? new ModelOutput(text, parsed.Usage?.PromptTokens ?? 0, parsed.Usage?.CompletionTokens ?? 0)
            : throw new InvalidOperationException("OpenAI-compatible API returned no text output.");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private sealed record Request(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("messages")] IReadOnlyList<Message> Messages);

    private sealed record Message(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record Response(
        [property: JsonPropertyName("choices")] IReadOnlyList<Choice> Choices,
        [property: JsonPropertyName("usage")] Usage? Usage);

    private sealed record Choice([property: JsonPropertyName("message")] ResponseMessage? Message);

    private sealed record ResponseMessage([property: JsonPropertyName("content")] string? Content);

    private sealed record Usage(
        [property: JsonPropertyName("prompt_tokens")] long PromptTokens,
        [property: JsonPropertyName("completion_tokens")] long CompletionTokens);
}
