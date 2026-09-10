using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// Platform-operator (super-admin) identity + gate for the <c>/api/admin</c> surface. Membership is an
/// env allowlist (<c>CONDUX_PLATFORM_ADMIN_EMAILS</c>) that the auth handler resolves to a claim on
/// every request — never a database flag, so it cannot be escalated by a data write and always tracks
/// the current config. A caller who isn't a platform admin gets 404 (the surface stays hidden),
/// following the org-tenancy 404-vs-403 convention.
/// </summary>
internal static class PlatformAdmin
{
    /// <summary>Claim the auth handler stamps when the session's email is on the allowlist.</summary>
    public const string Claim = "condux:platform_admin";

    /// <summary>Whether the principal is a platform admin (carries the stamped claim).</summary>
    public static bool IsPlatformAdmin(ClaimsPrincipal user) => user.HasClaim(Claim, "true");

    /// <summary>Endpoint filter for <c>/api/admin</c>: 404 unless the caller is a platform admin.</summary>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> RequirePlatformAdmin() =>
        async (ctx, next) =>
            IsPlatformAdmin(ctx.HttpContext.User) ? await next(ctx) : TypedResults.NotFound();
}
