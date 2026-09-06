using Condux.Storage.ClickHouse;
using Microsoft.Extensions.Logging;

namespace Condux.Consumer;

/// <summary>
/// The consumer's ClickHouse write side: the three row buffers and the one flush that drains them.
/// Kept out of the drain loop because accumulating and writing is a separate job from consuming, and
/// nothing else in here belongs to it.
///
/// <para>Flush cadence follows the stats buffer, which fills fastest because it takes a row for every
/// event before the sampling decision. The event buffer is sampled and the module buffer is
/// deduplicated, so both are smaller and ride the same flush rather than keeping their own timer.</para>
/// </summary>
public sealed class ClickHouseBatch(
    ClickHouseEventWriter eventWriter,
    ClickHouseIssueStatsWriter statsWriter,
    ClickHouseReleaseModuleWriter moduleWriter,
    ILogger logger)
{
    private const int BatchSize = 100;

    /// <summary>
    /// How long <see cref="FlushOnShutdownAsync"/> may take.
    ///
    /// <para>It has to be bounded, because a shutdown runs on the orchestrator's clock: SIGTERM, then
    /// SIGKILL once the grace period expires. <b>The process cannot read that grace period, and it is
    /// not portable</b>, so the deployment states it rather than the code assuming it:
    /// <c>stop_grace_period</c> in both compose files, and Kubernetes' 30s default for the chart. A
    /// bare Docker daemon was measured killing at 1s, where this timeout could never fire at all.
    /// Raising this means raising those first.</para>
    /// </summary>
    private static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly List<EventRow> events = [];
    private readonly List<IssueStatsRow> stats = [];
    private readonly List<ReleaseModuleRow> modules = [];
    private int sampledOut;

    public void AddStats(IssueStatsRow row) => stats.Add(row);

    public void AddEvent(EventRow row) => events.Add(row);

    public void AddModules(IReadOnlyList<ReleaseModuleRow> rows) => modules.AddRange(rows);

    /// <summary>An event whose raw payload the sampler shed. Counted only so the flush log is honest.</summary>
    public void CountSampledOut() => sampledOut++;

    /// <summary>
    /// Flush on a full batch, or on an idle tick so a quiet topic still lands what it has. Fullness is
    /// judged on the stats buffer, which is the one that takes a row per event.
    ///
    /// <para>The idle case asks whether ANY buffer holds rows, and that is not tidiness. Because
    /// <see cref="FlushAsync"/> clears each buffer as its own insert succeeds, a failure part way
    /// through leaves the later buffers full while the earlier ones are empty. Asking only about stats
    /// would then miss the events still waiting, and on a topic that went quiet straight after the
    /// failure nothing would ever trigger their retry.</para>
    /// </summary>
    public bool ShouldFlush(bool idle) =>
        stats.Count >= BatchSize || (idle && (stats.Count > 0 || events.Count > 0 || modules.Count > 0));

    /// <summary>
    /// Write each buffer and clear it as soon as its own insert succeeds, so a failure in a later write
    /// cannot cause an earlier one to be sent twice. That matters for the stats rollup in particular,
    /// where count is a sum and a repeat inflates it permanently.
    ///
    /// <para><b>Order is by how much its failure costs, most costly first.</b> An insert that throws
    /// ends the flush, so whatever runs last is the only one that can fail without taking the others
    /// with it. The module inventory is the one that can afford that: it is advisory, and its table
    /// merges on a sort key so a retry is free. The stats and the events are the ingest pipeline
    /// itself. Putting modules first, which reads as tidy because it is the idempotent one, would mean
    /// a single problem with the newest and least important table stalls the two writes the product
    /// actually depends on. That is not hypothetical: the table arrives in a migration, and a deploy
    /// that applied the code before the schema would have done exactly this.</para>
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var (moduleCount, statsCount, eventCount, shed) = (modules.Count, stats.Count, events.Count, sampledOut);

        await statsWriter.InsertAsync(stats, cancellationToken);
        stats.Clear();
        await eventWriter.InsertAsync(events, cancellationToken);
        events.Clear();
        await moduleWriter.InsertAsync(modules, cancellationToken);
        modules.Clear();
        sampledOut = 0;

        logger.LogInformation(
            "flushed {Stats} stat rows + {Events} events + {Modules} module rows to ClickHouse (sampled out {Shed})",
            statsCount, eventCount, moduleCount, shed);
    }

    /// <summary>
    /// Write whatever is still buffered, for a caller that is shutting down.
    ///
    /// <para>Without this the consumer's drain loop broke out of its while and closed the Kafka consumer
    /// with the buffers full, discarding up to <c>BatchSize - 1</c> stats rows plus any pending events
    /// and module rows. Those were not merely delayed: <c>EnableAutoCommit</c> is left at librdkafka's
    /// default of true, so the offsets for those messages are committed on a timer and again by
    /// <c>Close()</c>, and the restarted consumer resumes past them. <c>issue_stats_1h</c> is documented
    /// as the EXACT time series and a deploy restarts the worker, so this quietly made it inexact on
    /// every deploy, with nothing anywhere saying so.</para>
    ///
    /// <para><b>It takes no token on purpose.</b> The caller's token is already cancelled by the time it
    /// gets here, so accepting one would invite passing it, every write would throw immediately, and the
    /// bug would be preserved behind code that looks like a fix.</para>
    ///
    /// <para><b>It never throws.</b> This runs while the host is already stopping, where an exception
    /// replaces a clean shutdown with a crash and saves nothing. Losing the rows to a failed flush is
    /// exactly the old behaviour, so the worst case is unchanged while the normal case is fixed.</para>
    /// </summary>
    public async Task FlushOnShutdownAsync()
    {
        // No emptiness guard, deliberately. One was written and removed: each writer already skips an
        // empty insert, so its only observable effect was suppressing a "flushed 0 rows" log line, and
        // mutation-testing showed no test could tell whether it was there. A branch nothing can
        // distinguish is a branch that will be wrong one day without anything noticing.
        try
        {
            using var timeout = new CancellationTokenSource(ShutdownFlushTimeout);
            await FlushAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "final ClickHouse flush failed on shutdown; buffered rows were dropped");
        }
    }
}
