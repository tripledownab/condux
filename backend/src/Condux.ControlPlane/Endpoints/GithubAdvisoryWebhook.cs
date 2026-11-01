using Condux.Core.Alerting;
using Condux.GitHub;
using Condux.Notifications;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// The <c>dependabot_alert</c> webhook: an advisory that newly applies to a linked repository fires the
/// alert rules of every project linked to it, as an <see cref="AlertEventType.Advisory"/> event whose
/// level is the advisory's severity. Needs the GitHub App to subscribe to Dependabot alert events.
/// </summary>
internal static class GithubAdvisoryWebhook
{
    /// <summary>
    /// Claims each project the delivery reaches, then notifies them after the response. GitHub gives a
    /// delivery ten seconds and every channel may take far longer, so the request waits only for the claim.
    /// Delivery is at most once: a project is claimed before it is notified, so a notice a channel fails to
    /// take, or one cut off by a restart, is not sent again. It is best-effort like every other alert, since
    /// <see cref="AlertDispatcher"/> logs a failure rather than throwing.
    /// </summary>
    public static async Task HandleAsync(byte[] body, AdvisoryNoticeRepository notices, AlertDispatcher alerts)
    {
        if (DependabotAlertEvent.Parse(body) is not { } alertEvent)
        {
            return;
        }

        var finding = DependabotCveScanner.ToFinding(alertEvent.Alert);
        // The claim does not take the request's token either: GitHub hanging up after the insert commits
        // but before its rows are read would leave the projects claimed and never told.
        var projects = await notices.ClaimAsync(
            alertEvent.InstallationId, alertEvent.RepoFullName, finding.AdvisoryId,
            $"{finding.Ecosystem}:{finding.Package}", DateOnly.FromDateTime(DateTime.UtcNow), CancellationToken.None);
        _ = Task.Run(async () =>
        {
            foreach (var projectId in projects)
            {
                await alerts.DispatchAsync(
                    projectId, AlertEventType.Advisory, AdvisoryNotifications.ToNotification(projectId, finding),
                    CancellationToken.None);
            }
        }, CancellationToken.None);
    }
}
