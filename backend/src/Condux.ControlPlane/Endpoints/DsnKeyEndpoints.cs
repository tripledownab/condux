using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.Projects;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// A project's DSN keys: list, mint, rename, revoke (#34), tenancy-enforced (#48). Member+ reads,
/// admin+ writes, scoped by project. Minting a key is where a project's ingest credential is handed out,
/// so it is its own domain beside <see cref="ProjectEndpoints"/> and <see cref="ProvisioningEndpoints"/>.
/// The DSN string itself comes from <see cref="IngestDsn"/>, shared with project creation because that
/// mints the first key on the way through.
/// </summary>
internal static class DsnKeyEndpoints
{
    // DSN key labels are cosmetic and user-supplied; cap the length so a create/rename can't store an
    // unbounded string. Matches the frontend input's maxLength.
    private const int MaxLabelLength = 100;

    public static void MapDsnKeyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId:long}/keys", async (long projectId, DsnKeyRepository keys) =>
                TypedResults.Ok(await keys.ListByProjectAsync(projectId)))
            .WithName("listKeys").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        app.MapPost("/api/projects/{projectId:long}/keys",
                async Task<Results<Created<CreateKeyResponse>, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, CreateKeyRequest? req, DsnKeyRepository keys, ProjectRepository projects,
                    IConfiguration cfg) =>
                {
                    // A blank name falls back to "default" (the auto-minted first key), but a too-long one is
                    // rejected rather than silently truncated.
                    var trimmed = req?.Label?.Trim();
                    if (trimmed is { Length: > MaxLabelLength })
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_label"));
                    }
                    // The DSN carries the project's public UUID, so resolve it (the auth filter already
                    // confirmed the project exists for this caller).
                    if (await projects.GetAsync(projectId) is not { } project)
                    {
                        return TypedResults.NotFound();
                    }
                    var label = string.IsNullOrEmpty(trimmed) ? "default" : trimmed;
                    var key = await keys.CreateAsync(projectId, label, DsnKeyGenerator.NewPublicKey());
                    return TypedResults.Created($"/api/projects/{projectId}/keys/{key.Id}",
                        new CreateKeyResponse(
                            key, IngestDsn.Build(cfg, key.PublicKey, project.PublicId)));
                })
            .WithName("createKey").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        // Rename a key (label only). Admin+, tenant-scoped by project. A blank/too-long name is rejected.
        app.MapPatch("/api/projects/{projectId:long}/keys/{keyId:long}",
                async Task<Results<Ok<DsnKey>, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, long keyId, UpdateKeyRequest req, DsnKeyRepository keys) =>
                {
                    var label = req.Label?.Trim();
                    if (string.IsNullOrEmpty(label) || label.Length > MaxLabelLength)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_label"));
                    }
                    return await keys.UpdateLabelAsync(projectId, keyId, label) is { } key
                        ? TypedResults.Ok(key)
                        : TypedResults.NotFound();
                })
            .WithName("updateKey").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapPost("/api/projects/{projectId:long}/keys/{keyId:long}/revoke",
                async Task<Results<NoContent, NotFound>> (long projectId, long keyId, DsnKeyRepository keys) =>
                    await keys.RevokeAsync(projectId, keyId) ? TypedResults.NoContent() : TypedResults.NotFound())
            .WithName("revokeKey").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));
    }
}
