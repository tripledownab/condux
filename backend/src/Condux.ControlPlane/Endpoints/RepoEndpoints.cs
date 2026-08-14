using System.Globalization;
using System.Text.Json;
using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.Events;
using Condux.Core.Repos;
using Condux.Core.CveScanning;
using Condux.Core.SourceControl;
using Condux.GitHub;
using Condux.Storage.ClickHouse;
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

                    var link = await repos.LinkAsync(projectId, req.RepoFullName, req.DefaultBranch ?? "main");
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

        // Open CVE findings for a linked repo (#117): the repo's open Dependabot alerts, fetched with the
        // org's GitHub installation token. Member+, best-effort — returns empty when GitHub is not
        // connected, the App lacks the Dependabot-alerts permission, or the repo has none.
        app.MapGet("/api/projects/{projectId:long}/repos/{repoId:guid}/cve-findings",
                async Task<Results<Ok<IReadOnlyList<CveFinding>>, NotFound>> (
                    long projectId, Guid repoId, HttpContext http, RepoLinkRepository repos,
                    ProjectRepository projects, GithubInstallationRepository installations,
                    ILoggerFactory loggerFactory) =>
                {
                    if (await repos.GetAsync(projectId, repoId, http.RequestAborted) is not { } repo
                        || await projects.GetAsync(projectId, http.RequestAborted) is not { } project)
                    {
                        return TypedResults.NotFound();
                    }

                    // Findings come from a scanner, never from one forge's API directly (ADR-0022), so the
                    // shape stays the same whichever produced them.
                    var tokens = http.RequestServices.GetService<ISourceHostTokens>();
                    var scanner = http.RequestServices.GetService<ICveScanner>();
                    var installs = await installations.GetByOrgAsync(project.OrgId, http.RequestAborted);
                    if (tokens is null || scanner is null || installs.Count == 0)
                    {
                        return TypedResults.Ok<IReadOnlyList<CveFinding>>([]);
                    }

                    try
                    {
                        var token = await tokens.GetAsync(installs[0].InstallationId, http.RequestAborted);
                        return TypedResults.Ok(
                            await scanner.ScanAsync(token, repo.RepoFullName, http.RequestAborted));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Advisory feature: a GitHub hiccup or a missing permission yields no findings, not a 500.
                        loggerFactory.CreateLogger(nameof(RepoEndpoints))
                            .LogWarning(ex, "cve-findings failed project={ProjectId} repo={RepoId}", projectId, repoId);
                        return TypedResults.Ok<IReadOnlyList<CveFinding>>([]);
                    }
                })
            .WithName("cveFindings").WithTags("Repos")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        // Suggested code mappings (#113): derive stackRoot → sourceRoot rules by matching the project's
        // recent in-app stack-frame paths against the repo's file tree (fetched with the org's GitHub
        // installation token). Best-effort and member+ — the user reviews and adds the ones they want via
        // the existing add-mapping route. Returns empty when GitHub is not connected or nothing matches.
        app.MapGet("/api/projects/{projectId:long}/repos/{repoId:guid}/suggested-mappings",
                async Task<Results<Ok<IReadOnlyList<DerivedMapping>>, NotFound>> (
                    long projectId, Guid repoId, HttpContext http, RepoLinkRepository repos,
                    ProjectRepository projects, GithubInstallationRepository installations,
                    ClickHouseEventReader events, ILoggerFactory loggerFactory) =>
                {
                    if (await repos.GetAsync(projectId, repoId, http.RequestAborted) is not { } repo
                        || await projects.GetAsync(projectId, http.RequestAborted) is not { } project)
                    {
                        return TypedResults.NotFound();
                    }

                    // GitHub services are only registered when the App is configured; a token needs the
                    // org's installation. Any of these missing → nothing to derive (empty, not an error).
                    var tokens = http.RequestServices.GetService<ISourceHostTokens>();
                    var repoClient = http.RequestServices.GetService<ISourceHostClient>();
                    var installs = await installations.GetByOrgAsync(project.OrgId, http.RequestAborted);
                    if (tokens is null || repoClient is null || installs.Count == 0)
                    {
                        return TypedResults.Ok<IReadOnlyList<DerivedMapping>>([]);
                    }

                    var logger = loggerFactory.CreateLogger(nameof(RepoEndpoints));
                    try
                    {
                        var token = await tokens.GetAsync(installs[0].InstallationId, http.RequestAborted);
                        var tree = await repoClient.ListTreeAsync(
                            token, repo.RepoFullName, repo.DefaultBranch, http.RequestAborted);
                        var recent = await events.RecentByProjectAsync(
                            projectId.ToString(CultureInfo.InvariantCulture), 25, http.RequestAborted);

                        var derived = CodeMappingDeriver.Derive(InAppFramePaths(recent), tree);
                        // Do not re-suggest a stackRoot that is already mapped.
                        var existing = (await repos.ListCodeMappingsAsync(repoId, http.RequestAborted))
                            .Select(mapping => mapping.StackRoot).ToHashSet();
                        return TypedResults.Ok<IReadOnlyList<DerivedMapping>>(
                            [.. derived.Where(suggestion => !existing.Contains(suggestion.StackRoot))]);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Advisory feature: a GitHub/ClickHouse hiccup yields no suggestions, never a 500.
                        logger.LogWarning(ex, "suggest-mappings failed project={ProjectId} repo={RepoId}", projectId, repoId);
                        return TypedResults.Ok<IReadOnlyList<DerivedMapping>>([]);
                    }
                })
            .WithName("suggestedMappings").WithTags("Repos")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        app.MapPost("/api/projects/{projectId:long}/releases",
                async Task<Results<Created<Release>, NotFound>> (
                    long projectId, RecordReleaseRequest req, RepoLinkRepository repos, ReleaseRepository releases) =>
                {
                    if (await repos.GetAsync(projectId, req.RepoLinkId) is null)
                    {
                        return TypedResults.NotFound();
                    }
                    var release = await releases.RecordAsync(projectId, req.RepoLinkId, req.Version, req.CommitSha);
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

    // Default options match the ClickHouse writer's JsonSerializer.Serialize(event) (PascalCase), so a
    // stored payload round-trips back to an Event.
    private static readonly JsonSerializerOptions PayloadJson = new();

    // Distinct in-app stack-frame file paths across a sample of stored events — the runtime paths the
    // deriver matches against the repo tree. Library frames (not in-app) and unreadable payloads are skipped.
    private static IReadOnlyList<string> InAppFramePaths(IReadOnlyList<StoredEvent> events)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>();
        foreach (var stored in events)
        {
            Event? sample;
            try
            {
                sample = JsonSerializer.Deserialize<Event>(stored.Payload, PayloadJson);
            }
            catch (JsonException)
            {
                continue;
            }
            if (sample is null)
            {
                continue;
            }

            foreach (var exception in sample.Exceptions)
            {
                foreach (var frame in exception.Stacktrace?.Frames ?? [])
                {
                    if (frame.InApp && !string.IsNullOrEmpty(frame.Filename) && seen.Add(frame.Filename))
                    {
                        paths.Add(frame.Filename);
                    }
                }
            }
        }

        return paths;
    }
}
