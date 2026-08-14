using Microsoft.Extensions.Logging;

namespace Condux.Storage.Postgres;

/// <summary>
/// Nudges a project's live dashboard through the ADR-0030 event channel, without letting the nudge fail
/// work that is already committed. A missed nudge is caught by the slow safety poll; a failed request or
/// a crashed worker loop would not be.
///
/// This is how a change that happens outside any browser — the consumer landing an issue, a runner
/// claiming or finishing a job, the hosted Conductor concluding a run — reaches the screens that are
/// open. Lives beside <see cref="ProjectEventNotifier"/> because every service that can notify needs the
/// same best-effort wrapping, and the third inlined copy of it is how the copies start to drift.
/// </summary>
public static class ProjectEventNudge
{
    public static async Task TrySendAsync(
        ProjectEventNotifier projectEvents, ILogger logger, long projectId, CancellationToken ct)
    {
        try
        {
            await projectEvents.NotifyAsync(projectId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "project event notify failed project={ProjectId}", projectId);
        }
    }
}
