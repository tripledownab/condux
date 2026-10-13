using System.Security.Cryptography;
using System.Text;

namespace Condux.Core.SourceControl;

/// <summary>
/// Git's own name for a file's contents: the SHA-1 of <c>blob &lt;size&gt;\0</c> followed by the bytes. A
/// source host reports a file's blob SHA beside its contents, and the two agree for a file; GitHub answers
/// a symlink to a file with the target's contents under the link's own SHA, so checking that they agree is
/// how a reader knows the text it holds is the file it asked for.
/// </summary>
public static class GitBlobs
{
    /// <summary>The blob SHA, in lower-case hex as hosts print it, of these contents.</summary>
    public static string ToSha(ReadOnlySpan<byte> contents)
    {
        var header = Encoding.ASCII.GetBytes($"blob {contents.Length}\0");
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(header);
        sha1.AppendData(contents);
        return Convert.ToHexStringLower(sha1.GetHashAndReset());
    }
}
