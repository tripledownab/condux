using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Scoped MCP tokens (ADR-0029): mint / list / revoke a per-project, read-only credential an AI agent
/// presents as <c>Authorization: Bearer</c> to the MCP endpoint (<see cref="McpEndpoints"/>). Cookie-authed
/// dashboard management — admin mints + revokes, member reads; the raw token is returned once, on mint.
/// Mirrors <see cref="ReleaseTokenEndpoints"/>.
/// </summary>
internal static class McpTokenEndpoints
{
    public static void MapMcpTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/projects/{projectId:long}/mcp-tokens",
                async Task<Ok<MintedMcpTokenResponse>> (
                    long projectId, CreateMcpTokenRequest req, McpTokenRepository tokens) =>
                {
                    var name = string.IsNullOrWhiteSpace(req.Name) ? "Agent" : req.Name.Trim();
                    var (raw, hash) = McpTokens.Create();
                    var token = await tokens.CreateAsync(projectId, hash, name);
                    return TypedResults.Ok(new MintedMcpTokenResponse(token.Id, token.Name, raw, token.CreatedAt));
                })
            .WithName("createMcpToken").WithTags("McpTokens")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapGet("/api/projects/{projectId:long}/mcp-tokens",
                async (long projectId, McpTokenRepository tokens) =>
                    TypedResults.Ok((await tokens.ListByProjectAsync(projectId)).Select(ToResponse).ToArray()))
            .WithName("listMcpTokens").WithTags("McpTokens")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        app.MapDelete("/api/projects/{projectId:long}/mcp-tokens/{tokenId:guid}",
                async Task<Results<NoContent, NotFound>> (
                    long projectId, Guid tokenId, McpTokenRepository tokens) =>
                    await tokens.RevokeAsync(projectId, tokenId)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound())
            .WithName("revokeMcpToken").WithTags("McpTokens")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));
    }

    private static McpTokenResponse ToResponse(ScopedTokenRow t) =>
        new(t.Id, t.Name, t.CreatedAt, t.LastUsedAt, t.RevokedAt is not null);
}
