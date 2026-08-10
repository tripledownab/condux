using Condux.Core.Alerting;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.Consumer.Tests;

// Covers the consumer's alert-firing seam (previously untested): which trigger an ingested event selects
// and how the AlertNotification fields map. The delivery itself is covered by the notifier/dispatcher tests.
public class AlertNotificationFactoryTests
{
    private static readonly Guid Pub = Guid.Parse("0192f000-0000-7000-8000-000000000000");
    private static readonly Grouping Group = new("fp", "TypeError: boom", "app.py:10");

    private static UpsertResult Upsert(long occurrence, bool reopened) =>
        new(Id: 42, Occurrence: occurrence, PublicId: Pub, Reopened: reopened);

    [Fact]
    public void New_issue_occurrence_one_fires_NewIssue_with_mapped_fields()
    {
        var notification = AlertNotificationFactory.Build(7, Upsert(1, reopened: false), Group, Level.Error);

        Assert.NotNull(notification);
        Assert.Equal(AlertEventType.NewIssue, notification!.EventType);
        Assert.Equal(7, notification.ProjectId);
        Assert.Equal(Pub, notification.IssuePublicId);
        Assert.Equal("TypeError: boom", notification.Title);
        Assert.Equal("app.py:10", notification.Culprit);
        Assert.Equal(Level.Error, notification.Level);
    }

    [Fact]
    public void Reopened_resolved_issue_fires_Regression()
    {
        // A repeat occurrence (not 1) that reopened a resolved issue is a regression, not a new issue.
        var notification = AlertNotificationFactory.Build(7, Upsert(9, reopened: true), Group, Level.Fatal);

        Assert.NotNull(notification);
        Assert.Equal(AlertEventType.Regression, notification!.EventType);
        Assert.Equal(Level.Fatal, notification.Level);
    }

    [Fact]
    public void A_repeat_occurrence_that_did_not_reopen_fires_nothing()
    {
        Assert.Null(AlertNotificationFactory.Build(7, Upsert(2, reopened: false), Group, Level.Error));
    }
}
