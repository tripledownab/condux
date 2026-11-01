using Condux.Core.Events;
using Condux.Core.Tickets;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Logging;

namespace Condux.Consumer;

/// <summary>
/// Opens a tracker ticket for a new error when the project asked for that (ADR-0053 slice 2). It only
/// reserves the ticket: the control plane's worker is the one caller of the tracker, so this process never
/// holds the grant or the key that opens it, and it handles input anyone with a DSN can send. Best effort
/// like the other dispatchers here: a failure is logged and never interrupts the drain.
/// </summary>
public sealed class AutoTicketDispatcher(
    ProjectRepository projects, ProjectTrackerMappingRepository mappings, TrackerConnectionRepository connections,
    IssueTicketRepository tickets, ILogger<AutoTicketDispatcher> logger)
{
    public async Task DispatchAsync(long projectId, UpsertResult upsert, Level level, CancellationToken ct = default)
    {
        var isNew = upsert.Occurrence == 1;
        if (!AutoTicketPolicy.IsCandidate(isNew, level))
        {
            return;
        }
        try
        {
            if (await mappings.GetAsync(projectId, TicketProvider.Linear, ct) is not { } mapping
                || !AutoTicketPolicy.ShouldCreate(mapping.AutoCreate, isNew, level)
                || await projects.GetAsync(projectId, ct) is not { } project
                || await connections.GetWorkspaceAsync(project.OrgId, TicketProvider.Linear, ct) is not { Broken: false } workspace)
            {
                return;
            }
            var reserved = await tickets.ReserveAutomaticAsync(
                upsert.Id, projectId, TicketProvider.Linear, workspace.WorkspaceId, AutoTicketPolicy.PerProjectPerDay,
                AutoTicketPolicy.SettleDelay, ct);
            // Debug, not a warning: anyone with a DSN can mint new errors past the ceiling, so a line per
            // refusal would hand them the log volume. The project's Linear tab shows the day's count.
            if (reserved == AutoReservation.CeilingReached)
            {
                logger.LogDebug("automatic ticket skipped, daily ceiling reached project={ProjectId}", projectId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "automatic ticket reservation failed project={ProjectId}", projectId);
        }
    }
}
