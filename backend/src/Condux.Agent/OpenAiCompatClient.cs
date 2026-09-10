using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Condux.Core.Http;

namespace Condux.Agent;

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

    public bool SupportsTools => true;

    public async Task<ModelOutput> CreateAsync(
        string model, string system, string user, string apiKey, string baseUrl, CancellationToken ct = default)
    {
        // The org supplies this and the conductor runs inside the deployment, so an unchecked value is a
        // request we would make on the org's behalf from the inside. Checked HERE rather than where the
        // config is read, because this is the only client that uses it: the Anthropic one builds its URL
        // from its own configured host. It is checked at the store too, but a row written before that
        // check existed was never validated, and those are the rows that would carry a bad value.
        //
        // IsAbsoluteHttp, not LlmUrls.IsValidBaseUrl: the latter permits an ABSENT value, because a
        // provider with a fixed endpoint ignores it. Here the value is the endpoint, so absent is a
        // failure like any other malformed one.
        if (!HttpUrls.IsAbsoluteHttp(baseUrl))
        {
            throw new InvalidOperationException(
                "The organization's model provider base URL is not an absolute http(s) URL.");
        }

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
                $"OpenAI-compatible API returned {(int)resp.StatusCode}: {ModelApiError.Describe(detail)}");
        }

        var parsed = await resp.Content.ReadFromJsonAsync<Response>(ct)
            ?? throw new InvalidOperationException("OpenAI-compatible API returned an empty response.");
        var text = parsed.Choices.FirstOrDefault()?.Message?.Content ?? "";
        return text.Length > 0
            ? new ModelOutput(text, parsed.Usage?.PromptTokens ?? 0, parsed.Usage?.CompletionTokens ?? 0)
            : throw new InvalidOperationException("OpenAI-compatible API returned no text output.");
    }


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
