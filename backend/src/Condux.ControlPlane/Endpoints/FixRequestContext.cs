using System.Globalization;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Repos;
using Condux.Core.SourceControl;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Condux.Storage.SourceMaps;
using Microsoft.Extensions.DependencyInjection;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// What a manual fix request reads once it has reserved a run: the release the issue was first seen in,
/// and the scoped context built from its latest event, its repository's code mappings and the suspect
/// commit. Three failures degrade the prompt: an event store that cannot be reached, a source map that
/// cannot be used, and a suspect-commit lookup that fails other than by timing out. Every other failure
/// fails the request, and the caller hands the reserved run back.
/// </summary>
internal static class FixRequestContext
{
    /// <summary>The context for one issue's fix and the installation it will run under (0 when GitHub is
    /// not connected): a real provider mints a repo-scoped token from it, and the suspect-commit lookup
    /// needs it too.</summary>
    public static async Task<(FixContext Context, long InstallationId)> BuildAsync(
        long projectId, long issueId, string? firstRelease, RepoLink repo, long orgId,
        GithubInstallationRepository installations, ReleaseRepository releases, ClickHouseEventReader events,
        EventSymbolication symbolication, RepoLinkRepository repos, IServiceProvider services, ILogger logger,
        CancellationToken cancellationToken)
    {
        var installs = await installations.GetByOrgAsync(orgId, cancellationToken);
        var installationId = installs.Count > 0 ? installs[0].InstallationId : 0;
        // Release attribution + suspect commit (#144): resolve the release the issue was first seen in to
        // its commit (a bisect hint), then blame the culprit file at that commit for the change that most
        // likely introduced the bug. GitHub off or an unrecorded release simply drops the hint.
        var release = await ResolveReleaseAsync(projectId, firstRelease, releases, cancellationToken);
        var context = await AssembleAsync(
            projectId, issueId, repo, release, installationId,
            services.GetService<ISourceHostTokens>(), services.GetService<ISourceHostClient>(),
            events, symbolication, repos, logger, cancellationToken);
        return (context, installationId);
    }

    // The release the issue was first seen in, resolved to its recorded commit (#144). Null when the issue
    // has no first_release; a version with no recorded release still attributes the version (commit null).
    private static async Task<ReleaseContext?> ResolveReleaseAsync(
        long projectId, string? firstRelease, ReleaseRepository releases, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(firstRelease))
        {
            return null;
        }

        var release = await releases.GetByVersionAsync(projectId, firstRelease, cancellationToken);
        return new ReleaseContext(firstRelease, release?.CommitSha);
    }

    // Assemble the scoped, double-scrubbed fix context from the issue's latest stored event + the repo's
    // code mappings (#63) + the release attribution and suspect commit (#144). With no event yet
    // (or a payload that reads as null), return an empty context so the run falls back to a minimal prompt.
    private static async Task<FixContext> AssembleAsync(
        long projectId, long issueId, RepoLink repo, ReleaseContext? release, long installationId,
        ISourceHostTokens? tokens, ISourceHostClient? github, ClickHouseEventReader events,
        EventSymbolication symbolication, RepoLinkRepository repos, ILogger logger,
        CancellationToken cancellationToken)
    {
        // Context assembly is best-effort by design (#63): no sampled event degrades to the minimal
        // prompt, and an unreachable event store must degrade the same way, not fail the request.
        IReadOnlyList<StoredEvent> recent;
        try
        {
            recent = await events.RecentByIssueAsync(
                projectId.ToString(CultureInfo.InvariantCulture), (ulong)issueId, 1, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "fix context assembly skipped: event store unreachable issue={IssueId}", issueId);
            recent = [];
        }

        if (recent.Count == 0
            || recent[0].ToEvent() is not { } stored)
        {
            return new FixContext("", []);
        }

        // The Conductor reads the frames the issue page shows: a bundled frame names a minified chunk no
        // repository holds, and only its source map says which file it came from.
        var sample = await symbolication.SymbolicateAsync(projectId, stored, cancellationToken: cancellationToken);

        var mappings = await repos.ListCodeMappingsAsync(repo.Id, cancellationToken);
        var suspect = await ResolveSuspectAsync(
            repo, release, installationId, tokens, github, sample, mappings, logger, cancellationToken);
        return FixContextAssembler.Assemble(sample, mappings, release, suspect);
    }

    // The commit that most likely introduced the error (#144): the last change to the culprit file as of the
    // first-seen release commit, via GitHub blame-by-file. Needs GitHub connected + a release commit + an
    // in-app culprit frame; best-effort, so any gap or a GitHub hiccup just drops the hint (never fails).
    private static async Task<SuspectCommitContext?> ResolveSuspectAsync(
        RepoLink repo, ReleaseContext? release, long installationId,
        ISourceHostTokens? tokens, ISourceHostClient? github,
        Event sample, IReadOnlyList<CodeMapping> mappings, ILogger logger, CancellationToken cancellationToken)
    {
        if (release?.CommitSha is not { } sha || tokens is null || github is null || installationId == 0
            || FixContextAssembler.CulpritPath(sample, mappings) is not { } culprit)
        {
            return null;
        }

        try
        {
            var token = await tokens.GetAsync(installationId, cancellationToken);
            if (await github.GetLatestCommitTouchingAsync(token, repo.RepoFullName, culprit, sha, cancellationToken)
                is { } commit)
            {
                return new SuspectCommitContext(commit.Sha, commit.Subject, commit.Author);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "suspect-commit lookup skipped repo={Repo}", repo.RepoFullName);
        }

        return null;
    }
}
