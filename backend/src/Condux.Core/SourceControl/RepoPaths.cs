namespace Condux.Core.SourceControl;

/// <summary>
/// The one home for what a repo-relative path may be, and how it is written into a source-host URL.
///
/// Two untrusted sources reach a source host: a model's chosen path, and a stack frame's filename from an
/// ingested event. Both are strings someone else authored, and both end up in a REST path sent with a
/// token that can read and write the customer's repository, so a path that escapes its prefix does not
/// merely read the wrong file, it addresses a different resource entirely.
///
/// The rule lived in two places before this file (a private helper on the in-memory workspace and an
/// inline check in the managed-agents gateway), which is why it was absent from the third and fourth
/// callers. It belongs beside <see cref="ISourceHostClient"/> because it constrains that seam's inputs,
/// so a GitLab or Bitbucket client inherits it rather than rediscovering it.
///
/// <b>Escaping does not prevent traversal, and must not be relied on for it.</b> Measured, not assumed:
/// <c>.</c> and <c>..</c> are unreserved in RFC 3986, so <c>Uri.EscapeDataString("..")</c> returns
/// <c>..</c> unchanged. Escaping the whole path in one call would encode the separators and stop the
/// traversal, but it would also turn <c>src/App.cs</c> into one segment named <c>src%2FApp.cs</c> and
/// break every nested path in the product. So <see cref="Normalize"/> is the only defence against a path
/// leaving the repo root, and escaping exists for a different problem: keeping a legal but awkward
/// filename from changing the request, since an unescaped <c>?</c> starts a query string and an
/// unescaped <c>#</c> starts a fragment.
///
/// That is why <see cref="ToUrlPath"/> pairs them: a caller reaching for the escape gets the check with
/// it. <see cref="EscapeSegments"/> stays public because a repository name and a branch also need
/// encoding and are not repo paths, so use it only for those.
/// </summary>
public static class RepoPaths
{
    /// <summary>
    /// The model chooses these paths, so they are untrusted input. Reject anything that could escape the
    /// repo root (absolute paths, drive letters, or a parent-directory segment) rather than normalizing it
    /// away, so a traversal attempt fails loudly as a tool error instead of silently writing elsewhere.
    /// </summary>
    public static string Normalize(string path)
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

        if (Traverses(trimmed))
        {
            throw new ArgumentException($"Path must not leave the repo root: {path}", nameof(path));
        }

        return trimmed.StartsWith("./", StringComparison.Ordinal) ? trimmed[2..] : trimmed;
    }

    /// <summary>Whether <see cref="Normalize"/> would accept this path, without throwing.</summary>
    public static bool IsRepoRelative(string? path)
    {
        if (path is null)
        {
            return false;
        }

        try
        {
            Normalize(path);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// A repo-relative path, ready to interpolate into a source-host URL: normalized (so it cannot leave
    /// the repo root) and then percent-encoded one segment at a time (so no character in a filename can
    /// change which resource is addressed). Throws for a path <see cref="Normalize"/> rejects.
    /// </summary>
    public static string ToUrlPath(string path) => EscapeSegments(Normalize(path));

    /// <summary>
    /// Percent-encodes a slash-separated value one segment at a time, leaving the separators as
    /// structure, and refuses any <c>..</c> segment. For values that are not repo paths but still go into
    /// a URL path: a <c>owner/name</c> repository, or a branch such as <c>feature/x</c>. For a file path
    /// use <see cref="ToUrlPath"/>, which additionally rejects rooted paths and drive letters.
    ///
    /// The <c>..</c> check is here rather than only in <see cref="Normalize"/> because escaping does not
    /// remove it: <c>..</c> is unreserved, so an escaped value keeps it and the URI parser still collapses
    /// it. A repository or branch has no repo root to leave, but it does have a URL prefix to leave, and
    /// that is the same attack against a different part of the path. So there is deliberately no way to
    /// get a traversing value into a URL through this type.
    ///
    /// Nothing legitimate is lost. Measured with <c>git check-ref-format --branch</c>: git itself refuses
    /// <c>feature/../../x</c>, <c>..</c>, <c>a..b</c> and <c>feature/..</c>. A single <c>.</c> segment is
    /// allowed because it is not traversal, it resolves to the same resource.
    /// </summary>
    public static string EscapeSegments(string value)
    {
        if (Traverses(value))
        {
            throw new ArgumentException($"Value must not traverse the URL path: {value}", nameof(value));
        }

        return string.Join('/', value.Split('/').Select(Uri.EscapeDataString));
    }

    /// <summary>
    /// The one traversal predicate, shared by both entry points above rather than written at each.
    ///
    /// Splits on BOTH separators. Windows-style input reaches these paths (a .NET stack frame reports
    /// <c>src\App.cs</c>), and the two entry points had already drifted apart on exactly that when the
    /// check was written twice: one converted backslashes first and the other did not, so a value
    /// carrying <c>..</c> between backslashes passed one and failed the other.
    /// </summary>
    private static bool Traverses(string value) => value.Split('/', '\\').Contains("..");
}
