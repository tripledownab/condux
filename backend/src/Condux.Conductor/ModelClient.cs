namespace Condux.Conductor;

/// <summary>The model's reply: its text plus the token usage the API reported, which feeds the per-run
/// cost metering (#100).</summary>
public sealed record ModelOutput(string Text, long InputTokens, long OutputTokens);

/// <summary>
/// One model provider the Conductor can ask for a fix plan. Each provider (Anthropic today, an
/// OpenAI-compatible endpoint next, #66) is one implementation; the gateway picks the one whose
/// <see cref="Provider"/> matches the run's resolved config. The API key and base URL are passed per
/// call so a run can use an org's BYO config (#65) — the key is decrypted in-process, never on the wire.
/// </summary>
public interface IModelClient
{
    /// <summary>The provider key this client handles ("anthropic", "openai-compat", ...), matching the
    /// stored config's provider.</summary>
    string Provider { get; }

    Task<ModelOutput> CreateAsync(
        string model, string system, string user, string apiKey, string baseUrl,
        CancellationToken cancellationToken = default);
}
