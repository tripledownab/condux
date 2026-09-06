using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.Projects;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// The project resource: create and list within an org, then read, rename and delete one by its public
/// UUID (#125). Tenancy-enforced (#48), member+ reads and admin+ writes. Split out of
/// <see cref="ProvisioningEndpoints"/>, which keeps the org resource; a project's DSN keys are their own
/// domain again, in <see cref="DsnKeyEndpoints"/>.
/// </summary>
internal static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        // Create a project and mint its first DSN key; returns the ready-to-use DSN.
        app.MapPost("/api/orgs/{orgId:long}/projects",
                async (long orgId, CreateProjectRequest req,
                    ProjectRepository projects, DsnKeyRepository keys, IConfiguration cfg) =>
                {
                    var project = await projects.CreateAsync(orgId, req.Name, req.Platform ?? "other");
                    var key = await keys.CreateAsync(project.Id, "default", DsnKeyGenerator.NewPublicKey());
                    return TypedResults.Created($"/api/projects/{project.PublicId}",
                        new ProvisionProjectResponse(
                            project, key, IngestDsn.Build(cfg, key.PublicKey, project.PublicId)));
                })
            .WithName("createProject").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        app.MapGet("/api/orgs/{orgId:long}/projects", async (long orgId, ProjectRepository projects) =>
                TypedResults.Ok(await projects.ListByOrgAsync(orgId)))
            .WithName("listProjects").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        // A single project by its public UUID: the per-project detail page, reading one row instead of
        // the org list. The bigint id never appears in a URL (#125). Member+ on the owning org.
        app.MapGet("/api/projects/{projectId:guid}",
                async Task<Results<Ok<ProjectRecord>, NotFound>> (
                    Guid projectId, ProjectRepository projects) =>
                    await projects.GetByPublicIdAsync(projectId) is { } project
                        ? TypedResults.Ok(project)
                        : TypedResults.NotFound())
            .WithName("getProject").WithTags("Provisioning")
            .RequireAuthorization()
            .AddEndpointFilter(OrgAuthorization.RequireProjectRoleByPublicId(OrgRole.Member));

        // Rename a project (name + platform; the slug re-derives). Admin+ on the owning org.
        app.MapPatch("/api/projects/{projectId:guid}",
                async Task<Results<Ok<ProjectRecord>, NotFound, BadRequest<ErrorResponse>>> (
                    Guid projectId, UpdateProjectRequest req, ProjectRepository projects) =>
                {
                    if (string.IsNullOrWhiteSpace(req.Name))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_name"));
                    }
                    if (await projects.GetByPublicIdAsync(projectId) is not { } existing)
                    {
                        return TypedResults.NotFound();
                    }

                    return await projects.UpdateAsync(existing.Id, req.Name.Trim(), req.Platform ?? "other") is { } project
                        ? TypedResults.Ok(project)
                        : TypedResults.NotFound();
                })
            .WithName("updateProject").WithTags("Provisioning")
            .RequireAuthorization()
            .AddEndpointFilter(OrgAuthorization.RequireProjectRoleByPublicId(OrgRole.Admin));

        // Delete a project and everything scoped to it (DSN keys, issues, repos, alerts cascade). Admin+.
        app.MapDelete("/api/projects/{projectId:guid}",
                async Task<Results<NoContent, NotFound>> (Guid projectId, ProjectRepository projects) =>
                    await projects.GetByPublicIdAsync(projectId) is { } project
                    && await projects.DeleteAsync(project.Id)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound())
            .WithName("deleteProject").WithTags("Provisioning")
            .RequireAuthorization()
            .AddEndpointFilter(OrgAuthorization.RequireProjectRoleByPublicId(OrgRole.Admin));
    }
}
