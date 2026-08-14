namespace Condux.Core.Llm;

/// <summary>An org's stored LLM provider config (#65). <c>KeyEncrypted</c> is the sealed API key
/// (never the plaintext); only the process that runs the fix decrypts it, at fix time.</summary>
public sealed record StoredLlmConfig(
    long OrgId, string Provider, string Model, string BaseUrl, byte[] KeyEncrypted, DateTimeOffset UpdatedAt);

/// <summary>
/// Read side of the BYO-key registry, so resolving an org's provider needs neither the write side nor a
/// database: the agent core depends on this contract and a customer-hosted runner supplies none at all,
/// resolving its own key locally instead.
/// </summary>
public interface ILlmConfigReader
{
    Task<StoredLlmConfig?> GetAsync(long orgId, CancellationToken cancellationToken = default);
}
