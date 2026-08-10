using System.Globalization;
using System.Text;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Messaging;
using Confluent.Kafka;

namespace Condux.Messaging;

/// <summary>
/// Publishes events to a Redpanda/Kafka topic, keyed by project id so a project's
/// events land on the same partition (ordering + locality for the consumer).
/// </summary>
public sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly string _topic;

    public KafkaEventPublisher(string bootstrapServers, string topic = "events")
    {
        _producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = bootstrapServers }).Build();
        _topic = topic;
    }

    public async Task PublishAsync(
        string projectId, Event e, int retentionDays, CancellationToken cancellationToken = default)
    {
        var value = JsonSerializer.Serialize(e);
        var message = new Message<string, string>
        {
            Key = projectId,
            Value = value,
            Headers =
            [
                new Header(
                    EventHeaders.RetentionDays,
                    Encoding.UTF8.GetBytes(retentionDays.ToString(CultureInfo.InvariantCulture))),
            ],
        };
        await _producer.ProduceAsync(_topic, message, cancellationToken);
    }

    public void Dispose() => _producer.Dispose();
}
