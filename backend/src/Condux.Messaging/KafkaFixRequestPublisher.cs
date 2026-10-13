using System.Text.Json;
using Condux.Core.FixEngine;
using Microsoft.Extensions.Logging;

namespace Condux.Messaging;

/// <summary>
/// Publishes fix requests to the Redpanda/Kafka topic the Conductor worker drains, keyed by issue id
/// so a given issue's requests keep partition order. The control-plane's RequestFix endpoint and the
/// consumer's auto-fix dispatcher use this.
/// </summary>
public sealed class KafkaFixRequestPublisher(
    string bootstrapServers, ILogger<KafkaFixRequestPublisher> logger, string topic = "fix-requests")
    : IFixRequestPublisher, IDisposable
{
    private readonly KafkaJobProducer _producer = new(bootstrapServers, topic, logger);

    public Task PublishAsync(FixJob job, CancellationToken cancellationToken = default) =>
        _producer.ProduceAsync(job.IssueId.ToString(), JsonSerializer.Serialize(job), cancellationToken);

    public void Dispose() => _producer.Dispose();
}
