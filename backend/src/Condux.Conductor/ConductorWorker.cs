using System.Text.Json;
using Condux.Core.FixEngine;
using Condux.Storage.Postgres;
using Condux.Telemetry;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Condux.Conductor;

/// <summary>
/// The Conductor worker: drains the fix-request topic and runs each job through the
/// <see cref="FixOrchestrator"/> (create suggestion → provider → draft PR + audit). Requests are
/// published by the control-plane's RequestFix endpoint (a later slice); until then the topic is
/// simply idle. Resilient — a transient error is logged and retried, never crashing the worker.
/// </summary>
public sealed class ConductorWorker(
    IConfiguration config, ILogger<ConductorWorker> logger, FixOrchestrator orchestrator,
    IssueRepository issues, ProjectEventNotifier projectEvents, ConduxSelfReporter selfReport)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => RunAsync(stoppingToken), stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var bootstrap = config["CONDUX_KAFKA_BOOTSTRAP"] ?? "localhost:9092";
        var topic = config["CONDUX_FIX_TOPIC"] ?? "fix-requests";

        await EnsureTopicAsync(bootstrap, topic, stoppingToken);

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = "condux-conductor",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();

        consumer.Subscribe(topic);
        logger.LogInformation("conductor subscribed bootstrap={Bootstrap} topic={Topic}", bootstrap, topic);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(TimeSpan.FromSeconds(1));
                if (result?.Message?.Value is { } value && JsonSerializer.Deserialize<FixJob>(value) is { } job)
                {
                    var suggestion = await orchestrator.RunAsync(job, stoppingToken);
                    logger.LogInformation(
                        "fix run issue={IssueId} id={FixId} status={Status} pr={PrUrl}",
                        job.IssueId, suggestion.Id, suggestion.Status, suggestion.PrUrl);
                    await NotifyProjectAsync(job.IssueId, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "conductor iteration error");
                selfReport.Report(ex); // Condux on Condux (#75)
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        consumer.Close();
    }

    // A concluded run happened outside any browser, so the dashboard learns of it only through this
    // nudge (ADR-0030). The job carries the issue's internal id, not its project, so resolve it first —
    // all best-effort, because the run itself is already recorded.
    private async Task NotifyProjectAsync(long issueId, CancellationToken cancellationToken)
    {
        try
        {
            var summaries = await issues.SummariesByInternalIdsAsync([issueId], cancellationToken);
            if (summaries.TryGetValue(issueId, out var summary))
            {
                await ProjectEventNudge.TrySendAsync(
                    projectEvents, logger, summary.ProjectId, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "project lookup for nudge failed issue={IssueId}", issueId);
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
                logger.LogWarning("ensure topic attempt {Attempt} failed: {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }
}
