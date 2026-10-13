using System.Text.Json;
using Condux.Core.CveFix;
using Microsoft.Extensions.Logging;

namespace Condux.Messaging;

/// <summary>
/// Publishes CVE-bump requests to the Redpanda/Kafka topic the Conductor's CVE worker drains, keyed by
/// repo so a given repo's bumps keep partition order. A separate topic from the issue fix queue
/// (<see cref="KafkaFixRequestPublisher"/>), so the two run lists stay independent. The control-plane's
/// CVE-fix endpoint uses this.
/// </summary>
public sealed class KafkaCveFixPublisher(
    string bootstrapServers, ILogger<KafkaCveFixPublisher> logger, string topic = "cve-fix-requests")
    : ICveFixPublisher, IDisposable
{
    private readonly KafkaJobProducer _producer = new(bootstrapServers, topic, logger);

    public Task PublishAsync(CveFixJob job, CancellationToken cancellationToken = default) =>
        _producer.ProduceAsync(job.RepoLinkId.ToString(), JsonSerializer.Serialize(job), cancellationToken);

    public void Dispose() => _producer.Dispose();
}
