using System.Globalization;
using System.Text.Json;
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
/// file tree. Best-effort: anything missing or failing yields no suggestions rather than an error.
/// </summary>
internal static class RepoSuggestionEndpoints
{
    public static void MapRepoSuggestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId:long}/repos/{repoId:guid}/suggested-mappings",
                async Task<Results<Ok<IReadOnlyList<DerivedMapping>>, NotFound>> (
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
                        return TypedResults.Ok<IReadOnlyList<DerivedMapping>>([]);
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

                        var derived = CodeMappingDeriver.Derive(InAppFramePaths(recent), tree);
                        // Do not re-suggest a stackRoot that is already mapped.
                        var existing = (await repos.ListCodeMappingsAsync(repoId, http.RequestAborted))
                            .Select(mapping => mapping.StackRoot).ToHashSet();
                        return TypedResults.Ok<IReadOnlyList<DerivedMapping>>(
                            [.. derived.Where(suggestion => !existing.Contains(suggestion.StackRoot))]);
                    }
                    // Only the caller hanging up passes through: a timed-out GitHub or ClickHouse call is a
                    // TaskCanceledException too, and it is exactly the hiccup this should absorb.
                    catch (Exception ex) when (ex is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested)
                    {
                        // Advisory feature: a GitHub/ClickHouse hiccup yields no suggestions, never a 500.
                        logger.LogWarning(ex, "suggest-mappings failed project={ProjectId} repo={RepoId}", projectId, repoId);
                        return TypedResults.Ok<IReadOnlyList<DerivedMapping>>([]);
                    }
                })
            .WithName("suggestedMappings").WithTags("Repos")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));
    }

    // Default options match the ClickHouse writer's JsonSerializer.Serialize(event) (PascalCase), so a
    // stored payload round-trips back to an Event.
    private static readonly JsonSerializerOptions PayloadJson = new();

    // Distinct in-app stack-frame file paths across a sample of stored events: the runtime paths the
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
