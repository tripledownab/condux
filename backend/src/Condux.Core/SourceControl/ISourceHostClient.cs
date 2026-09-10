namespace Condux.Core.SourceControl;

/// <summary>A file read from a source repo: its repo-relative path, decoded contents, and blob sha
/// (needed to update it in place).</summary>
public sealed record RepoFile(string Path, string Content, string Sha);

/// <summary>The commit most likely to have introduced an error (#144): the last commit that touched the
/// culprit file at or before a release commit — its sha, subject line, author (null when the commit has no
/// associated account), and URL. A "suspect commit" hint for the fix.</summary>
public sealed record SuspectCommit(string Sha, string Subject, string? Author, string HtmlUrl);

/// <summary>
/// The git operations a Conductor fix run needs, over one hosted source provider — GitHub today, GitLab
/// and others later (#145). Every method takes a per-call installation/access token that the implementation
/// never stores. Adding a provider is a new implementation of this interface; nothing above it (the fix
/// gateway, the endpoints) hard-codes a provider. Built like the BYO-key <c>IModelClient</c> seam.
/// </summary>
public interface ISourceHostClient
{
    /// <summary>Read a file at a ref. Null when the path does not exist there.</summary>
    Task<RepoFile?> GetFileAsync(
        string token, string repoFullName, string path, string gitRef, CancellationToken ct = default);

    /// <summary>The commit sha a branch currently points at.</summary>
    Task<string> GetBranchHeadShaAsync(
        string token, string repoFullName, string branch, CancellationToken ct = default);

    /// <summary>Create a new branch at the given commit.</summary>
    Task CreateBranchAsync(
        string token, string repoFullName, string branch, string fromSha, CancellationToken ct = default);

    /// <summary>Create or update one file on a branch. <paramref name="existingSha"/> is the blob sha when
    /// the file already exists (required for updates); null creates it.</summary>
    Task PutFileAsync(
        string token, string repoFullName, string path, string branch, string message, string content,
        string? existingSha, CancellationToken ct = default);

    /// <summary>The repo's branch names (enough for a base-branch picker).</summary>
    Task<IReadOnlyList<string>> ListBranchesAsync(
        string token, string repoFullName, CancellationToken ct = default);

    /// <summary>
    /// The <c>owner/name</c> of every repo this installation can reach, for a repo picker. Scoped by the
    /// token, so it answers "what may we actually touch?" rather than "what exists": linking anything
    /// outside this list would produce a link no fix run could ever use.
    /// </summary>
    Task<IReadOnlyList<string>> ListInstallationRepositoriesAsync(
        string token, CancellationToken ct = default);

    /// <summary>The repo-relative paths of every file (blob) at a ref.</summary>
    Task<IReadOnlyList<string>> ListTreeAsync(
        string token, string repoFullName, string gitRef, CancellationToken ct = default);

    /// <summary>The most recent commit that touched <paramref name="path"/> at or before
    /// <paramref name="sha"/> (#144). Null when the path has no history at that ref.</summary>
    Task<SuspectCommit?> GetLatestCommitTouchingAsync(
        string token, string repoFullName, string path, string sha, CancellationToken ct = default);

    /// <summary>Open a draft pull request (a draft merge request on providers that call it that) and return
    /// its URL. The draft flag is non-negotiable — the Conductor never opens a ready-for-review PR.</summary>
    Task<string> OpenDraftPullRequestAsync(
        string token, string repoFullName, string headBranch, string baseBranch, string title, string body,
        CancellationToken ct = default);
}
