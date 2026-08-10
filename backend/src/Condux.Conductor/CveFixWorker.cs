using System.Text.Json;
using Condux.Core.CveFix;
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
    ConduxSelfReporter selfReport)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => RunAsync(stoppingToken), stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var bootstrap = config["CONDUX_KAFKA_BOOTSTRAP"] ?? "localhost:9092";
        var topic = config["CONDUX_CVE_FIX_TOPIC"] ?? "cve-fix-requests";

        await EnsureTopicAsync(bootstrap, topic, stoppingToken);

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = "condux-conductor-cve",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();

        consumer.Subscribe(topic);
        logger.LogInformation("conductor-cve subscribed bootstrap={Bootstrap} topic={Topic}", bootstrap, topic);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(TimeSpan.FromSeconds(1));
                if (result?.Message?.Value is { } value && JsonSerializer.Deserialize<CveFixJob>(value) is { } job)
                {
                    var run = await orchestrator.RunAsync(job, stoppingToken);
                    logger.LogInformation(
                        "cve fix run repo={RepoLinkId} ghsa={Ghsa} id={RunId} status={Status} pr={PrUrl}",
                        job.RepoLinkId, job.GhsaId, run.Id, run.Status, run.PrUrl);
                }
            }
            catch (OperationCanceledException)
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
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        consumer.Close();
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
