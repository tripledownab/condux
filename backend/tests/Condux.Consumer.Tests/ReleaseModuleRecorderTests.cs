using Condux.Core.Events;
using Condux.Core.Scrub;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Condux.Consumer.Tests;

public class ReleaseModuleRecorderTests
{
    private static readonly DateTimeOffset SeenAt = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static ReleaseModuleRecorder Recorder() => new(NullLogger.Instance);

    private static Event EventWith(
        Dictionary<string, string> modules, string release = "1.4.2", string environment = "production") =>
        new() { Release = release, Environment = environment, Platform = "javascript", Modules = modules };

    [Fact]
    public void RecordsEachModuleOnceAndThenNothingForTheSameRelease()
    {
        var recorder = Recorder();
        var e = EventWith(new Dictionary<string, string> { ["lodash"] = "4.17.11", ["express"] = "4.16.0" });

        var first = recorder.Collect("7", e, SeenAt, 90);
        var second = recorder.Collect("7", e, SeenAt, 90);

        Assert.Equal(2, first.Count);
        // The point of the recorder. Without this a busy release would insert its whole dependency
        // tree on every single event it reports.
        Assert.Empty(second);
    }

    /// <summary>
    /// The memory keys on everything the table sorts on, so a genuinely different row is still
    /// recorded. If it keyed on the package alone, a deploy of a new version would be silently
    /// swallowed and the inventory would report the old one forever.
    /// </summary>
    [Theory]
    [InlineData("1.4.3", "production", "4.17.11")] // a new release
    [InlineData("1.4.2", "staging", "4.17.11")]    // the same release in another environment
    [InlineData("1.4.2", "production", "4.17.21")] // the same release, upgraded package
    public void RecordsAgainWhenAnyPartOfTheRowDiffers(string release, string environment, string version)
    {
        var recorder = Recorder();
        recorder.Collect("7", EventWith(new Dictionary<string, string> { ["lodash"] = "4.17.11" }), SeenAt, 90);

        var second = recorder.Collect(
            "7",
            EventWith(new Dictionary<string, string> { ["lodash"] = version }, release, environment),
            SeenAt,
            90);

        Assert.Single(second);
    }

    /// <summary>
    /// The row is rewritten on a new day, and this is a correctness requirement rather than a nicety.
    /// condux.release_modules expires a row at last_seen + retention_days, so an inventory only
    /// survives while something refreshes last_seen. Remember a row forever and a consumer that runs
    /// longer than the retention window would watch a live release's inventory expire underneath it
    /// while that release was still reporting every minute.
    /// </summary>
    [Fact]
    public void RecordsTheSameRowAgainOnTheNextDaySoLastSeenKeepsMoving()
    {
        var recorder = Recorder();
        var e = EventWith(new Dictionary<string, string> { ["lodash"] = "4.17.11" });

        Assert.Single(recorder.Collect("7", e, SeenAt, 90));
        Assert.Empty(recorder.Collect("7", e, SeenAt.AddHours(6), 90));
        Assert.Single(recorder.Collect("7", e, SeenAt.AddDays(1), 90));
    }

    [Fact]
    public void KeepsProjectsApart()
    {
        var recorder = Recorder();
        var e = EventWith(new Dictionary<string, string> { ["lodash"] = "4.17.11" });

        Assert.Single(recorder.Collect("7", e, SeenAt, 90));
        Assert.Single(recorder.Collect("8", e, SeenAt, 90));
    }

    /// <summary>
    /// A version the scrub replaced is not a version. No row reads as unknown, which is true; a row
    /// saying "[redacted]" would render as a running version that matches no advisory and means
    /// nothing to the reader.
    /// </summary>
    [Fact]
    public void DoesNotRecordARedactedVersion()
    {
        var rows = Recorder().Collect(
            "7",
            EventWith(new Dictionary<string, string> { ["jsonwebtoken"] = Scrubber.Redacted }),
            SeenAt,
            90);

        Assert.Empty(rows);
    }

    [Fact]
    public void StampsTheProjectTierRetentionOnEachRow()
    {
        var rows = Recorder().Collect(
            "7", EventWith(new Dictionary<string, string> { ["lodash"] = "4.17.11" }), SeenAt, 30);

        Assert.Equal(30, Assert.Single(rows).retention_days);
    }

    [Fact]
    public void RecordsTheEcosystemThePlatformMapsToAndNotTheRawPlatform()
    {
        var rows = Recorder().Collect(
            "7", EventWith(new Dictionary<string, string> { ["lodash"] = "4.17.11" }), SeenAt, 90);

        Assert.Equal("npm", Assert.Single(rows).ecosystem);
    }
}
