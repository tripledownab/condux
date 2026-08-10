using System.Text.Json;
using Condux.Core.FixEngine;
using Confluent.Kafka;

namespace Condux.Messaging;

/// <summary>
/// Publishes fix requests to the Redpanda/Kafka topic the Conductor worker drains, keyed by issue id
/// so a given issue's requests keep partition order. The control-plane's RequestFix endpoint uses this.
/// </summary>
public sealed class KafkaFixRequestPublisher : IFixRequestPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly string _topic;

    public KafkaFixRequestPublisher(string bootstrapServers, string topic = "fix-requests")
    {
        _producer = new ProducerBuilder<string, string>(
            new ProducerConfig
            {
                BootstrapServers = bootstrapServers,
                // RequestFix awaits this publish inside the HTTP request. Bound how long a produce can
                // block so an unreachable broker fails the request in seconds rather than hanging on
                // rdkafka's 5-minute default (which left the dashboard stuck on "Requesting").
                MessageTimeoutMs = 10_000,
            }).Build();
        _topic = topic;
    }

    public async Task PublishAsync(FixJob job, CancellationToken cancellationToken = default)
    {
        var value = JsonSerializer.Serialize(job);
        await _producer.ProduceAsync(
            _topic, new Message<string, string> { Key = job.IssueId.ToString(), Value = value }, cancellationToken);
    }

    public void Dispose() => _producer.Dispose();
}
