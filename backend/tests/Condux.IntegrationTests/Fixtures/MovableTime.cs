namespace Condux.IntegrationTests.Fixtures;

// A clock a test moves by hand, for code that reads its time from a TimeProvider.
public sealed class MovableTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
