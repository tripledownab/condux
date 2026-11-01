using Condux.Core.Secrets;
using Condux.Core.Tickets;
using Condux.Linear;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Tickets;

/// <summary>
/// Revokes an org's Linear grant at Linear, best effort, before the row holding it is removed. Shared by
/// disconnect and by deleting an org, which cascades the row away and would otherwise forget a grant that
/// still works. Both tokens are revoked, since Linear documents a hint for each and does not say that
/// revoking one ends the other.
/// </summary>
internal static class LinearRevocation
{
    /// <summary>True when Linear confirmed the refresh token is revoked. False also covers Linear not being
    /// configured on this deployment, in which case nothing could be called.</summary>
    public static async Task<bool> TryRevokeAsync(IServiceProvider services, TrackerConnection connection, CancellationToken ct)
    {
        if (connection.Provider != TicketProvider.Linear
            || services.GetService<LinearOAuth>() is not { } oauth || services.GetService<SecretBox>() is not { } secrets)
        {
            return false;
        }
        var refreshRevoked = await oauth.RevokeAsync(secrets.Open(connection.RefreshTokenSealed), "refresh_token", ct);
        await oauth.RevokeAsync(secrets.Open(connection.AccessTokenSealed), "access_token", ct);
        return refreshRevoked;
    }

    /// <summary>Revoke the org's grant if it has one, ahead of the org being deleted. Linear refusing or not
    /// answering does not stop the deletion; failing to read our own database does, as it would anyway.</summary>
    public static async Task TryRevokeOrgAsync(
        IServiceProvider services, TrackerConnectionRepository connections, long orgId, CancellationToken ct)
    {
        if (await connections.GetAsync(orgId, TicketProvider.Linear, ct) is { } connection)
        {
            await TryRevokeAsync(services, connection, ct);
        }
    }
}
