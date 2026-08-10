using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.Core.Messaging;
using Condux.Core.Plans;
using Condux.Core.Sampling;
using Condux.Notifications;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Condux.Telemetry;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Condux.Consumer;

/// <summary>
/// The ingest consumer: drains the Redpanda events topic, fingerprints each event,
/// upserts the grouped issue in Postgres, and batch-inserts the raw event into
/// ClickHouse. On a new or regressed issue it also fires the project's alert rules
/// (best-effort, via <see cref="AlertDispatcher"/>). Resilient — a transient
/// consume/store error is logged and retried, never crashing the worker.
/// </summary>
public sealed class ConsumerWorker(
    IConfiguration config, ILogger<ConsumerWorker> logger, IHttpClientFactory httpClientFactory,
    AlertDispatcher alertDispatcher, AutoFixDispatcher autoFixDispatcher, ConduxSelfReporter selfReport)
    : BackgroundService
{
    private const int BatchSize = 100;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => RunAsync(stoppingToken), stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var bootstrap = config["CONDUX_KAFKA_BOOTSTRAP"] ?? "localhost:9092";
        var topic = config["CONDUX_KAFKA_TOPIC"] ?? "events";
        // Datastore connections come from the environment — never hardcode credentials, even for dev
        // (dev values are supplied by docker-compose). Missing config fails fast with a clear message.
        var pgConn = config.Require("CONDUX_POSTGRES");

        // Per-issue storage sampling: always keep the first N raw events of every issue,
        // then 1-in-M of the repeats. The issue counter (Postgres) still counts every event —
        // only redundant raw-event storage (ClickHouse) is shed. Set SAMPLE_EVERY<=1 to disable.
        var keepFirst = long.TryParse(config["CONDUX_SAMPLE_KEEP_FIRST"], out var kf) ? kf : 50;
        var sampleEvery = long.TryParse(config["CONDUX_SAMPLE_EVERY"], out var se) ? se : 20;
        var sampler = new IssueSampler(keepFirst, sampleEvery);

        await EnsureTopicAsync(bootstrap, topic, stoppingToken);

        var issues = new IssueRepository(pgConn);
        // Live dashboard badges (ADR-0030): nudges a project's SSE stream on a new/regressed issue.
        var projectEvents = new ProjectEventNotifier(pgConn);
        // Resilient ClickHouse client from the factory (retry/timeout/circuit-breaker configured in Program).
        var eventWriter = new ClickHouseEventWriter(httpClientFactory.CreateClient(ClickHouseRegistration.ClientName));
        var statsWriter = new ClickHouseIssueStatsWriter(
            httpClientFactory.CreateClient(ClickHouseRegistration.ClientName));

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = "condux-consumer",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();

        consumer.Subscribe(topic);
        logger.LogInformation(
            "consumer subscribed bootstrap={Bootstrap} topic={Topic} sample=keepFirst:{KeepFirst}/every:{SampleEvery}",
            bootstrap, topic, sampler.KeepFirst, sampler.SampleEvery);

        var buffer = new List<EventRow>();
        var statsBuffer = new List<IssueStatsRow>();
        var sampledOut = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(TimeSpan.FromSeconds(1));
                if (result?.Message?.Value is { } value && JsonSerializer.Deserialize<Event>(value) is { } e)
                {
                    // The message key is the numeric project id (set by the relay from the DSN-authed
                    // project). issues.project_id is a bigint FK to projects, so a non-numeric key can't
                    // be stored — skip it. ClickHouse keeps the key as a string (its sort key).
                    var projectKey = result.Message.Key;
                    if (long.TryParse(projectKey, NumberStyles.None, CultureInfo.InvariantCulture, out var projectId))
                    {
                        var grouping = Fingerprinter.Compute(e);
                        var seenAt = e.TimestampUnixMs > 0
                            ? DateTimeOffset.FromUnixTimeMilliseconds(e.TimestampUnixMs)
                            : DateTimeOffset.UtcNow;

                        var upsert = await issues.UpsertAsync(projectId, grouping, e.Level, seenAt, e.Release, stoppingToken);
                        await FireAlertsAsync(projectId, upsert, grouping, e.Level, stoppingToken);
                        // Auto-fix (#101): in an auto-mode org, a new/regressed error-or-worse issue
                        // publishes a fix job (quota-gated, best-effort). The event carries the context.
                        await autoFixDispatcher.DispatchAsync(projectId, upsert, e, stoppingToken);
                        // Live dashboard badges (ADR-0030): a new or regressed issue nudges the project's
                        // SSE stream so every viewer's "new issues" badge refetches. Best-effort — a notify
                        // failure never interrupts the drain (the badge also polls as a safety net).
                        if (upsert.Occurrence == 1 || upsert.Reopened)
                        {
                            try
                            {
                                await projectEvents.NotifyAsync(projectId, stoppingToken);
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "project-event notify failed for project {ProjectId}", projectId);
                            }
                        }
                        // Count EVERY event in the hourly rollup (exact time series, #102); only the raw
                        // payload row below is subject to sampling.
                        statsBuffer.Add(ClickHouseIssueStatsWriter.ToRow(projectKey, (ulong)upsert.Id, seenAt));
                        if (sampler.ShouldStore(upsert.Occurrence))
                        {
                            var retentionDays = ReadRetentionDays(result.Message.Headers);
                            buffer.Add(ClickHouseEventWriter.ToRow(
                                projectKey, (ulong)upsert.Id, e, grouping.Fingerprint, retentionDays));
                        }
                        else
                        {
                            sampledOut++;
                        }
                    }
                    else
                    {
                        logger.LogWarning("skipping event with non-numeric project key {Key}", projectKey);
                    }
                }

                // Flush on a full batch, or when the topic is idle (result == null on timeout). Stats
                // rows flush with the same cadence (they accumulate faster — one per event, unsampled).
                if (statsBuffer.Count >= BatchSize || (statsBuffer.Count > 0 && result is null))
                {
                    await statsWriter.InsertAsync(statsBuffer, stoppingToken);
                    await eventWriter.InsertAsync(buffer, stoppingToken);
                    logger.LogInformation(
                        "flushed {Stats} stat rows + {Count} events to ClickHouse (sampled out {Sampled})",
                        statsBuffer.Count, buffer.Count, sampledOut);
                    statsBuffer.Clear();
                    buffer.Clear();
                    sampledOut = 0;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "consumer iteration error");
                selfReport.Report(ex); // Condux on Condux (#75): report our own worker errors, best-effort
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

    // The relay stamps the project's plan-tier retention as a Kafka header. Fall back to the single
    // PlanCatalog default when it is absent (an older message) or malformed, so an event is never written
    // with a zero/invalid TTL.
    private static int ReadRetentionDays(Headers headers)
    {
        if (headers.TryGetLastBytes(EventHeaders.RetentionDays, out var bytes)
            && int.TryParse(Encoding.UTF8.GetString(bytes), NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            && days > 0)
        {
            return days;
        }
        return PlanCatalog.DefaultRetentionDays;
    }

    // Fire the project's alert rules when this event created a new issue (occurrence 1) or reopened a
    // resolved one (a regression). The caller already parsed the numeric project id. Dispatch is
    // best-effort and swallows its own errors; nothing here can disrupt ingest.
    private async Task FireAlertsAsync(
        long projectId, UpsertResult upsert, Grouping grouping, Level level, CancellationToken cancellationToken)
    {
        if (AlertNotificationFactory.Build(projectId, upsert, grouping, level) is not { } notification)
        {
            return;
        }

        await alertDispatcher.DispatchAsync(projectId, notification.EventType, notification, cancellationToken);
    }

    // Create the topic up front (with retry until the broker is reachable) so the
    // consumer never starts against a missing topic.
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
