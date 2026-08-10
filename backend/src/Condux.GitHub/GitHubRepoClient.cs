using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Condux.Core.SourceControl;

namespace Condux.GitHub;

/// <summary>An open Dependabot alert (CVE) on a repo (#117): the advisory ids + severity, the vulnerable
/// package and range, and the first patched version to bump to (null when none is published yet).</summary>
public sealed record DependabotAlert(
    string GhsaId,
    string? CveId,
    string Severity,
    string Summary,
    string Package,
    string Ecosystem,
    string VulnerableRange,
    string? FixedVersion,
    string HtmlUrl);

/// <summary>
/// The GitHub implementation of <see cref="ISourceHostClient"/> (#145): the repo operations a fix run
/// needs, over the GitHub REST API with an installation token per call — read a file, resolve a branch
/// head, create a branch, commit a file, and open a <b>draft</b> pull request (the draft flag is hard-coded
/// — the Conductor never opens a ready-for-review PR, ADR-0010). The token is passed per request, never
/// stored; like the rest of Condux.GitHub this is a thin HttpClient layer with no external NuGet deps.
/// Also exposes GitHub-specific reads not on the neutral interface (Dependabot alerts, #117).
/// </summary>
public sealed class GitHubRepoClient(HttpClient http, string apiBaseUrl = "https://api.github.com")
    : ISourceHostClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Read a file at a ref. Null when the path does not exist there.</summary>
    public async Task<RepoFile?> GetFileAsync(
        string token, string repoFullName, string path, string gitRef, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            token, HttpMethod.Get, $"/repos/{repoFullName}/contents/{path}?ref={Uri.EscapeDataString(gitRef)}",
            body: null, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        resp.EnsureSuccessStatusCode();
        var file = await resp.Content.ReadFromJsonAsync<ContentResponse>(ct)
            ?? throw new InvalidOperationException($"GitHub returned an empty contents response for {path}.");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(file.Content.Replace("\n", "")));
        return new RepoFile(path, decoded, file.Sha);
    }

    /// <summary>The commit sha a branch currently points at.</summary>
    public async Task<string> GetBranchHeadShaAsync(
        string token, string repoFullName, string branch, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            token, HttpMethod.Get, $"/repos/{repoFullName}/git/ref/heads/{branch}", body: null, ct);
        resp.EnsureSuccessStatusCode();
        var reference = await resp.Content.ReadFromJsonAsync<RefResponse>(ct)
            ?? throw new InvalidOperationException($"GitHub returned an empty ref response for {branch}.");
        return reference.Object.Sha;
    }

    /// <summary>Create a new branch at the given commit.</summary>
    public async Task CreateBranchAsync(
        string token, string repoFullName, string branch, string fromSha, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            token, HttpMethod.Post, $"/repos/{repoFullName}/git/refs",
            new { @ref = $"refs/heads/{branch}", sha = fromSha }, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Create or update one file on a branch. <paramref name="existingSha"/> is the blob sha when
    /// the file already exists (required by GitHub for updates); null creates it.</summary>
    public async Task PutFileAsync(
        string token, string repoFullName, string path, string branch, string message, string content,
        string? existingSha, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            token, HttpMethod.Put, $"/repos/{repoFullName}/contents/{path}",
            new
            {
                message,
                branch,
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
                sha = existingSha,
            }, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>The repo's branch names (first page, 100 — enough for a base-branch picker).</summary>
    public async Task<IReadOnlyList<string>> ListBranchesAsync(
        string token, string repoFullName, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            token, HttpMethod.Get, $"/repos/{repoFullName}/branches?per_page=100", body: null, ct);
        resp.EnsureSuccessStatusCode();
        var branches = await resp.Content.ReadFromJsonAsync<List<BranchResponse>>(ct) ?? [];
        return [.. branches.Select(b => b.Name)];
    }

    /// <summary>Every repo this installation can reach (first page, 100 — enough for a repo picker).
    /// Scoped by the installation token, so the list is exactly what a fix run could act on.</summary>
    public async Task<IReadOnlyList<string>> ListInstallationRepositoriesAsync(
        string token, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            token, HttpMethod.Get, "/installation/repositories?per_page=100", body: null, ct);
        resp.EnsureSuccessStatusCode();
        var page = await resp.Content.ReadFromJsonAsync<InstallationRepositoriesResponse>(ct);
        return [.. (page?.Repositories ?? []).Select(r => r.FullName)];
    }

    /// <summary>Open Dependabot alerts (CVEs) for a repo (#117): the vulnerable package, its severity,
    /// the vulnerable range and the first patched version. Needs the App's "Dependabot alerts: read"
    /// permission; without it GitHub 403s. First page (100), open alerts only.</summary>
    public async Task<IReadOnlyList<DependabotAlert>> ListDependabotAlertsAsync(
        string token, string repoFullName, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            token, HttpMethod.Get, $"/repos/{repoFullName}/dependabot/alerts?state=open&per_page=100",
            body: null, ct);
        resp.EnsureSuccessStatusCode();
        var alerts = await resp.Content.ReadFromJsonAsync<List<AlertResponse>>(ct) ?? [];
        return
        [
            .. alerts.Select(alert => new DependabotAlert(
                alert.SecurityAdvisory.GhsaId,
                alert.SecurityAdvisory.CveId,
                alert.SecurityAdvisory.Severity,
                alert.SecurityAdvisory.Summary,
                alert.SecurityVulnerability.Package.Name,
                alert.SecurityVulnerability.Package.Ecosystem,
                alert.SecurityVulnerability.VulnerableVersionRange,
                alert.SecurityVulnerability.FirstPatchedVersion?.Identifier,
                alert.HtmlUrl)),
        ];
    }

    /// <summary>The repo-relative paths of every file (blob) at a ref, via the recursive git-tree API
    /// (#113, to auto-derive code mappings). Only blobs are returned (directories dropped). GitHub caps a
    /// tree response at 100k entries and flags <c>truncated</c>; a truncated tree is used as-is (a partial
    /// file list still derives good prefix rules), which the caller may note.</summary>
    public async Task<IReadOnlyList<string>> ListTreeAsync(
        string token, string repoFullName, string gitRef, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            token, HttpMethod.Get,
            $"/repos/{repoFullName}/git/trees/{Uri.EscapeDataString(gitRef)}?recursive=1", body: null, ct);
        resp.EnsureSuccessStatusCode();
        var tree = await resp.Content.ReadFromJsonAsync<TreeResponse>(ct)
            ?? throw new InvalidOperationException($"GitHub returned an empty tree response for {repoFullName}.");
        return [.. tree.Tree.Where(entry => entry.Type == "blob").Select(entry => entry.Path)];
    }

    /// <summary>The most recent commit that touched <paramref name="path"/> at or before
    /// <paramref name="sha"/> — the strongest cheap "who last changed the code that threw" signal (#144).
    /// Null when the path has no commit history at that ref (e.g. it was added later). REST-only; GitHub's
    /// blame is GraphQL-only, so this is file-level, not line-level.</summary>
    public async Task<SuspectCommit?> GetLatestCommitTouchingAsync(
        string token, string repoFullName, string path, string sha, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            token, HttpMethod.Get,
            $"/repos/{repoFullName}/commits?path={Uri.EscapeDataString(path)}&sha={Uri.EscapeDataString(sha)}&per_page=1",
            body: null, ct);
        resp.EnsureSuccessStatusCode();
        var commits = await resp.Content.ReadFromJsonAsync<List<CommitResponse>>(ct) ?? [];
        if (commits.Count == 0)
        {
            return null;
        }

        var commit = commits[0];
        // The subject line only — the body can be long and adds little to the hint.
        var subject = commit.Commit.Message.Split('\n', 2)[0].Trim();
        return new SuspectCommit(commit.Sha, subject, commit.Author?.Login, commit.HtmlUrl);
    }

    /// <summary>Open a draft pull request and return its URL.</summary>
    public async Task<string> OpenDraftPullRequestAsync(
        string token, string repoFullName, string headBranch, string baseBranch, string title, string body,
        CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            token, HttpMethod.Post, $"/repos/{repoFullName}/pulls",
            new { title, body, head = headBranch, @base = baseBranch, draft = true }, ct);
        resp.EnsureSuccessStatusCode();
        var pr = await resp.Content.ReadFromJsonAsync<PullResponse>(ct)
            ?? throw new InvalidOperationException("GitHub returned an empty pull-request response.");
        return pr.HtmlUrl;
    }

    private async Task<HttpResponseMessage> SendAsync(
        string token, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, apiBaseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Condux", "1.0"));
        if (body is not null)
        {
            req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        }

        return await http.SendAsync(req, ct);
    }

    private sealed record ContentResponse(
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("sha")] string Sha);

    private sealed record RefResponse([property: JsonPropertyName("object")] RefObject Object);

    private sealed record RefObject([property: JsonPropertyName("sha")] string Sha);

    private sealed record PullResponse([property: JsonPropertyName("html_url")] string HtmlUrl);

    private sealed record BranchResponse([property: JsonPropertyName("name")] string Name);

    private sealed record InstallationRepositoriesResponse(
        [property: JsonPropertyName("repositories")] IReadOnlyList<RepositoryResponse>? Repositories);

    private sealed record RepositoryResponse([property: JsonPropertyName("full_name")] string FullName);

    private sealed record TreeResponse([property: JsonPropertyName("tree")] IReadOnlyList<TreeEntry> Tree);

    private sealed record TreeEntry(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("type")] string Type);

    // The list-commits JSON: top-level sha + html_url + the nested commit (message), plus the top-level
    // author (a GitHub user — required but may be an empty object when the committer has no GitHub account,
    // so login is optional). See https://docs.github.com/rest/commits/commits#list-commits.
    private sealed record CommitResponse(
        [property: JsonPropertyName("sha")] string Sha,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("commit")] CommitDetail Commit,
        [property: JsonPropertyName("author")] CommitAuthor? Author);

    private sealed record CommitDetail([property: JsonPropertyName("message")] string Message);

    private sealed record CommitAuthor([property: JsonPropertyName("login")] string? Login);

    // The Dependabot alert JSON: advisory ids/severity/summary, plus the alert-specific vulnerability
    // (its package + range + first patched version). See https://docs.github.com/rest/dependabot/alerts.
    private sealed record AlertResponse(
        [property: JsonPropertyName("security_advisory")] AdvisoryJson SecurityAdvisory,
        [property: JsonPropertyName("security_vulnerability")] VulnerabilityJson SecurityVulnerability,
        [property: JsonPropertyName("html_url")] string HtmlUrl);

    private sealed record AdvisoryJson(
        [property: JsonPropertyName("ghsa_id")] string GhsaId,
        [property: JsonPropertyName("cve_id")] string? CveId,
        [property: JsonPropertyName("severity")] string Severity,
        [property: JsonPropertyName("summary")] string Summary);

    private sealed record VulnerabilityJson(
        [property: JsonPropertyName("package")] PackageJson Package,
        [property: JsonPropertyName("vulnerable_version_range")] string VulnerableVersionRange,
        [property: JsonPropertyName("first_patched_version")] PatchedVersionJson? FirstPatchedVersion);

    private sealed record PackageJson(
        [property: JsonPropertyName("ecosystem")] string Ecosystem,
        [property: JsonPropertyName("name")] string Name);

    private sealed record PatchedVersionJson([property: JsonPropertyName("identifier")] string Identifier);
}
