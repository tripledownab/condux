using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Runner tokens (ADR-0033 slice 4, #68): the dashboard's management of the credential a customer's runner
/// presents to <see cref="RunnerEndpoints"/>. Cookie-authed, admin mints and revokes, member reads, and the
/// raw token is returned once on mint. Built like <see cref="McpTokenEndpoints"/>, but scoped to an org
/// rather than a project, because a runner serves whatever work its org produces.
/// </summary>
internal static class RunnerTokenEndpoints
{
    public static void MapRunnerTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/orgs/{orgId:long}/runner-tokens",
                async Task<Ok<MintedRunnerTokenResponse>> (
                    long orgId, CreateRunnerTokenRequest req, RunnerTokenRepository tokens) =>
                {
                    var label = string.IsNullOrWhiteSpace(req.Label) ? "Runner" : req.Label.Trim();
                    var (raw, hash) = RunnerTokens.Create();
                    var token = await tokens.CreateAsync(orgId, hash, label);
                    return TypedResults.Ok(
                        new MintedRunnerTokenResponse(token.Id, token.Name, raw, token.CreatedAt));
                })
            .WithName("createRunnerToken").WithTags("RunnerTokens")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        app.MapGet("/api/orgs/{orgId:long}/runner-tokens",
                async (long orgId, RunnerTokenRepository tokens) =>
                    TypedResults.Ok((await tokens.ListByOrgAsync(orgId)).Select(ToResponse).ToArray()))
            .WithName("listRunnerTokens").WithTags("RunnerTokens")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        app.MapDelete("/api/orgs/{orgId:long}/runner-tokens/{tokenId:guid}",
                async Task<Results<NoContent, NotFound>> (
                    long orgId, Guid tokenId, RunnerTokenRepository tokens) =>
                    await tokens.RevokeAsync(orgId, tokenId)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound())
            .WithName("revokeRunnerToken").WithTags("RunnerTokens")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));
    }

    private static RunnerTokenResponse ToResponse(ScopedTokenRow t) =>
        new(t.Id, t.Name, t.CreatedAt, t.LastUsedAt, t.RevokedAt is not null);
}
