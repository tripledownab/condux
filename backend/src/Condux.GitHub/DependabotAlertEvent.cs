using System.Text.Json;

namespace Condux.GitHub;

/// <summary>
/// A <c>dependabot_alert</c> webhook delivery that says an advisory newly applies to a repository: the
/// installation it came through, the repository and the alert. GitHub describes three actions that way: an
/// alert <c>created</c>, one <c>reintroduced</c> after a fix, and one <c>auto_reopened</c> because the
/// auto-triage rule that closed it no longer applies. The rest (dismissed, fixed, reopened by a person,
/// withdrawn and so on) parse to null, as does an alert whose state is not open, since one already
/// dismissed or fixed is no longer news.
/// </summary>
public sealed record DependabotAlertEvent(long InstallationId, string RepoFullName, DependabotAlert Alert)
{
    private static readonly string[] NewAlertActions = ["created", "reintroduced", "auto_reopened"];

    /// <summary>The event in <paramref name="body"/>, or null when it is not a new alert or lacks what
    /// routing it needs.</summary>
    public static DependabotAlertEvent? Parse(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String
            || !NewAlertActions.Contains(action.GetString())
            || !root.TryGetProperty("installation", out var installation)
            || installation.ValueKind != JsonValueKind.Object
            || !installation.TryGetProperty("id", out var id) || !id.TryGetInt64(out var installationId)
            || !root.TryGetProperty("repository", out var repository) || repository.ValueKind != JsonValueKind.Object
            || !repository.TryGetProperty("full_name", out var fullName) || fullName.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("alert", out var alertElement) || alertElement.ValueKind != JsonValueKind.Object
            || (alertElement.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                && state.GetString() != "open")
            || GitHubRepoClient.ToDependabotAlert(alertElement) is not { } alert)
        {
            return null;
        }

        return new DependabotAlertEvent(installationId, fullName.GetString()!, alert);
    }
}
