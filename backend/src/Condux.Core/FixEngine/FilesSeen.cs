using Condux.Core.SourceControl;

namespace Condux.Core.FixEngine;

/// <summary>
/// The repository files a fix run saw in full before it proposed its changes, each by the blob it saw. A
/// proposal carries complete file contents, so a change to a file the run never read replaces that file
/// with whatever the model imagined it held: a run once rewrote a test file it had never been shown and
/// deleted every test in it. The host asks this before it commits, against the file's current blob, so no
/// gateway can publish such a change, and a file that changed since the run read it is refused too.
/// </summary>
public sealed class FilesSeen
{
    private readonly Dictionary<string, string> blobs;

    private FilesSeen(Dictionary<string, string> blobs) => this.blobs = blobs;

    /// <summary>A run that saw exactly these files, whole, as these blobs.</summary>
    public static FilesSeen Only(IEnumerable<RepoFile> files)
    {
        // Two spellings of one path are one file, so a later one replaces the earlier rather than throwing.
        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            blobs[RepoPaths.Normalize(file.Path)] = file.Sha;
        }
        return new FilesSeen(blobs);
    }

    /// <summary>Whether the run saw this path as a file at all, whatever blob it was then.</summary>
    public bool HasSeen(string path) => blobs.ContainsKey(RepoPaths.Normalize(path));

    /// <summary>Whether the run saw this file whole, as the blob it is now.</summary>
    public bool Includes(string path, string sha) =>
        blobs.TryGetValue(RepoPaths.Normalize(path), out var seen) && seen == sha;
}
