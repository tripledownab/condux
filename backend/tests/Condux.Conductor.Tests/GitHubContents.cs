using System.Text;
using Condux.Core.SourceControl;

namespace Condux.Conductor.Tests;

/// <summary>
/// A GitHub "get repository content" answer for a file, as the stubs serve it. The SHA is the real blob
/// SHA of the contents, because the client checks the two agree (that is how it tells a symlink from a
/// file), so a made-up SHA would read as a link.
/// </summary>
internal static class GitHubContents
{
    /// <summary>The answer for a file holding <paramref name="text"/>.</summary>
    public static string ToJson(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return $$"""{"type":"file","encoding":"base64","content":"{{Convert.ToBase64String(bytes)}}","sha":"{{GitBlobs.ToSha(bytes)}}"}""";
    }

    /// <summary>The blob SHA of a file holding <paramref name="text"/>, for asserting what a commit replaced.</summary>
    public static string ToSha(string text) => GitBlobs.ToSha(Encoding.UTF8.GetBytes(text));
}
