using Condux.Storage.Postgres;
using Condux.Telemetry;

namespace Condux.ControlPlane.Uploads;

/// <summary>
/// Keeps what CI uploads within what each org may keep. Every pass expires the artifacts older than
/// their tier's retention, then deletes the objects the queue holds: those expired here, those evicted past
/// a quota at upload time, and those of deleted projects. Once a day it also sweeps storage for objects no
/// row names (<see cref="OrphanObjectSweep"/>), so a delete that never reached one is made up for. Registered
/// only when object storage is on.
///
/// Every replica runs it. Expiry is a guarded DELETE, so two passes queue each key once. Two drains can
/// take the same keys: a single key is deleted only by the drain that takes it off the queue (see
/// <see cref="ObjectDeletionQueue"/>), and a prefix may be deleted twice, which S3 answers as a success.
/// </summary>
public sealed class UploadStorageWorker(
    UploadStorage storage,
    ObjectDeletionQueue queue,
    OrphanObjectSweep sweep,
    ILogger<UploadStorageWorker> logger,
    ConduxSelfReporter selfReport)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    // Bounds one pass, not the work: what a pass does not reach is drained on the next one.
    internal const int MaxPerPass = 500;

    // A sweep lists the whole bucket, so it runs far less often than the drain. Per replica, in memory, so
    // a restart, which any deploy that changes the control-plane causes, sweeps on its first pass. Replicas
    // sweeping the same day may queue a key twice, which the drain absorbs: its claim finds the entry gone,
    // or the object already deleted.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromDays(1);
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;
    private bool _sweepFailing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        // Waits before the first tick, like the SSO worker: replicas start together, and nothing here is
        // urgent enough to send all of them at storage the moment a deploy lands.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "upload storage pass error");
                selfReport.Report(ex); // Condux on Condux (#75)
            }
        }
    }

    // A failed sweep must not cost the pass its drain, and is tried again on the next pass rather than the
    // next day. Every failure is logged, but only the first of a run is reported, so a sweep that cannot
    // list (missing permission, a bucket too slow to list) does not file an issue every pass.
    private async Task SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var swept = await sweep.SweepAsync(now, ct);
            _nextSweep = now + SweepInterval;
            _sweepFailing = false;
            logger.LogInformation(
                "upload storage sweep listed={Listed} orphans={Orphans} queued={Queued} recent={Recent} unrecognised={Unrecognised}",
                swept.Listed, swept.Orphans, swept.Queued, swept.Recent, swept.Unrecognised);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "upload storage sweep failed; retrying next pass");
            if (!_sweepFailing)
            {
                selfReport.Report(ex);
            }
            _sweepFailing = true;
        }
    }

    /// <summary>One pass. Takes the instant, so a test can expire artifacts without waiting for them.</summary>
    public async Task RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var expired = await storage.ExpireAsync(now, ct);
        if (now >= _nextSweep)
        {
            await SweepAsync(now, ct);
        }
        var drained = await queue.DrainAsync(MaxPerPass, ct);
        if (expired + drained.Deleted > 0)
        {
            logger.LogInformation(
                "upload storage pass expired={Expired} deleted={Deleted}", expired, drained.Deleted);
        }
        if (drained.FirstFailure is { } failure)
        {
            logger.LogWarning(failure, "upload storage could not delete {Failed} queued keys", drained.Failed);
            selfReport.Report(failure);
        }
    }
}
