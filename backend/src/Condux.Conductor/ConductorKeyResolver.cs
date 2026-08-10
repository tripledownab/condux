using Condux.Core.Secrets;
using Condux.Storage.Postgres;

namespace Condux.Conductor;

/// <summary>The effective model call config for one run: which provider client to use, the decrypted
/// key, the model, and the base URL (empty for Anthropic; the endpoint for an OpenAI-compatible one).</summary>
public sealed record ResolvedLlm(string Provider, string ApiKey, string Model, string BaseUrl);

/// <summary>
/// Resolves the provider + key + model for a fix run (#65/#66): an org's BYO-key config when it has one
/// (any supported provider), otherwise the platform default (Anthropic). The key is decrypted here
/// inside the Conductor — it never rides the Kafka job. When the secret store isn't configured (no
/// <c>CONDUX_SECRET_KEY</c>, so <c>configs</c> and <c>box</c> are null) every run uses the default,
/// exactly as before BYO-key existed.
/// </summary>
public sealed class ConductorKeyResolver(string defaultApiKey, ILlmConfigReader? configs, SecretBox? box)
{
    public async Task<ResolvedLlm> ResolveAsync(
        long orgId, string defaultModel, CancellationToken cancellationToken = default)
    {
        if (configs is not null && box is not null && orgId != 0
            && await configs.GetAsync(orgId, cancellationToken) is { } config)
        {
            return new ResolvedLlm(
                config.Provider, box.Open(config.KeyEncrypted), config.Model, config.BaseUrl);
        }
        return new ResolvedLlm("anthropic", defaultApiKey, defaultModel, "");
    }
}
