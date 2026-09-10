using Condux.Core.Llm;
using Condux.Core.Secrets;

namespace Condux.Agent;

/// <summary>The effective model call config for one run: which provider client to use, the decrypted
/// key, the model, and the base URL (empty for Anthropic; the endpoint for an OpenAI-compatible one).</summary>
public sealed record ResolvedLlm(string Provider, string ApiKey, string Model, string BaseUrl);

/// <summary>
/// Resolves the provider + key + model for a fix run (#65/#66): an org's BYO-key config when it has one
/// (any supported provider), otherwise the default this process was started with. The key is decrypted
/// in the process that runs the fix and never rides the job. With no registry to read (<c>configs</c> and
/// <c>box</c> null — the secret store is unconfigured, or the process is a customer's runner holding its
/// own key) every run uses that default.
/// </summary>
public sealed class ModelKeyResolver(
    string defaultApiKey, ILlmConfigReader? configs, SecretBox? box,
    string defaultProvider = "anthropic", string defaultBaseUrl = "")
{
    public async Task<ResolvedLlm> ResolveAsync(
        long orgId, string defaultModel, CancellationToken cancellationToken = default)
    {
        if (configs is not null && box is not null && orgId != 0
            && await configs.GetAsync(orgId, cancellationToken) is { } config)
        {
            // The base URL is NOT checked here. It is checked in OpenAiCompatClient, which is the only
            // client that uses it: the Anthropic client builds its URL from its own configured host and
            // ignores this value entirely, so rejecting it here would fail every fix run for an org whose
            // stored row happens to carry a stale base URL that nothing would ever have fetched.
            return new ResolvedLlm(
                config.Provider, box.Open(config.KeyEncrypted), config.Model, config.BaseUrl);
        }
        return new ResolvedLlm(defaultProvider, defaultApiKey, defaultModel, defaultBaseUrl);
    }
}
