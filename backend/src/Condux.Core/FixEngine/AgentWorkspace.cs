using Condux.Core.SourceControl;

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

    /// <summary>
    /// Whether this workspace can execute commands. False on the in-memory default, and the loop offers
    /// the command tool only when it is true: a model told about a tool that always fails spends its
    /// budget discovering that, so the capability decides the tool set rather than the error message.
    /// </summary>
    bool CanRunCommands => false;

    /// <summary>
    /// Run an already-authorized argument vector in the workspace and capture its output. Implementations
    /// execute it directly, never through a shell. Only called when <see cref="CanRunCommands"/> is true.
    /// </summary>
    Task<CommandResult> RunCommandAsync(
        IReadOnlyList<string> argv, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This workspace cannot run commands.");
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

    // The model chooses these paths, so they are untrusted input. The rule is RepoPaths.Normalize and is
    // called directly rather than through a local alias: an alias is somewhere to add "just one" tweak,
    // and a second home for this rule is what let two other callers ship without it.
    public Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = RepoPaths.Normalize(path);
        return Task.FromResult(files.TryGetValue(normalized, out var contents) ? contents : null);
    }

    public Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        var normalized = RepoPaths.Normalize(path);
        files[normalized] = contents;
        changed[normalized] = contents;
        return Task.CompletedTask;
    }
}
