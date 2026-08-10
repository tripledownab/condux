using Condux.ControlPlane.Auth;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// First-run onboarding completion (#132 follow-up). Completion is an explicit signal — the user clicked
/// Finish — because it cannot be derived from data: a fresh owner has an org + a project the moment before
/// they finish exactly as after. Persisted as <c>users.onboarded_at</c> so the dashboard gate opens on the
/// click, cross-device. Invited users are marked onboarded on invite-accept instead (see InviteEndpoints).
/// </summary>
internal static class OnboardingEndpoints
{
    public static void MapOnboardingEndpoints(this IEndpointRouteBuilder app)
    {
        // Marks the caller onboarded. The dashboard only enables Finish once the mandatory steps (org +
        // project) exist; marking a user with no tenant is harmless (tenancy is still enforced everywhere),
        // so no extra server-side gate is needed. Idempotent.
        app.MapPost("/api/onboarding/complete",
                async Task<NoContent> (HttpContext http, UserRepository users) =>
                {
                    await users.MarkOnboardedAsync(OrgAuthorization.CurrentUserId(http.User), http.RequestAborted);
                    return TypedResults.NoContent();
                })
            .WithName("completeOnboarding").WithTags("Onboarding").RequireAuthorization();
    }
}
