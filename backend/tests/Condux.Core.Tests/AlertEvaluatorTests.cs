using Condux.Core.Alerting;
using Condux.Core.Events;
using Xunit;

namespace Condux.Core.Tests;

public class AlertEvaluatorTests
{
    // Default event set is new + regression and level set is Error + Fatal, so a test that varies one gate
    // (event/level/enabled) isn't also gated by the others — each isolates the gate it names.
    private static AlertRule Rule(
        AlertEventType[]? events = null, Level[]? levels = null, bool enabled = true) =>
        new(Guid.NewGuid(), 1, "rule",
            events ?? [AlertEventType.NewIssue, AlertEventType.Regression],
            levels ?? [Level.Error, Level.Fatal], enabled);

    [Fact]
    public void Matches_WhenLevelIsInTheSet_AndRuleSubscribesToTheEvent()
    {
        Assert.True(AlertEvaluator.Matches(Rule(levels: [Level.Error]), AlertEventType.NewIssue, Level.Error));
        Assert.True(AlertEvaluator.Matches(
            Rule(levels: [Level.Warning, Level.Fatal]), AlertEventType.Regression, Level.Fatal));
    }

    [Fact]
    public void Matches_TheNewTriageEvents()
    {
        // Resolved/Assigned fire only for rules that subscribe to exactly them.
        Assert.True(AlertEvaluator.Matches(
            Rule(events: [AlertEventType.Resolved]), AlertEventType.Resolved, Level.Error));
        Assert.True(AlertEvaluator.Matches(
            Rule(events: [AlertEventType.Assigned]), AlertEventType.Assigned, Level.Error));
    }

    [Fact]
    public void NoMatch_WhenLevelNotInTheSet()
    {
        // A rule targeting exactly Warning does NOT fire on Error — the point of a level set vs a threshold.
        Assert.False(AlertEvaluator.Matches(Rule(levels: [Level.Warning]), AlertEventType.NewIssue, Level.Error));
        Assert.False(AlertEvaluator.Matches(Rule(levels: [Level.Error]), AlertEventType.NewIssue, Level.Warning));
    }

    [Fact]
    public void NoMatch_WhenRuleDoesNotSubscribeToTheEvent()
    {
        Assert.False(AlertEvaluator.Matches(
            Rule(events: [AlertEventType.Regression]), AlertEventType.NewIssue, Level.Fatal));
        Assert.False(AlertEvaluator.Matches(
            Rule(events: [AlertEventType.NewIssue]), AlertEventType.Assigned, Level.Fatal));
    }

    [Fact]
    public void NoMatch_WhenRuleDisabled()
    {
        Assert.False(AlertEvaluator.Matches(Rule(enabled: false), AlertEventType.NewIssue, Level.Fatal));
    }
}
