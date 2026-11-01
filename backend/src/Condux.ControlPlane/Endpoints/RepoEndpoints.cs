using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.Repos;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Connect-repo + error→code linking (#89): link a GitHub repo to a project, set the code mappings
/// that turn stack-frame paths into repo paths, and record release→commit associations. Tenancy is
/// enforced (reads need member, writes need admin). The GitHub App install that discovers repos and
/// mints tokens lands in #61; here a repo is linked by its <c>owner/name</c>.
/// </summary>
internal static class RepoEndpoints
{
    public static void MapRepoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/projects/{projectId:long}/repos",
                async Task<Results<Created<RepoLink>, NotFound, Conflict<ErrorResponse>>> (
                    long projectId, LinkRepoRequest req, RepoLinkRepository repos, ProjectRepository projects,
                    GithubInstallationRepository installations, GitHubAppConfig githubConfig,
                    CancellationToken ct) =>
                {
                    // A repo can only be linked once GitHub is connected for the org — otherwise the Conductor
                    // has no installation token to reach it (a dead link). Enforced only when the GitHub App is
                    // configured on this server; a GitHub-less deployment (no App) skips it (deep-links only).
                    if (githubConfig.Enabled)
                    {
                        if (await projects.GetAsync(projectId, ct) is not { } project)
                        {
                            return TypedResults.NotFound();
                        }
                        if ((await installations.GetByOrgAsync(project.OrgId, ct)).Count == 0)
                        {
                            return TypedResults.Conflict(new ErrorResponse("github_not_connected"));
                        }
                    }

                    // A blank branch is stored as main, as a missing one is, and any other is trimmed as the update
                    // route trims it: an empty one names no branch, and a padded one names a branch that is not there.
                    var branch = string.IsNullOrWhiteSpace(req.DefaultBranch) ? "main" : req.DefaultBranch.Trim();
                    var link = await repos.LinkAsync(projectId, req.RepoFullName, branch);
                    return TypedResults.Created($"/api/projects/{projectId}/repos/{link.Id}", link);
                })
            .WithName("linkRepo").WithTags("Repos")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapGet("/api/projects/{projectId:long}/repos",
                async (long projectId, RepoLinkRepository repos) =>
                    TypedResults.Ok(await repos.ListByProjectAsync(projectId)))
            .WithName("listRepos").WithTags("Repos")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Change the branch draft PRs target (carried onto every FixJob as its BaseBranch).
        app.MapPatch("/api/projects/{projectId:long}/repos/{repoId:guid}",
                async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, Guid repoId, UpdateRepoRequest req, RepoLinkRepository repos) =>
                {
                    if (string.IsNullOrWhiteSpace(req.DefaultBranch))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_branch"));
                    }

                    return await repos.UpdateDefaultBranchAsync(projectId, repoId, req.DefaultBranch.Trim())
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound();
                })
            .WithName("updateRepo").WithTags("Repos")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        // Unlink a repo (its code mappings + releases cascade away). Admin+.
        app.MapDelete("/api/projects/{projectId:long}/repos/{repoId:guid}",
                async Task<Results<NoContent, NotFound>> (
                    long projectId, Guid repoId, RepoLinkRepository repos) =>
                    await repos.DeleteAsync(projectId, repoId)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound())
            .WithName("unlinkRepo").WithTags("Repos")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapPost("/api/projects/{projectId:long}/repos/{repoId:guid}/code-mappings",
                async Task<Results<Created<CodeMapping>, NotFound>> (
                    long projectId, Guid repoId, CodeMappingRequest req, RepoLinkRepository repos) =>
                {
                    if (await repos.GetAsync(projectId, repoId) is null)
                    {
                        return TypedResults.NotFound();
                    }
                    var mapping = await repos.AddCodeMappingAsync(repoId, req.StackRoot, req.SourceRoot ?? "");
                    return TypedResults.Created(
                        $"/api/projects/{projectId}/repos/{repoId}/code-mappings/{mapping.Id}", mapping);
                })
            .WithName("addCodeMapping").WithTags("Repos")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapGet("/api/projects/{projectId:long}/repos/{repoId:guid}/code-mappings",
                async Task<Results<Ok<IReadOnlyList<CodeMapping>>, NotFound>> (
                    long projectId, Guid repoId, RepoLinkRepository repos) =>
                    await repos.GetAsync(projectId, repoId) is null
                        ? TypedResults.NotFound()
                        : TypedResults.Ok(await repos.ListCodeMappingsAsync(repoId)))
            .WithName("listCodeMappings").WithTags("Repos")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Remove one code mapping. Admin+. The repo is checked to belong to the project first, so the
        // delete is tenancy-safe.
        app.MapDelete("/api/projects/{projectId:long}/repos/{repoId:guid}/code-mappings/{mappingId:guid}",
                async Task<Results<NoContent, NotFound>> (
                    long projectId, Guid repoId, Guid mappingId, RepoLinkRepository repos) =>
                {
                    if (await repos.GetAsync(projectId, repoId) is null)
                    {
                        return TypedResults.NotFound();
                    }
                    return await repos.DeleteCodeMappingAsync(repoId, mappingId)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound();
                })
            .WithName("deleteCodeMapping").WithTags("Repos")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapPost("/api/projects/{projectId:long}/releases",
                async Task<Results<Created<Release>, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, RecordReleaseRequest req, RepoLinkRepository repos, ReleaseRepository releases) =>
                {
                    var (recordable, error) = ReleaseRecords.ToRecordable(req.Version, req.CommitSha);
                    if (recordable is null)
                    {
                        return TypedResults.BadRequest(new ErrorResponse(error!));
                    }
                    if (await repos.GetAsync(projectId, req.RepoLinkId) is null)
                    {
                        return TypedResults.NotFound();
                    }
                    var release = await releases.RecordAsync(projectId, req.RepoLinkId, recordable);
                    return TypedResults.Created($"/api/projects/{projectId}/releases/{release.Id}", release);
                })
            .WithName("recordRelease").WithTags("Repos")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapGet("/api/projects/{projectId:long}/releases",
                async (long projectId, ReleaseRepository releases) =>
                    TypedResults.Ok(await releases.ListByProjectAsync(projectId)))
            .WithName("listReleases").WithTags("Repos")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));
    }
}
