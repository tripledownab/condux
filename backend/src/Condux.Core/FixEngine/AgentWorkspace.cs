namespace Condux.Core.FixEngine;

/// <summary>
/// The checkout an agentic run reads and edits. Isolation is the workspace's problem, never the loop's:
/// the in-memory workspace holds fetched files and cannot execute anything, while a sandboxed workspace
/// backs the same operations with an ephemeral container. A workspace never holds a source-host token,
/// so an agent cannot push, and every write is staged for the host to commit as a draft pull request.
/// </summary>
public interface IAgentWorkspace
{
    /// <summary>The repo-relative paths the agent can see.</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>A file's contents, or null when it is not in the workspace.</summary>
    Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Stage a file's full new contents.</summary>
    Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default);

    /// <summary>The staged edits, repo-relative path to full contents. What the host commits.</summary>
    IReadOnlyDictionary<string, string> ChangedFiles { get; }
}

/// <summary>
/// A workspace over files already fetched from the source host, with edits staged in memory. This is the
/// default backend: it matches what the single-shot gateway could already do, adds iteration, and needs
/// no container, so it runs in CI unchanged. It executes nothing, so a prompt-injected instruction in a
/// source file has no command to reach.
/// </summary>
public sealed class InMemoryWorkspace : IAgentWorkspace
{
    private readonly Dictionary<string, string> files;
    private readonly Dictionary<string, string> changed = new(StringComparer.Ordinal);

    public InMemoryWorkspace(IReadOnlyDictionary<string, string> seed)
    {
        files = new Dictionary<string, string>(seed, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, string> ChangedFiles => changed;

    public Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. files.Keys.OrderBy(path => path, StringComparer.Ordinal)]);

    public Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePath(path);
        return Task.FromResult(files.TryGetValue(normalized, out var contents) ? contents : null);
    }

    public Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePath(path);
        files[normalized] = contents;
        changed[normalized] = contents;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The model chooses these paths, so they are untrusted input. Reject anything that could escape the
    /// repo root (absolute paths, drive letters, or a parent-directory segment) rather than normalizing it
    /// away, so a traversal attempt fails loudly as a tool error instead of silently writing elsewhere.
    /// </summary>
    private static string NormalizePath(string path)
    {
        var trimmed = path.Replace('\\', '/').Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Path is empty.", nameof(path));
        }

        if (trimmed.StartsWith('/') || trimmed.Contains(':'))
        {
            throw new ArgumentException($"Path must be repo-relative: {path}", nameof(path));
        }

        if (trimmed.Split('/').Any(segment => segment == ".."))
        {
            throw new ArgumentException($"Path must not leave the repo root: {path}", nameof(path));
        }

        return trimmed.StartsWith("./", StringComparison.Ordinal) ? trimmed[2..] : trimmed;
    }
}
