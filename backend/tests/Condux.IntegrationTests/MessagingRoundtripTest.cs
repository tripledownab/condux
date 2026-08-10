using System.Text;
using Condux.Core.Events;
using Condux.Core.Messaging;
using Condux.Core.Plans;
using Condux.IntegrationTests.Fixtures;
using Condux.Messaging;
using Confluent.Kafka;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Verifies the publish→consume path over a real Redpanda broker (the compose/prod
/// default): the relay's <see cref="KafkaEventPublisher"/> serializes + keys the
/// event, and a consumer reads it back. Also guards the Confluent.Kafka 2.5.x pin —
/// 2.6.0's Fetch API v12 request breaks the consumer against Redpanda.
/// </summary>
[Trait("Category", "Integration")]
public class MessagingRoundtripTest(RedpandaFixture redpanda) : IClassFixture<RedpandaFixture>
{
    [Fact]
    public async Task PublishedEvent_IsConsumable()
    {
        const string topic = "events";
        using var publisher = new KafkaEventPublisher(redpanda.BootstrapAddress, topic);

        // ProduceAsync awaits delivery, so this also auto-creates the topic.
        var retentionDays = PlanCatalog.For(Tier.Free).RetentionDays;
        await publisher.PublishAsync("proj-1", new Event { EventId = "abc", Level = Level.Error }, retentionDays);

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = redpanda.BootstrapAddress,
            GroupId = "test-" + Guid.NewGuid(),
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(topic);

        // Poll to a deadline: early polls can return null while the consumer group is
        // still being assigned its partitions.
        ConsumeResult<string, string>? result = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            result = consumer.Consume(TimeSpan.FromSeconds(1));
            if (result is not null)
            {
                break;
            }
        }

        Assert.NotNull(result);
        Assert.Equal("proj-1", result!.Message.Key);
        Assert.Contains("\"EventId\":\"abc\"", result.Message.Value);

        // The relay's per-tier retention rides as a header the consumer reads back.
        Assert.True(result.Message.Headers.TryGetLastBytes(EventHeaders.RetentionDays, out var retentionHeader));
        Assert.Equal(retentionDays.ToString(), Encoding.UTF8.GetString(retentionHeader));

        consumer.Close();
    }
}
