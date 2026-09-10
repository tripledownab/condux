namespace Condux.Storage.Postgres;

/// <summary>
/// Mints, lists, resolves and revokes runner tokens (a customer-hosted runner leases work and reports
/// results with them, ADR-0033 slice 4).
///
/// Scoped to an org rather than a project, which is the only structural difference from the other token
/// kinds: a runner serves whatever work its org produces, across every project in it.
/// </summary>
public sealed class RunnerTokenRepository(string connectionString)
{
    private readonly ScopedTokenRepository tokens = new(connectionString, "runner_tokens", "org_id");

    public Task<ScopedTokenRow> CreateAsync(
        long orgId, string tokenHash, string label, CancellationToken ct = default) =>
        tokens.CreateAsync(orgId, tokenHash, label, ct: ct);

    public Task<IReadOnlyList<ScopedTokenRow>> ListByOrgAsync(long orgId, CancellationToken ct = default) =>
        tokens.ListAsync(orgId, ct);

    /// <summary>The org a live token belongs to (null if unknown or revoked); stamps last use.</summary>
    public Task<long?> ResolveOrgAsync(string tokenHash, CancellationToken ct = default) =>
        tokens.ResolveScopeAsync(tokenHash, ct);

    public Task<bool> RevokeAsync(long orgId, Guid id, CancellationToken ct = default) =>
        tokens.RevokeAsync(orgId, id, ct);
}
