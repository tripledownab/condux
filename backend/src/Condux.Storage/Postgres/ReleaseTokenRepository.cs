namespace Condux.Storage.Postgres;

/// <summary>
/// Mints, lists, resolves and revokes scoped release tokens (CI records releases and uploads source maps with them).
///
/// Every operation is <see cref="ScopedTokenRepository"/>'s; this type names the table it lives in and
/// the column it is scoped by, and nothing else.
/// </summary>
public sealed class ReleaseTokenRepository(string connectionString)
{
    private readonly ScopedTokenRepository tokens = new(connectionString, "release_tokens", "project_id");

    public Task<ScopedTokenRow> CreateAsync(
        long projectId, string tokenHash, string name, CancellationToken ct = default) =>
        tokens.CreateAsync(projectId, tokenHash, name, ct: ct);

    public Task<IReadOnlyList<ScopedTokenRow>> ListByProjectAsync(long projectId, CancellationToken ct = default) =>
        tokens.ListAsync(projectId, ct);

    /// <summary>The project a live token belongs to (null if unknown or revoked); stamps last use.</summary>
    public Task<long?> ResolveProjectAsync(string tokenHash, CancellationToken ct = default) =>
        tokens.ResolveScopeAsync(tokenHash, ct);

    public Task<bool> RevokeAsync(long projectId, Guid id, CancellationToken ct = default) =>
        tokens.RevokeAsync(projectId, id, ct);
}
