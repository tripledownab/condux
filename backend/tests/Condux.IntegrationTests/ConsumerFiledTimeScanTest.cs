using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The consumer files every event under its bounded time (EventMessages.ToFiledEvent) before anything
/// reads the time. The helpers are unit tested, but nothing runs the drain loop itself, so without this a
/// deleted or misplaced call would leave every test green while the issue, the rollup, the inventory and
/// the stored row all took the sender's claimed time again. A scan, because the loop needs a broker,
/// Postgres and ClickHouse to run, and this is the one line it depends on.
/// </summary>
public class ConsumerFiledTimeScanTest
{
    private const string FileTheEvent = "e = EventMessages.ToFiledEvent(e, result.Message);";

    // Every place the drain loop reads the event's time, directly or through the event.
    private static readonly string[] Readers =
    [
        "var seenAt = DateTimeOffset.FromUnixTimeMilliseconds(e.TimestampUnixMs);",
        "issues.UpsertAsync(",
        "autoFixDispatcher.DispatchAsync(",
        "ClickHouseIssueStatsWriter.ToRow(",
        "moduleRecorder.Collect(",
        "ClickHouseEventWriter.ToRow(",
    ];

    [Fact]
    public void The_drain_loop_files_the_event_before_anything_reads_its_time()
    {
        var path = Path.Combine(RepoFiles.FindRoot(), "backend", "src", "Condux.Consumer", "ConsumerWorker.cs");
        // Code lines only, so a call commented out does not count as made.
        var code = File.ReadAllLines(path).Select(line => line.Trim()).ToList();

        var filed = code.FindIndex(line => line == FileTheEvent);

        Assert.True(filed >= 0, $"ConsumerWorker no longer calls: {FileTheEvent}");
        Assert.All(Readers, reader =>
        {
            var at = code.FindIndex(line => !line.StartsWith("//", StringComparison.Ordinal)
                && line.Contains(reader, StringComparison.Ordinal));
            Assert.True(at >= 0, $"not found, so this test no longer checks it: {reader}");
            Assert.True(filed < at, $"read before the event is filed: {reader}");
        });
    }
}
