using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Condux.Core.SourceControl;

namespace Condux.GitHub;

/// <summary>The pull request half of the GitHub client: opening the Conductor's draft pull request, and
/// finding one by its head branch for a run that stopped before it recorded the one it opened.</summary>
public sealed partial class GitHubRepoClient
{
    /// <summary>Open a draft pull request and return its URL.</summary>
    public async Task<string> OpenDraftPullRequestAsync(
        string token, string repoFullName, string headBranch, string baseBranch, string title, string body,
        CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            token, HttpMethod.Post, $"/repos/{ToRepoSegment(repoFullName)}/pulls",
            new { title, body, head = headBranch, @base = baseBranch, draft = true }, ct);
        resp.EnsureSuccessStatusCode();
        var pr = await resp.Content.ReadFromJsonAsync<PullResponse>(ct)
            ?? throw new InvalidOperationException("GitHub returned an empty pull-request response.");
        return pr.HtmlUrl;
    }

    /// <summary>
    /// The URL of a pull request from <paramref name="headBranch"/> in this repository, in any state, or
    /// null when there is none. GitHub filters on <c>head</c> only in the form <c>owner:branch</c>, and the
    /// Conductor pushes its branch to the repository itself, so the owner is the repository's. Without
    /// <c>state=all</c> the list holds open pull requests only, and one a reviewer closed would read as
    /// never opened. A pull request from another branch is refused rather than returned: it would mean the
    /// filter was not applied, and the caller would record somebody else's pull request as a run's success.
    /// </summary>
    public async Task<FoundPullRequest?> FindPullRequestAsync(
        string token, string repoFullName, string headBranch, CancellationToken ct = default)
    {
        var owner = repoFullName.Split('/', 2)[0];
        using var resp = await SendAsync(
            token, HttpMethod.Get,
            $"/repos/{ToRepoSegment(repoFullName)}/pulls?state=all&per_page=1&head={Uri.EscapeDataString($"{owner}:{headBranch}")}",
            body: null, ct);
        resp.EnsureSuccessStatusCode();
        var pulls = await resp.Content.ReadFromJsonAsync<List<ListedPull>>(ct) ?? [];
        if (pulls.Count == 0)
        {
            return null;
        }
        var pull = pulls[0];
        return pull.Head?.Ref == headBranch
            ? new FoundPullRequest(pull.HtmlUrl, pull.MergedAt)
            : throw new InvalidOperationException("GitHub listed a pull request from another branch.");
    }

    private sealed record PullResponse([property: JsonPropertyName("html_url")] string HtmlUrl);

    private sealed record ListedPull(
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("merged_at")] DateTimeOffset? MergedAt,
        [property: JsonPropertyName("head")] PullHead? Head);

    private sealed record PullHead([property: JsonPropertyName("ref")] string Ref);
}
