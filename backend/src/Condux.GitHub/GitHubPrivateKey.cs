namespace Condux.GitHub;

/// <summary>
/// Resolves the GitHub App private key to PEM text. The inline value wins; otherwise the <c>.pem</c> at
/// the given path is read. Absent means null, and each caller decides what absence means to it: the
/// control-plane treats it as the app being switched off, while the Conductor's anthropic provider
/// cannot run without it.
///
/// <para>Present but unreadable is neither, and is the case this exists for. The containers run as a
/// non-root user, so a host-mounted key owned by root is invisible to them, and the bare
/// <see cref="UnauthorizedAccessException"/> that surfaces points at nothing: startup dies, every route
/// 502s, and the reported symptom is unrelated (sign-in buttons vanish, because the login page hides
/// them when the providers endpoint is unreachable). It therefore still fails fast, but with a message
/// naming the cause and the fix.</para>
///
/// <para>This lives in <c>Condux.GitHub</c> because two deployments read the same key and only one of
/// them had learned that. The control-plane grew the diagnostic after an outage; the Conductor kept a
/// second copy that called <c>File.ReadAllText</c> bare, under a comment claiming the two matched. A
/// caller may still choose its own absence policy, which is the only difference either of them
/// actually wanted.</para>
/// </summary>
public static class GitHubPrivateKey
{
    /// <param name="inline">The key's PEM text, supplied directly. Wins when set.</param>
    /// <param name="path">Path to a mounted <c>.pem</c>, read only when <paramref name="inline"/> is empty.</param>
    /// <returns>The PEM text, or null when neither is configured or the path does not exist.</returns>
    /// <exception cref="InvalidOperationException">The file exists but cannot be read.</exception>
    public static string? Resolve(string? inline, string? path)
    {
        if (!string.IsNullOrEmpty(inline))
        {
            return inline;
        }

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception failure) when (failure is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"The GitHub App private key at '{path}' exists but could not be read as "
                + $"'{Environment.UserName}'. A host-mounted key must be owned by the user the container "
                + "runs as, not root: derive that uid from the image rather than assuming it "
                + "(docker inspect <container> --format '{{.Config.User}}') and chown the file to it.",
                failure);
        }
    }
}
