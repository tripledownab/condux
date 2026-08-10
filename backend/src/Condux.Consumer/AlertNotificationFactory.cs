using Condux.Core.Alerting;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.Storage.Postgres;

namespace Condux.Consumer;

/// <summary>Decides which alert (if any) an ingested event fires and builds its
/// <see cref="AlertNotification"/>. Pure and dependency-free, so the event selection + field mapping is
/// unit-testable without standing up the worker (delivery itself is <c>AlertDispatcher</c>). A brand-new
/// issue (occurrence 1) fires <see cref="AlertEventType.NewIssue"/>, a reopened resolved issue fires
/// <see cref="AlertEventType.Regression"/>, and any other occurrence fires nothing (returns null).</summary>
public static class AlertNotificationFactory
{
    public static AlertNotification? Build(long projectId, UpsertResult upsert, Grouping grouping, Level level)
    {
        var eventType = upsert.Occurrence == 1 ? AlertEventType.NewIssue
            : upsert.Reopened ? AlertEventType.Regression
            : (AlertEventType?)null;
        return eventType is { } fired
            ? new AlertNotification(projectId, upsert.PublicId, grouping.Title, grouping.Culprit, level, fired)
            : null;
    }
}
