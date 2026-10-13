using Condux.Core.SourceControl;

namespace Condux.Core.FixEngine;

/// <summary>
/// A checkout with the rest of the repository behind it. The checkout holds only the files fetched for the
/// issue, so a test file or a caller the model needs was simply absent: a read answered "no such file", and
/// a write then created a file of that name, which the host committed over the real one. Here a read the
/// checkout cannot answer goes to the repository at the base branch, and a write to an existing file the run
/// has not seen is refused back to the model, which can read it and try again. The read function it is given
/// can only read, so this workspace, like the others, has no way to push.
///
/// A file counts as seen only when the model received the repository's own contents for it, whole: from the
/// opening message, or from a read that returned exactly what the repository holds. Text a command left in
/// a sandbox at that path does not count, nor does a file the opening message had to cut short.
/// </summary>
public sealed class RepositoryWorkspace : IAgentWorkspace
{
    private readonly IAgentWorkspace checkout;
    private readonly Func<string, CancellationToken, Task<RepoFile?>> readRepository;
    private readonly Dictionary<string, RepoFile?> fromRepository = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RepoFile> seen = new(StringComparer.Ordinal);

    /// <param name="checkout">The workspace the run edits and, if it can, runs commands in.</param>
    /// <param name="fetched">The files fetched for the issue, which seed the checkout.</param>
    /// <param name="shownWhole">Those of them the opening message carried in full.</param>
    /// <param name="readRepository">One file at the base branch, or null when it does not exist.</param>
    public RepositoryWorkspace(
        IAgentWorkspace checkout, IEnumerable<RepoFile> fetched, IEnumerable<RepoFile> shownWhole,
        Func<string, CancellationToken, Task<RepoFile?>> readRepository)
    {
        this.checkout = checkout;
        this.readRepository = readRepository;
        foreach (var file in fetched)
        {
            fromRepository[RepoPaths.Normalize(file.Path)] = file;
        }
        foreach (var file in shownWhole)
        {
            seen[RepoPaths.Normalize(file.Path)] = file;
        }
    }

    /// <summary>What the run saw whole, for the host's check before it commits.</summary>
    public FilesSeen Seen => FilesSeen.Only(seen.Values);

    public IReadOnlyDictionary<string, string> ChangedFiles => checkout.ChangedFiles;

    public bool CanRunCommands => checkout.CanRunCommands;

    public Task<CommandResult> RunCommandAsync(
        IReadOnlyList<string> argv, CancellationToken cancellationToken = default) =>
        checkout.RunCommandAsync(argv, cancellationToken);

    public Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken cancellationToken = default) =>
        checkout.ListFilesAsync(cancellationToken);

    public async Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = RepoPaths.Normalize(path);
        var contents = await checkout.ReadFileAsync(normalized, cancellationToken);
        if (contents is null)
        {
            contents = ToText(await FetchAsync(normalized, cancellationToken));
        }
        // Seen only if what the model got is the repository's own file, so a staged edit or a command's
        // output at this path never stands in for it.
        if (contents is not null && fromRepository.GetValueOrDefault(normalized) is { } original
            && original.Content == contents)
        {
            seen[normalized] = original;
        }
        return contents;
    }

    public async Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        var normalized = RepoPaths.Normalize(path);
        if (!seen.ContainsKey(normalized) && !checkout.ChangedFiles.ContainsKey(normalized)
            && await FetchAsync(normalized, cancellationToken) is not null)
        {
            // Thrown as InvalidOperationException so the loop hands it back to the model as a failed tool
            // call, which it can recover from, rather than ending the run.
            throw new InvalidOperationException(
                $"{normalized} already exists and has not been read in this run. write_file replaces the "
                + "whole file, so read it first and write it back with your change.");
        }
        await checkout.WriteFileAsync(normalized, contents, cancellationToken);
    }

    // Any failure to read is reported to the model as a failed tool call rather than ending the run, and a
    // write that needed the answer is refused, since whether the file exists is then unknown.
    private async Task<RepoFile?> FetchAsync(string normalized, CancellationToken cancellationToken)
    {
        if (fromRepository.TryGetValue(normalized, out var file))
        {
            return file;
        }
        try
        {
            file = await readRepository(normalized, cancellationToken);
        }
        catch (HttpRequestException error)
        {
            throw new InvalidOperationException($"{normalized} could not be read from the repository.", error);
        }
        fromRepository[normalized] = file;
        return file;
    }

    // A file the host has but cannot give as text is neither readable nor absent: a failed read, so it is
    // never seen, and so never replaced either.
    private static string? ToText(RepoFile? file) =>
        file is { Content: null }
            ? throw new InvalidOperationException(
                $"{file.Path} has no text to read here (too large, or not text), so it cannot be read or changed in this run.")
            : file?.Content;
}
