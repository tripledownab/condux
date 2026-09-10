using System.Formats.Tar;
using System.Text;
using Condux.Core.SourceControl;

namespace Condux.Agent.Sandbox;

/// <summary>
/// Tar packing and unpacking for the container's file API, which speaks nothing else. Uses the BCL's tar
/// support rather than shelling out, so the worker never needs a tar binary and this stays unit testable.
/// </summary>
public static class SandboxTar
{
    /// <summary>
    /// Pack repo-relative files into a tar archive Docker can expand into the workspace.
    ///
    /// An entry name is what Docker turns into a path on extraction, so a traversing one writes outside
    /// the workspace directory. The check is here rather than in the callers because this is the single
    /// place a name becomes a file: the workspace seeds a checkout through it and also stages every agent
    /// write through it, and guarding only the second would leave the first to be discovered later.
    /// </summary>
    public static byte[] FromFiles(IReadOnlyDictionary<string, string> files)
    {
        using var buffer = new MemoryStream();

        // Left open so the writer's disposal does not close the stream before the bytes are read back.
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, contents) in files)
            {
                var bytes = Encoding.UTF8.GetBytes(contents);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, RepoPaths.Normalize(path))
                {
                    DataStream = new MemoryStream(bytes),
                    // Readable and writable by the owner: the agent edits these files in place.
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                        | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                };
                writer.WriteEntry(entry);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The contents of the first regular file in an archive, or null when it holds none. Docker answers a
    /// single-path download with an archive of exactly that file, so the first entry is the one asked for.
    /// </summary>
    public static string? FirstFileContents(byte[] archive)
    {
        using var buffer = new MemoryStream(archive);
        using var reader = new TarReader(buffer);

        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || entry.DataStream is null)
            {
                continue;
            }

            using var contents = new StreamReader(entry.DataStream, Encoding.UTF8);
            return contents.ReadToEnd();
        }

        return null;
    }
}
