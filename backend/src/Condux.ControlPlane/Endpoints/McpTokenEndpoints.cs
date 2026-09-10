using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Scoped MCP tokens (ADR-0029): mint / list / revoke a per-project credential an AI agent presents as
/// <c>Authorization: Bearer</c> to the MCP endpoint (<see cref="McpEndpoints"/>). Cookie-authed
/// dashboard management — admin mints + revokes, member reads; the raw token is returned once, on mint.
/// Built like <see cref="ReleaseTokenEndpoints"/>.
///
/// A token also carries what it may do (ADR-0046), chosen here and never edited: there is deliberately no
/// route that widens an existing token, so its authority is whatever this endpoint wrote.
/// </summary>
internal static class McpTokenEndpoints
{
    public static void MapMcpTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/projects/{projectId:long}/mcp-tokens",
                async Task<Results<Ok<MintedMcpTokenResponse>, BadRequest<ErrorResponse>>> (
                    long projectId, CreateMcpTokenRequest req, McpTokenRepository tokens) =>
                {
                    // An omitted capability mints a read token, which is what every token predating
                    // ADR-0046 is. An unrecognised one is refused rather than silently downgraded: a
                    // client asking for authority we do not know about must not believe it got it, and
                    // must not quietly receive less than it asked for either.
                    if (!McpCapabilities.TryParse(
                        req.Capability ?? McpCapabilities.Name(McpCapability.Read), out var capability))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_capability"));
                    }
                    var name = string.IsNullOrWhiteSpace(req.Name) ? "Agent" : req.Name.Trim();
                    var (raw, hash) = McpTokens.Create();
                    var token = await tokens.CreateAsync(projectId, hash, name, (int)capability);
                    return TypedResults.Ok(new MintedMcpTokenResponse(
                        token.Id, token.Name, raw, McpCapabilities.Name((McpCapability)token.Capability),
                        token.CreatedAt));
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
        new(t.Id, t.Name, McpCapabilities.Name((McpCapability)t.Capability), t.CreatedAt, t.LastUsedAt,
            t.RevokedAt is not null);
}
