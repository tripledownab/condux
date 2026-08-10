using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Condux.ControlPlane.Llm;

/// <summary>The providers the BYO-key registry supports. Bedrock/Vertex (#67) need SDK-based auth
/// rather than a bearer key, so they are a separate slice behind this check.</summary>
public static class LlmProviders
{
    public const string Anthropic = "anthropic";
    public const string OpenAiCompatible = "openai-compat";

    public static bool IsSupported(string provider) =>
        provider is Anthropic or OpenAiCompatible;

    /// <summary>OpenAI-compatible endpoints need a base URL; Anthropic's is fixed.</summary>
    public static bool RequiresBaseUrl(string provider) => provider == OpenAiCompatible;
}

/// <summary>A model the org's key can use, for the settings model picker.</summary>
public sealed record LlmModel(string Id, string DisplayName);

/// <summary>Lists the models a BYO key can use, which doubles as validation — a live, working key
/// yields models; a bad key or unreachable provider yields <c>null</c>. Injectable so tests do not call
/// a real provider.</summary>
public interface ILlmKeyValidator
{
    Task<IReadOnlyList<LlmModel>?> ListModelsAsync(
        string provider, string baseUrl, string apiKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lists models via each provider's models endpoint (Anthropic <c>GET /v1/models</c> with
/// <c>x-api-key</c>; an OpenAI-compatible <c>GET {baseUrl}/models</c> with a Bearer key). Both return a
/// <c>data[]</c> of objects with an <c>id</c> (Anthropic also carries <c>display_name</c>), so one parse
/// handles both. The <see cref="HttpClient"/> comes from <c>IHttpClientFactory</c>.
/// </summary>
public sealed class LlmKeyValidator(HttpClient http) : ILlmKeyValidator
{
    public async Task<IReadOnlyList<LlmModel>?> ListModelsAsync(
        string provider, string baseUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage { Method = HttpMethod.Get };
        if (provider == LlmProviders.Anthropic)
        {
            request.RequestUri = new Uri("https://api.anthropic.com/v1/models?limit=1000");
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else if (provider == LlmProviders.OpenAiCompatible && !string.IsNullOrEmpty(baseUrl))
        {
            request.RequestUri = new Uri($"{baseUrl.TrimEnd('/')}/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        else
        {
            return null;
        }

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<ModelsResponse>(
                stream, cancellationToken: cancellationToken);
            return [.. (payload?.Data ?? []).Select(m => new LlmModel(m.Id, m.DisplayName ?? m.Id))];
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ModelsResponse([property: JsonPropertyName("data")] List<ModelInfo>? Data);

    private sealed record ModelInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("display_name")] string? DisplayName);
}
