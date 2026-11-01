using Condux.Core.Secrets;
using Condux.Core.Tickets;

namespace Condux.Linear;

/// <summary>
/// Opens Linear issues for an org, renewing its grant on the way. A refresh runs when the access token has
/// expired, or once when Linear refuses one that had not, and its result is written back guarded on the
/// refresh token it replaced (<see cref="ITrackerConnectionStore"/>). Losing that race means another process
/// renewed first, so this one rereads the row and uses the winner's token rather than its own.
/// </summary>
public sealed class LinearTicketSink(
    ITrackerConnectionStore connections, SecretBox secrets, LinearOAuth oauth, LinearApi api,
    Func<DateTimeOffset> clock) : ITicketSink
{
    // Renew a little early, so a token does not expire between this check and Linear reading it.
    private static readonly TimeSpan ExpirySlack = TimeSpan.FromMinutes(5);

    private const TicketProvider Provider = TicketProvider.Linear;

    public async Task<TicketOutcome> CreateAsync(
        long orgId, string targetId, IReadOnlyList<string> labelIds, Guid idempotencyId, TicketContent content,
        CancellationToken ct = default)
    {
        var access = await GetAccessTokenAsync(orgId, forceRefresh: false, ct);
        if (access.Outcome is { } early)
        {
            return early;
        }
        try
        {
            return await api.CreateIssueAsync(access.Token!, targetId, labelIds, idempotencyId, content, ct);
        }
        catch (LinearUnauthorizedException)
        {
            var renewed = await GetAccessTokenAsync(orgId, forceRefresh: true, ct);
            if (renewed.Outcome is { } late)
            {
                return late;
            }
            try
            {
                return await api.CreateIssueAsync(renewed.Token!, targetId, labelIds, idempotencyId, content, ct);
            }
            catch (LinearUnauthorizedException)
            {
                // A token Linear has just issued and then refuses is not one more refresh will fix.
                return TicketOutcome.Fail(TicketFailure.ConnectionBroken);
            }
        }
    }

    /// <summary>A usable access token for the org, or the outcome that explains why there is none.</summary>
    public async Task<(string? Token, TicketOutcome? Outcome)> GetAccessTokenAsync(
        long orgId, bool forceRefresh, CancellationToken ct)
    {
        if (await connections.GetAsync(orgId, Provider, ct) is not { } conn)
        {
            return (null, TicketOutcome.Fail(TicketFailure.NotConnected));
        }
        if (conn.BrokenAt is not null)
        {
            return (null, TicketOutcome.Fail(TicketFailure.ConnectionBroken));
        }
        if (!forceRefresh && conn.AccessExpiresAt - ExpirySlack > clock())
        {
            return (secrets.Open(conn.AccessTokenSealed), null);
        }

        var refresh = await oauth.RefreshAsync(secrets.Open(conn.RefreshTokenSealed), ct);
        if (refresh.Tokens is { } tokens)
        {
            if (await connections.ReplaceTokensAsync(
                    orgId, Provider, conn.RefreshTokenSealed, secrets.Seal(tokens.AccessToken),
                    secrets.Seal(tokens.RefreshToken), tokens.ExpiresAt, ct))
            {
                return (tokens.AccessToken, null);
            }
            // Another process renewed first and its pair is the stored one. Ours was issued too, but the
            // row is the single record of which pair is current, so use what it holds.
            return await ReadWinnerAsync(orgId, ct);
        }
        if (refresh.Refused)
        {
            // Guarded on the token that was refused, so a process holding a stale one cannot break a
            // connection another process has already renewed. If the guard misses, that renewal stands.
            return await connections.MarkBrokenAsync(orgId, Provider, conn.RefreshTokenSealed, ct)
                ? (null, TicketOutcome.Fail(TicketFailure.ConnectionBroken))
                : await ReadWinnerAsync(orgId, ct);
        }
        return (null, TicketOutcome.RetryLater);
    }

    // Read once, never refresh again: a second renewal racing a third is how this would loop.
    private async Task<(string? Token, TicketOutcome? Outcome)> ReadWinnerAsync(long orgId, CancellationToken ct) =>
        await connections.GetAsync(orgId, Provider, ct) switch
        {
            null => (null, TicketOutcome.Fail(TicketFailure.NotConnected)),
            { BrokenAt: not null } => (null, TicketOutcome.Fail(TicketFailure.ConnectionBroken)),
            var winner => (secrets.Open(winner.AccessTokenSealed), null),
        };
}
