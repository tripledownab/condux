using System.Text.Json;
using Condux.Core.FixEngine;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Appends to a fix run's audit trail without letting the write fail the request.
///
/// Every caller has already committed the thing the entry describes — a queued run, a claimed lease, a
/// reported outcome. Throwing afterwards would answer an error for work that happened regardless, and in
/// the request path it would also refund an allowance the run is still going to spend. A missing audit
/// line is the smaller loss, so it is logged and swallowed, the same way alert dispatch and auto-fix treat
/// their own best-effort writes.
/// </summary>
internal static class FixAudit
{
    public static async Task TryWriteAsync(
        IFixStore fixes, ILogger logger, Guid fixId, string actor, string eventName, object detail,
        CancellationToken cancellationToken)
    {
        try
        {
            await fixes.AppendAuditAsync(
                fixId, actor, eventName, JsonSerializer.Serialize(detail), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "fix audit failed fix={FixId} event={Event}", fixId, eventName);
        }
    }
}
