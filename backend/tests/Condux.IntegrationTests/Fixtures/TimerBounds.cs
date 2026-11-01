namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// The least a stopwatch can read across a wait a .NET timer ended (CancelAfter, Task.Delay). Such a timer
/// can fire marginally before a stopwatch started ahead of it reaches the due time: a test asserting the
/// stopwatch read at least the wait failed at 0.999489 s for a 1 s budget. The slack is a small part of
/// every wait these tests make, so the bound still tells a wait from none.
/// </summary>
public static class TimerBounds
{
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(50);

    public static TimeSpan ToLowerBound(TimeSpan due) => due - Slack;
}
