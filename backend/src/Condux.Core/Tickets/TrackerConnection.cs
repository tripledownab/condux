namespace Condux.Core.Tickets;

/// <summary>An org's grant to one tracker workspace, as stored: the tokens are sealed with
/// <see cref="Secrets.SecretBox"/> and opened only by the sink that uses them.</summary>
public sealed record TrackerConnection(
    long OrgId, TicketProvider Provider, string WorkspaceId, string WorkspaceName,
    byte[] AccessTokenSealed, byte[] RefreshTokenSealed, DateTimeOffset AccessExpiresAt,
    DateTimeOffset? BrokenAt);

/// <summary>
/// Where tracker grants are kept. The contract lives in Core so a sink can renew its own tokens without
/// referencing the storage project. The two writes after a refresh are guarded on the refresh token the
/// caller read, because Linear replaces it on every refresh: two processes refreshing at once must not
/// overwrite each other, and one holding a stale token must not mark a healthy connection broken.
/// </summary>
public interface ITrackerConnectionStore
{
    Task<TrackerConnection?> GetAsync(long orgId, TicketProvider provider, CancellationToken cancellationToken = default);

    /// <summary>Store the renewed pair, only if the stored refresh token is still
    /// <paramref name="replacedRefreshSealed"/>. False means another process renewed first.</summary>
    Task<bool> ReplaceTokensAsync(
        long orgId, TicketProvider provider, byte[] replacedRefreshSealed, byte[] accessSealed,
        byte[] refreshSealed, DateTimeOffset accessExpiresAt, CancellationToken cancellationToken = default);

    /// <summary>Mark the grant broken, only if the stored refresh token is still the one that was refused.</summary>
    Task<bool> MarkBrokenAsync(
        long orgId, TicketProvider provider, byte[] refusedRefreshSealed, CancellationToken cancellationToken = default);
}
