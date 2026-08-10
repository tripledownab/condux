using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Core.Repos;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Scoped release tokens (the machine credential CI uses to record releases): mint / list / revoke a
/// per-project token from the dashboard (cookie auth, admin mints, member reads), plus the token-authed
/// <c>POST /api/releases</c> that lets CI record a release with just <c>Authorization: Bearer</c> — no
/// login, no internal ids. The token identifies the project; the repo defaults to the project's sole
/// linked repo (or is named as <c>owner/name</c> to disambiguate).
/// </summary>
internal static class ReleaseTokenEndpoints
{
    public static void MapReleaseTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/projects/{projectId:long}/release-tokens",
                async Task<Ok<MintedReleaseTokenResponse>> (
                    long projectId, CreateReleaseTokenRequest req, ReleaseTokenRepository tokens) =>
                {
                    var name = string.IsNullOrWhiteSpace(req.Name) ? "CI" : req.Name.Trim();
                    var (raw, hash) = ReleaseTokens.Create();
                    var token = await tokens.CreateAsync(projectId, hash, name);
                    return TypedResults.Ok(new MintedReleaseTokenResponse(token.Id, token.Name, raw, token.CreatedAt));
                })
            .WithName("createReleaseToken").WithTags("ReleaseTokens")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapGet("/api/projects/{projectId:long}/release-tokens",
                async (long projectId, ReleaseTokenRepository tokens) =>
                    TypedResults.Ok((await tokens.ListByProjectAsync(projectId)).Select(ToResponse).ToArray()))
            .WithName("listReleaseTokens").WithTags("ReleaseTokens")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        app.MapDelete("/api/projects/{projectId:long}/release-tokens/{tokenId:guid}",
                async Task<Results<NoContent, NotFound>> (
                    long projectId, Guid tokenId, ReleaseTokenRepository tokens) =>
                    await tokens.RevokeAsync(projectId, tokenId)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound())
            .WithName("revokeReleaseToken").WithTags("ReleaseTokens")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        // The CI path: authenticated by the release token in the Authorization header, not a cookie.
        app.MapPost("/api/releases",
                async Task<Results<Created<Release>, UnauthorizedHttpResult, BadRequest<string>>> (
                    HttpContext http, RecordReleaseViaTokenRequest req,
                    ReleaseTokenRepository tokens, RepoLinkRepository repos, ReleaseRepository releases) =>
                {
                    if (BearerToken.From(http) is not { } raw)
                    {
                        return TypedResults.Unauthorized();
                    }
                    if (await tokens.ResolveProjectAsync(ReleaseTokens.HashToken(raw)) is not { } projectId)
                    {
                        return TypedResults.Unauthorized();
                    }
                    if (string.IsNullOrWhiteSpace(req.Version) || string.IsNullOrWhiteSpace(req.CommitSha))
                    {
                        return TypedResults.BadRequest("version and commitSha are required");
                    }

                    var links = await repos.ListByProjectAsync(projectId);
                    if (ResolveRepo(links, req.Repo) is not { } link)
                    {
                        return TypedResults.BadRequest(links.Count == 0
                            ? "no repository is linked to this project"
                            : "specify repo as owner/name; this project has multiple linked repositories");
                    }

                    var release = await releases.RecordAsync(projectId, link.Id, req.Version.Trim(), req.CommitSha.Trim());
                    return TypedResults.Created($"/api/releases/{release.Id}", release);
                })
            .WithName("recordReleaseViaToken").WithTags("ReleaseTokens");
    }

    private static RepoLink? ResolveRepo(IReadOnlyList<RepoLink> links, string? repo) =>
        string.IsNullOrWhiteSpace(repo)
            ? links.Count == 1 ? links[0] : null
            : links.FirstOrDefault(l => l.RepoFullName.Equals(repo.Trim(), StringComparison.OrdinalIgnoreCase));

    private static ReleaseTokenResponse ToResponse(ReleaseToken t) =>
        new(t.Id, t.Name, t.CreatedAt, t.LastUsedAt, t.RevokedAt is not null);
}
