using System.Net;
using Condux.Storage.ClickHouse;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Condux.Consumer.Tests;

public class ClickHouseBatchTests
{
    /// <summary>
    /// Records the table each insert targeted, in order, and can be told to fail one of them. All
    /// three writers share one handler so the ordering across them is observable in a single list.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Inserted { get; } = [];

        public string? FailFor { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            var table = query.Split("INTO ")[1].Split(' ')[0];
            Inserted.Add(table);
            return Task.FromResult(new HttpResponseMessage(
                table == FailFor ? HttpStatusCode.InternalServerError : HttpStatusCode.OK));
        }
    }

    private static (ClickHouseBatch Batch, RecordingHandler Handler) Build()
    {
        var handler = new RecordingHandler();
        HttpClient Client() => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://clickhouse") };
        var batch = new ClickHouseBatch(
            new ClickHouseEventWriter(Client()),
            new ClickHouseIssueStatsWriter(Client()),
            new ClickHouseReleaseModuleWriter(Client()),
            NullLogger.Instance);
        return (batch, handler);
    }

    private static IssueStatsRow Stats() =>
        ClickHouseIssueStatsWriter.ToRow("7", 1, DateTimeOffset.UnixEpoch);

    private static ReleaseModuleRow Module() =>
        new("7", "1.4.2", "production", "npm", "lodash", "4.17.11", "2026-09-03 12:00:00", 90);

    private static EventRow EventRow() =>
        new("7", 1, "abc", "2026-09-03 12:00:00.000", "error", "javascript", "production", "1.4.2",
            "", "", "boom", "Error", "boom", "fp", [], "{}", 90, "");

    /// <summary>
    /// The ingest pipeline is written before the advisory extra, because an insert that throws ends
    /// the flush and so whatever runs last is the only one that can fail alone.
    /// </summary>
    [Fact]
    public async Task WritesTheIngestTablesBeforeTheAdvisoryOne()
    {
        var (batch, handler) = Build();
        batch.AddStats(Stats());
        batch.AddEvent(EventRow());
        batch.AddModules([Module()]);

        await batch.FlushAsync(CancellationToken.None);

        Assert.Equal(
            ["condux.issue_stats_1h", "condux.events", "condux.release_modules"], handler.Inserted);
    }

    /// <summary>
    /// Why that order is the one that matters, stated as the failure it prevents.
    ///
    /// condux.release_modules arrives in a migration, so there is a window on any deploy where the
    /// code exists and the table does not. With modules written first, that window stalls the stats
    /// and event writes too, which is the whole ingest pipeline stopped by the newest and least
    /// important table. Written last, a missing inventory table costs only the inventory.
    ///
    /// Swap the order back in FlushAsync and this test fails.
    /// </summary>
    [Fact]
    public async Task AMissingInventoryTableDoesNotStallTheIngestWrites()
    {
        var (batch, handler) = Build();
        handler.FailFor = "condux.release_modules";
        batch.AddStats(Stats());
        batch.AddEvent(EventRow());
        batch.AddModules([Module()]);

        await Assert.ThrowsAsync<HttpRequestException>(() => batch.FlushAsync(CancellationToken.None));

        // The events and the counts landed despite the inventory failing.
        Assert.Equal(["condux.issue_stats_1h", "condux.events", "condux.release_modules"], handler.Inserted);

        // And the next flush retries only the inventory, so nothing is written twice.
        handler.FailFor = null;
        handler.Inserted.Clear();
        await batch.FlushAsync(CancellationToken.None);
        Assert.Equal(["condux.release_modules"], handler.Inserted);
    }

    /// <summary>
    /// The reason each buffer is cleared as soon as its own insert succeeds, rather than all of them
    /// at the end. issue_stats_1h counts with a sum, so a row sent twice inflates the time series
    /// permanently and silently. Before this ordering, any failure in the events write left the stats
    /// buffer full and the next flush resent it.
    ///
    /// Revert FlushAsync to clearing at the end and this test fails on the second flush.
    /// </summary>
    [Fact]
    public async Task DoesNotResendAnEarlierTableWhenALaterInsertFails()
    {
        var (batch, handler) = Build();
        handler.FailFor = "condux.events";
        batch.AddStats(Stats());
        batch.AddEvent(EventRow());
        batch.AddModules([Module()]);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => batch.FlushAsync(CancellationToken.None));

        // The failing table is retried, because its rows were never accepted, and so is the inventory
        // behind it, which never got its turn. The stats that already landed are NOT resent, which is
        // the whole point: issue_stats_1h counts with a sum.
        handler.FailFor = null;
        handler.Inserted.Clear();
        await batch.FlushAsync(CancellationToken.None);

        Assert.Equal(["condux.events", "condux.release_modules"], handler.Inserted);
        Assert.DoesNotContain("condux.issue_stats_1h", handler.Inserted);
    }

    /// <summary>
    /// The two fixes in this class interact, and this is where. Clearing each buffer on its own success
    /// means a failure part way through leaves the later buffers full and the earlier ones empty. A
    /// flush trigger that asked only about stats would then be blind to the events still waiting, and
    /// on a topic that went quiet right after the failure nothing would ever retry them.
    /// </summary>
    [Fact]
    public async Task StillAsksToFlushWhenOnlyALaterBufferIsLeftHoldingRows()
    {
        var (batch, handler) = Build();
        handler.FailFor = "condux.events";
        batch.AddStats(Stats());
        batch.AddEvent(EventRow());

        await Assert.ThrowsAsync<HttpRequestException>(() => batch.FlushAsync(CancellationToken.None));

        // Stats flushed and cleared; the events row is still buffered and must not be forgotten.
        Assert.True(batch.ShouldFlush(idle: true));
    }

    [Fact]
    public void FlushesOnceTheStatsBufferIsFullOrTheTopicGoesIdle()
    {
        var (batch, _) = Build();

        Assert.False(batch.ShouldFlush(idle: false));
        // An idle tick with nothing buffered must not produce an empty write every second.
        Assert.False(batch.ShouldFlush(idle: true));

        batch.AddStats(Stats());
        Assert.False(batch.ShouldFlush(idle: false));
        Assert.True(batch.ShouldFlush(idle: true));
    }

    /// <summary>
    /// A flush with nothing buffered issues no requests at all. The writers short circuit an empty
    /// batch, and this pins that they still do once they share one insert helper.
    /// </summary>
    [Fact]
    public async Task WritesNothingWhenEveryBufferIsEmpty()
    {
        var (batch, handler) = Build();

        await batch.FlushAsync(CancellationToken.None);

        Assert.Empty(handler.Inserted);
    }

    /// <summary>
    /// The shutdown flush writes what the drain loop left behind.
    ///
    /// <para>This is the whole point of the method: the loop exits with a partial batch, and before this
    /// existed those rows were dropped while Kafka's auto-committed offsets moved past them, so they
    /// were gone rather than delayed. Remove the FlushAsync call from FlushOnShutdownAsync and this
    /// fails, because nothing is inserted.</para>
    /// </summary>
    [Fact]
    public async Task ShutdownFlushWritesTheRowsTheDrainLoopLeftBuffered()
    {
        var (batch, handler) = Build();
        batch.AddStats(Stats());

        await batch.FlushOnShutdownAsync();

        Assert.Contains("condux.issue_stats_1h", handler.Inserted);
    }

    /// <summary>
    /// A failing shutdown flush must not throw. It runs while the host is already stopping, so an
    /// exception here turns a clean shutdown into a crash and saves none of the rows. Drop the
    /// try/catch and this fails.
    /// </summary>
    [Fact]
    public async Task ShutdownFlushSwallowsAWriteFailureRatherThanCrashingTheShutdown()
    {
        var (batch, handler) = Build();
        handler.FailFor = "condux.issue_stats_1h";
        batch.AddStats(Stats());

        await batch.FlushOnShutdownAsync();

        // It tried, and the failure did not escape.
        Assert.Contains("condux.issue_stats_1h", handler.Inserted);
    }

    /// <summary>
    /// A shutdown with nothing buffered issues no request, so it cannot stall the process against the
    /// orchestrator's grace period.
    ///
    /// <para>This pins the writers' empty-insert short circuit as reached through the shutdown entry
    /// point, and nothing more. An earlier version of the method also carried its own emptiness guard,
    /// and this test could not tell whether that guard was present, which is why the guard is gone: the
    /// writers already provide the behaviour.</para>
    /// </summary>
    [Fact]
    public async Task ShutdownFlushWritesNothingWhenEveryBufferIsEmpty()
    {
        var (batch, handler) = Build();

        await batch.FlushOnShutdownAsync();

        Assert.Empty(handler.Inserted);
    }
}
