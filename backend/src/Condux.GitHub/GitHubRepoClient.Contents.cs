using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Condux.Core.SourceControl;

namespace Condux.GitHub;

/// <summary>The file contents half of the GitHub client: reading one file at a ref, and writing one on a
/// branch. Reading is where GitHub's several answers for a path (a file, a directory, a submodule, a link,
/// a file too large to return) are told apart, so a caller never mistakes one for an empty file or for
/// another file's contents.</summary>
public sealed partial class GitHubRepoClient
{
    /// <summary>Read a file at a ref. Null when the path does not exist there.</summary>
    public async Task<RepoFile?> GetFileAsync(
        string token, string repoFullName, string path, string gitRef, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            token, HttpMethod.Get,
            $"/repos/{ToRepoSegment(repoFullName)}/contents/{ToFilePath(path)}?ref={Uri.EscapeDataString(gitRef)}",
            body: null, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        resp.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        // The same route lists a directory (an array) and describes a submodule or a symlink, none of which
        // carries file contents. Asked for one by a path a model chose, that is a wrong path, not a fault.
        var file = document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.Deserialize<ContentResponse>()
            : null;
        if (file is null || file.Type is not (null or "file"))
        {
            throw new InvalidOperationException($"{path} is not a file.");
        }
        // Over 1 MB the default media type answers 200 with "encoding": "none" and an empty content field
        // (GitHub REST docs, "Get repository content", size limits).
        if (file.Encoding == "none" || file.Content is null)
        {
            return new RepoFile(path, null, file.Sha);
        }
        var bytes = Convert.FromBase64String(file.Content.Replace("\n", ""));
        // A symlink to a file answers as "file" with the TARGET's contents under the LINK's own sha, so the
        // contents and the sha disagree, which is the one tell it gives.
        if (GitBlobs.ToSha(bytes) != file.Sha)
        {
            throw new InvalidOperationException($"{path} is a link to another file, not a file; read the file it points to.");
        }
        return new RepoFile(path, ToText(bytes), file.Sha);
    }

    // Strict, so a binary file is reported as having no text rather than decoded lossily: replacement
    // characters would read as its contents, and written back they would corrupt it.
    private static string? ToText(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Create or update one file on a branch. <paramref name="existingSha"/> is the blob sha when
    /// the file already exists (required by GitHub for updates); null creates it.</summary>
    public async Task PutFileAsync(
        string token, string repoFullName, string path, string branch, string message, string content,
        string? existingSha, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            token, HttpMethod.Put, $"/repos/{ToRepoSegment(repoFullName)}/contents/{ToFilePath(path)}",
            new
            {
                message,
                branch,
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
                sha = existingSha,
            }, ct);
        resp.EnsureSuccessStatusCode();
    }

    private sealed record ContentResponse(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("sha")] string Sha,
        [property: JsonPropertyName("encoding")] string? Encoding);
}
