using System.Globalization;
using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Condux.Conductor;

/// <summary>
/// The CVE bump half of the verification watcher (ADR-0051), run on <see cref="VerificationWorker"/>'s
/// timer: each merged, watching bump is read against the project's runtime dependency inventory and, once
/// the pure <see cref="CveFixVerification"/> policy concludes, its verdict and evidence are stored and the
/// project's open dashboards nudged. This class is only the plumbing.
/// </summary>
public sealed class CveFixVerifier(
    PostgresCveFixVerification verification, IServiceScopeFactory scopes, ProjectEventNotifier projectEvents,
    TimeProvider clock, ILogger<CveFixVerifier> logger)
{
    /// <summary>
    /// Concludes what it can of every watching bump. A bump whose read fails is logged and left watching
    /// for the next tick, without stopping the bumps after it.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var watching = await verification.ListWatchingAsync(ct);
        if (watching.Count == 0)
        {
            return;
        }

        // The inventory reader is a typed HttpClient (transient); a per-tick scope avoids holding a captive
        // handler in this singleton.
        using var scope = scopes.CreateScope();
        var inventory = scope.ServiceProvider.GetRequiredService<ClickHouseReleaseModuleReader>();
        var now = clock.GetUtcNow();
        foreach (var run in watching)
        {
            try
            {
                await ConcludeAsync(run, inventory, now, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "cve bump verification failed id={RunId}", run.RunId);
            }
        }
    }

    private async Task ConcludeAsync(
        WatchedCveFix run, ClickHouseReleaseModuleReader inventory, DateTimeOffset now, CancellationToken ct)
    {
        // Every sighting the verdict can use is after the merge, so that is all that is read, uncapped
        // below the reader's ceiling; reaching the ceiling is passed on, and a verdict on a partial read
        // never holds.
        var (sightings, complete) = await inventory.ReadSightingsSinceAsync(
            // ClickHouse keys the inventory on the numeric project id as a string.
            run.ProjectId.ToString(CultureInfo.InvariantCulture), run.Package, run.MergedAt, ct);
        var verdict = CveFixVerification.Evaluate(
            run.Ecosystem, run.Package, run.ToVersion, run.VulnerableRanges, run.MergedAt, now, sightings, complete);
        if (verdict.Status == VerifyStatus.Watching)
        {
            return;
        }

        var evidence = CveFixVerification.ToEvidence(verdict, run.ToVersion, run.VulnerableRanges, run.MergedAt, now, complete)
            .ToJson();
        if (!await verification.SetVerifyStatusAsync(run.RunId, verdict.Status, evidence, now, ct))
        {
            return;
        }

        logger.LogInformation(
            "cve bump verified id={RunId} status={Status} sightings={Sightings} complete={Complete}",
            run.RunId, verdict.Status, verdict.Sightings.Count, complete);
        await ProjectEventNudge.TrySendAsync(projectEvents, logger, run.ProjectId, ct);
    }
}
