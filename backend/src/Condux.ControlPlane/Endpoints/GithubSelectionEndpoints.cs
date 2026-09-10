using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.GitHub;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// The one branch of connecting that needs the user to answer a question: they can reach the Condux app on
/// more than one GitHub account, so the callback in <see cref="GithubConnectEndpoints"/> cannot pick for
/// them. It hands the browser a signed <see cref="GithubSelectionToken"/> naming the candidates, the
/// dashboard renders them, and the choice comes back here.
///
/// The signature is what bounds the choice. Only ids inside the token are accepted, so a caller cannot
/// name an installation GitHub never said they could reach, and the link itself still refuses one another
/// org holds. Both routes are admin+ and org-scoped. All behind the opt-in <see cref="GitHubAppConfig"/>;
/// when the app isn't configured they return 404.
/// </summary>
internal static class GithubSelectionEndpoints
{
    public static void MapGithubSelectionEndpoints(this IEndpointRouteBuilder app)
    {
        // The accounts behind a selection token, so the dashboard can render the choice. Reading it here
        // rather than in the browser keeps the signature the only thing that decides what the token says.
        app.MapGet("/api/orgs/{orgId:long}/github/selection",
                Results<Ok<GithubSelectionResponse>, BadRequest<ErrorResponse>, NotFound> (
                    long orgId, string token, GitHubAppConfig config) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var selection = GithubSelectionToken.Validate(
                        token, DateTimeOffset.UtcNow, config.Options!.WebhookSecret);
                    return selection is null || selection.OrgId != orgId
                        ? TypedResults.BadRequest(new ErrorResponse("invalid_selection"))
                        : TypedResults.Ok(new GithubSelectionResponse(
                            [.. selection.Candidates.Select(c =>
                                new GithubSelectionOption(c.InstallationId, c.AccountLogin))]));
                })
            .WithName("githubSelection").WithTags("GitHub")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        // Link the installation the user picked. Only ids inside the signed token are accepted, so the
        // choice is bounded by what GitHub told us that user can reach. Admin+.
        app.MapPost("/api/orgs/{orgId:long}/github/select",
                async Task<Results<NoContent, BadRequest<ErrorResponse>, Conflict<ErrorResponse>, NotFound>> (
                    long orgId, GithubSelectRequest req, GitHubAppConfig config,
                    GithubInstallationRepository installations, HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var selection = GithubSelectionToken.Validate(
                        req.Selection, DateTimeOffset.UtcNow, config.Options!.WebhookSecret);
                    var chosen = selection?.OrgId == orgId
                        ? selection.Candidates.FirstOrDefault(c => c.InstallationId == req.InstallationId)
                        : null;
                    if (chosen is null)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_selection"));
                    }

                    return await installations.LinkAsync(
                        chosen.InstallationId, orgId, chosen.AccountLogin, http.RequestAborted)
                        ? TypedResults.NoContent()
                        : TypedResults.Conflict(new ErrorResponse("installation_linked_elsewhere"));
                })
            .WithName("githubSelect").WithTags("GitHub")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));
    }
}
