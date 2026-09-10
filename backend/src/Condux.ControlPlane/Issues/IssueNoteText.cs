namespace Condux.ControlPlane.Issues;

/// <summary>
/// What counts as an acceptable note body, in one place. The REST endpoint and the MCP
/// <c>add_issue_note</c> tool (ADR-0046) both write to the same column, so a limit enforced in only one
/// of them is not a limit at all. The error codes are the REST wire codes; the tool renders them as text.
/// </summary>
internal static class IssueNoteText
{
    /// <summary>The longest body accepted. A note is triage context, not an attachment.</summary>
    public const int MaxLength = 5_000;

    /// <summary>
    /// Trims a submitted body and checks it. On false, <paramref name="error"/> is the wire code and
    /// <paramref name="body"/> is empty.
    /// </summary>
    public static bool TryNormalize(string? raw, out string body, out string error)
    {
        body = raw?.Trim() ?? string.Empty;
        if (body.Length == 0)
        {
            error = "note_body_required";
            body = string.Empty;
            return false;
        }
        if (body.Length > MaxLength)
        {
            error = "note_too_long";
            body = string.Empty;
            return false;
        }
        error = string.Empty;
        return true;
    }
}
