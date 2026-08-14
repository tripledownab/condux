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

    /// <summary>
    /// Whether the broker answers. Asking the producer we already hold for cluster metadata, rather than
    /// standing up an admin client, so the probe exercises the same connection publishing uses: a
    /// reachable broker that this producer cannot talk to would otherwise still read as ready.
    ///
    /// The relay accepts events whether or not this is true, and a failure here means they are dropped
    /// after the customer's SDK was told they were accepted, which is the outage worth catching early.
    /// </summary>
    public bool CanReachBroker(TimeSpan timeout)
    {
        try
        {
            // A dependent admin client shares this producer's connection rather than opening its own.
            using var admin = new DependentAdminClientBuilder(_producer.Handle).Build();
            admin.GetMetadata(timeout);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose() => _producer.Dispose();
}
