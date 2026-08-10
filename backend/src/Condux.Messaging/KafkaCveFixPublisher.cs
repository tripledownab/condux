using System.Text.Json;
using Condux.Core.CveFix;
using Confluent.Kafka;

namespace Condux.Messaging;

/// <summary>
/// Publishes CVE-bump requests to the Redpanda/Kafka topic the Conductor's CVE worker drains, keyed by
/// repo so a given repo's bumps keep partition order. A separate topic from the issue fix queue
/// (<see cref="KafkaFixRequestPublisher"/>) — the two run lists stay independent. The control-plane's
/// CVE-fix endpoint uses this.
/// </summary>
public sealed class KafkaCveFixPublisher : ICveFixPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly string _topic;

    public KafkaCveFixPublisher(string bootstrapServers, string topic = "cve-fix-requests")
    {
        _producer = new ProducerBuilder<string, string>(
            new ProducerConfig
            {
                BootstrapServers = bootstrapServers,
                // The endpoint awaits this publish inside the HTTP request, so bound how long a produce
                // can block — an unreachable broker fails the request in seconds, not rdkafka's 5-min default.
                MessageTimeoutMs = 10_000,
            }).Build();
        _topic = topic;
    }

    public async Task PublishAsync(CveFixJob job, CancellationToken cancellationToken = default)
    {
        var value = JsonSerializer.Serialize(job);
        await _producer.ProduceAsync(
            _topic, new Message<string, string> { Key = job.RepoLinkId.ToString(), Value = value }, cancellationToken);
    }

    public void Dispose() => _producer.Dispose();
}
