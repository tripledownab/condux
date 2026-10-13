using Condux.Core.Auth;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// Resolves the project a CI request acts for from its release token, the per-project machine credential
/// every token-authed upload and the release recorder accept. One home, so a new CI endpoint cannot hash
/// the bearer value its own way.
/// </summary>
internal static class ReleaseTokenAuth
{
    /// <summary>The project the request's <c>Bearer</c> release token belongs to, or null when the header
    /// is missing or names no live token.</summary>
    public static async Task<long?> ResolveProjectAsync(HttpContext http, ReleaseTokenRepository tokens)
    {
        if (BearerToken.From(http) is not { } raw)
        {
            return null;
        }

        return await tokens.ResolveProjectAsync(ReleaseTokens.HashToken(raw));
    }
}
