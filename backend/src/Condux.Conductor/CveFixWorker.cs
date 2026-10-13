using System.Text.Json;
using Condux.Core.CveFix;
using Condux.Storage.Postgres;
using Condux.Telemetry;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Condux.Conductor;

/// <summary>
/// The Conductor's CVE worker: drains the CVE-fix topic and runs each job through the
/// <see cref="CveFixOrchestrator"/> (record run → provider → draft bump PR). A sibling of
/// <see cref="ConductorWorker"/> on its own topic + consumer group, so the supply-chain path is
/// independent of the issue-fix path but reuses the same provider. Resilient — a transient error is
/// logged and retried, never crashing the worker.
/// </summary>
public sealed class CveFixWorker(
    IConfiguration config, ILogger<CveFixWorker> logger, CveFixOrchestrator orchestrator,
    RepoLinkRepository repoLinks, ProjectEventNotifier projectEvents, ConduxSelfReporter selfReport)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => RunAsync(stoppingToken), stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var bootstrap = config["CONDUX_KAFKA_BOOTSTRAP"] ?? "localhost:9092";
        var topic = config["CONDUX_CVE_FIX_TOPIC"] ?? "cve-fix-requests";

        await EnsureTopicAsync(bootstrap, topic, stoppingToken);

        using var consumer = new ConsumerBuilder<string, string>(
            ConsumerPolling.CreateConfig(bootstrap, "condux-conductor-cve")).Build();

        consumer.Subscribe(topic);
        logger.LogInformation("conductor-cve subscribed bootstrap={Bootstrap} topic={Topic}", bootstrap, topic);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = ConsumerPolling.ConsumeAndStore(consumer, TimeSpan.FromSeconds(1));
                if (result?.Message?.Value is { } value && JsonSerializer.Deserialize<CveFixJob>(value) is { } job)
                {
                    var outcome = await ConsumerPolling.RunWhilePollingAsync(
                        consumer, () => orchestrator.RunAsync(job, stoppingToken), TimeSpan.FromSeconds(1));
                    if (outcome is null)
                    {
                        // This job's run exists already or was ended before it started, or another run is in
                        // progress on the advisory and the store handed this one's reservation back.
                        logger.LogWarning(
                            "cve fix job not run: delivered again, its run already concluded or cancelled, or its advisory has a run in progress ghsa={Ghsa} job={JobId}",
                            job.GhsaId, job.JobId);
                        continue;
                    }
                    var (run, error) = outcome;
                    logger.LogInformation(
                        "cve fix run repo={RepoLinkId} ghsa={Ghsa} id={RunId} status={Status} pr={PrUrl}",
                        job.RepoLinkId, job.GhsaId, run.Id, run.Status, run.PrUrl);
                    if (error is not null)
                    {
                        // A bump has no audit trail, so this line is the only record of why it failed.
                        // Not self-reported: most failures are the model or GitHub refusing the run, and
                        // the log keeps the rest. The classification is this error's; a run the stale-run
                        // sweep concluded first keeps the reason the sweep stored.
                        logger.LogWarning(error, "cve fix run failed id={RunId} classifiedAs={Reason}",
                            run.Id, run.FailureReason);
                    }
                    await NotifyProjectAsync(job.RepoLinkId, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "conductor-cve iteration error");
                selfReport.Report(ex); // Condux on Condux (#75)
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        consumer.Close();
    }

    // A concluded bump happened outside any browser, so the dashboard learns of it only through this
    // nudge (ADR-0030). The job carries the repo link, not its project, so resolve it first — all
    // best-effort, because the run itself is already recorded.
    private async Task NotifyProjectAsync(Guid repoLinkId, CancellationToken cancellationToken)
    {
        try
        {
            if (await repoLinks.GetProjectIdAsync(repoLinkId, cancellationToken) is { } projectId)
            {
                await ProjectEventNudge.TrySendAsync(projectEvents, logger, projectId, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "project lookup for nudge failed repo={RepoLinkId}", repoLinkId);
        }
    }

    // Create the topic up front (with retry until the broker is reachable) so the consumer never
    // starts against a missing topic.
    private async Task EnsureTopicAsync(string bootstrap, string topic, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 15 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                using var admin = new AdminClientBuilder(
                    new AdminClientConfig { BootstrapServers = bootstrap }).Build();
                await admin.CreateTopicsAsync(
                    [new TopicSpecification { Name = topic, NumPartitions = 3, ReplicationFactor = 1 }]);
                logger.LogInformation("created topic {Topic}", topic);
                return;
            }
            catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
            {
                return; // already exists — fine
            }
            catch (Exception ex)
            {
                logger.LogWarning("ensure cve topic attempt {Attempt} failed: {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }
}
