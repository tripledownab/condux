using System.Globalization;
using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Core.Events;
using Condux.Core.Repos;
using Condux.Core.SourceControl;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Condux.Storage.SourceMaps;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Suggested code mappings (#113): derives <c>stackRoot → sourceRoot</c> rules by matching the in-app frame
/// paths of a project's recent events, read the way the issue page shows them, against the linked repo's
/// file tree. Best-effort: anything missing or failing yields no suggestions rather than an error, with an
/// outcome (<see cref="MappingSuggestions"/>) saying which, so the dashboard can tell "connect GitHub" from
/// "nothing to add" from "try again".
/// </summary>
internal static class RepoSuggestionEndpoints
{
    public static void MapRepoSuggestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId:long}/repos/{repoId:guid}/suggested-mappings",
                async Task<Results<Ok<MappingSuggestions>, NotFound>> (
                    long projectId, Guid repoId, HttpContext http, RepoLinkRepository repos,
                    ProjectRepository projects, GithubInstallationRepository installations,
                    ClickHouseEventReader events, EventSymbolication symbolication,
                    ILoggerFactory loggerFactory) =>
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
                        return TypedResults.Ok(MappingSuggestions.None(MappingSuggestions.NotConnected));
                    }

                    var logger = loggerFactory.CreateLogger(nameof(RepoSuggestionEndpoints));
                    try
                    {
                        var token = await tokens.GetAsync(installs[0].InstallationId, http.RequestAborted);
                        var tree = await repoClient.ListTreeAsync(
                            token, repo.RepoFullName, repo.DefaultBranch, http.RequestAborted);
                        // Symbolicated first: a bundled app's stored frame names a minified chunk no
                        // repository holds, while the issue page and the Conductor read the source path the
                        // map names, which is the form a code mapping has to match.
                        var sampled = await events.RecentByProjectAsync(
                            projectId.ToString(CultureInfo.InvariantCulture), 25, http.RequestAborted);
                        // Symbolication runs under its own budget, past which the remaining events keep their
                        // stored frames, which only means fewer suggestions.
                        var recent = await symbolication.SymbolicateAsync(projectId, sampled, http.RequestAborted);

                        // One read of the project's mappings: this repo's are what a suggestion may duplicate,
                        // and the others' stack roots are what it may tie with.
                        var projectMappings = await repos.ListProjectCodeMappingsAsync(projectId, http.RequestAborted);
                        var existing = projectMappings.Where(mapping => mapping.RepoLinkId == repoId).ToList();
                        var mappedElsewhere = projectMappings
                            .Where(mapping => mapping.RepoLinkId != repoId)
                            .Select(mapping => mapping.StackRoot)
                            .ToList();
                        return TypedResults.Ok(
                            CodeMappingDeriver.Suggest(InAppFramePaths(recent), tree, existing, mappedElsewhere));
                    }
                    // Only the caller hanging up passes through: a timed-out GitHub or ClickHouse call is a
                    // TaskCanceledException too, and it is exactly the hiccup this should absorb.
                    catch (Exception ex) when (ex is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested)
                    {
                        // Advisory feature: a GitHub/ClickHouse hiccup yields no suggestions, never a 500.
                        logger.LogWarning(ex, "suggest-mappings failed project={ProjectId} repo={RepoId}", projectId, repoId);
                        return TypedResults.Ok(MappingSuggestions.None(MappingSuggestions.Unavailable));
                    }
                })
            .WithName("suggestedMappings").WithTags("Repos")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));
    }

    // The most distinct frame paths one suggestion considers. Whoever holds the DSN chooses how many frames
    // an event carries, and the work of choosing the rules grows with paths and rules together, so it is
    // bounded here.
    private const int MaxFramePaths = 500;

    // Distinct in-app stack-frame file paths across a sample of stored events, at most MaxFramePaths: the
    // runtime paths the deriver matches against the repo tree. Library frames (not in-app) and unreadable
    // payloads are skipped.
    private static IReadOnlyList<string> InAppFramePaths(IReadOnlyList<StoredEvent> events)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>();
        foreach (var stored in events)
        {
            if (stored.ToEvent() is not { } sample)
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
                        if (paths.Count == MaxFramePaths)
                        {
                            return paths;
                        }
                    }
                }
            }
        }

        return paths;
    }
}
