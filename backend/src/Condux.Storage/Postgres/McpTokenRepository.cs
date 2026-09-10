namespace Condux.Storage.Postgres;

/// <summary>
/// Mints, lists, resolves and revokes scoped MCP tokens (an AI agent reads a project's issues with them over POST /api/mcp).
///
/// Every operation is <see cref="ScopedTokenRepository"/>'s; this type names the table it lives in, the
/// column it is scoped by and the column holding what it may do, and nothing else.
/// </summary>
public sealed class McpTokenRepository(string connectionString)
{
    private readonly ScopedTokenRepository tokens =
        new(connectionString, "mcp_tokens", "project_id", "capability");

    public Task<ScopedTokenRow> CreateAsync(
        long projectId, string tokenHash, string name, int capability = 0, CancellationToken ct = default) =>
        tokens.CreateAsync(projectId, tokenHash, name, capability, ct);

    public Task<IReadOnlyList<ScopedTokenRow>> ListByProjectAsync(long projectId, CancellationToken ct = default) =>
        tokens.ListAsync(projectId, ct);

    /// <summary>
    /// The live token behind a presented hash: its project, its row id and its capability (null if
    /// unknown or revoked). The MCP endpoint needs all three, because a tool call is gated by the
    /// capability and an MCP-written note is attributed to the token id.
    /// </summary>
    public Task<ScopedTokenIdentity?> ResolveIdentityAsync(string tokenHash, CancellationToken ct = default) =>
        tokens.ResolveIdentityAsync(tokenHash, ct);

    public Task<bool> RevokeAsync(long projectId, Guid id, CancellationToken ct = default) =>
        tokens.RevokeAsync(projectId, id, ct);
}
