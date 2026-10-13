extern alias conductor;

using System.Reflection;
using Condux.IntegrationTests.Fixtures;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Xunit;
using ConsumerPolling = conductor::Condux.Conductor.ConsumerPolling;

namespace Condux.IntegrationTests;

/// <summary>
/// Drives <see cref="ConsumerPolling"/> against a real broker with the poll interval shortened to the
/// broker's minimum session timeout, so a job a few seconds long stands in for a fix run of many
/// minutes. The inline case is the control: it shows the job length used here does break the group,
/// so the helper's case passing means the helper kept it there.
/// </summary>
[Trait("Category", "Integration")]
public class ConsumerPollingTest(RedpandaFixture redpanda) : IClassFixture<RedpandaFixture>
{
    // Redpanda's default group_min_session_timeout_ms is 6000, and librdkafka refuses a poll
    // interval below the session timeout.
    private const int PollIntervalMs = 6000;
    private static readonly TimeSpan JobLength = TimeSpan.FromMilliseconds(PollIntervalMs * 1.5);

    [Fact]
    public async Task A_job_longer_than_the_poll_interval_keeps_the_consumer_in_its_group()
    {
        var topic = await ProduceAsync("first", "second");
        using var consumer = Subscribe(topic);
        var first = ConsumeWithin(consumer, TimeSpan.FromSeconds(30));
        Assert.Equal("first", first?.Message.Value);

        var counting = CountingConsumer.Wrap(consumer);
        var done = await ConsumerPolling.RunWhilePollingAsync(
            counting, async () => { await Task.Delay(JobLength); return true; }, TimeSpan.FromMilliseconds(500));

        Assert.True(done);
        // "second" waited the whole job. Paused, it stays on the broker instead of being fetched and
        // handed back on every poll.
        Assert.Equal(0, CountingConsumer.ReturnedBy(counting));
        // The commit covers the taken job alone.
        consumer.Commit();
        var committed = consumer.Committed([first!.TopicPartition], TimeSpan.FromSeconds(10));
        Assert.Equal(first.Offset + 1, committed.Single().Offset);

        var second = ConsumeWithin(consumer, TimeSpan.FromSeconds(10));
        Assert.Equal("second", second?.Message.Value);
        consumer.Close();
    }

    [Fact]
    public async Task A_message_from_a_partition_assigned_during_the_job_is_delivered_after_it()
    {
        var topic = "polling-" + Guid.NewGuid();
        using (var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = redpanda.BootstrapAddress }).Build())
        {
            await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 2, ReplicationFactor = 1 }]);
        }

        // Automatic commit stays on here, as in the workers: it is what commits the taken job's offset
        // when the rebalance revokes its partition, so the job is not delivered a second time.
        var group = "test-" + Guid.NewGuid();
        using var consumer = Subscribe(topic, group, autoCommit: true);
        using var leaver = Subscribe(topic, group, autoCommit: true);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (consumer.Assignment.Count != 1 || leaver.Assignment.Count != 1)
        {
            Assert.True(DateTime.UtcNow < deadline, "the group never split the two partitions");
            consumer.Consume(TimeSpan.FromMilliseconds(200));
            leaver.Consume(TimeSpan.FromMilliseconds(200));
        }

        var own = consumer.Assignment.Single().Partition;
        var other = leaver.Assignment.Single().Partition;
        await ProduceAsync(topic, own, "own-0", "own-1");
        await ProduceAsync(topic, other, "other-0", "other-1");

        var taken = ConsumeWithin(consumer, TimeSpan.FromSeconds(10));
        Assert.Equal("own-0", taken?.Message.Value);

        // The other member leaves mid-job, so the group hands its partition to this consumer, unpaused,
        // and messages from it can be returned while the job still runs.
        var counting = CountingConsumer.Wrap(consumer);
        await ConsumerPolling.RunWhilePollingAsync(
            counting,
            async () =>
            {
                leaver.Close();
                await Task.Delay(JobLength);
                return true;
            },
            TimeSpan.FromMilliseconds(500));

        // Each partition assigned during the job hands back one message and is then paused, not one
        // message per poll.
        Assert.InRange(CountingConsumer.ReturnedBy(counting), 1, 2);

        var rest = new List<string>();
        while (rest.Count < 3 && ConsumeWithin(consumer, TimeSpan.FromSeconds(10)) is { } next)
        {
            rest.Add(next.Message.Value);
        }

        Assert.Equal(["other-0", "other-1", "own-1"], rest.Order());
        consumer.Close();
    }

    [Fact]
    public async Task The_same_job_run_inline_loses_the_group()
    {
        var topic = await ProduceAsync("first", "second");
        using var consumer = Subscribe(topic);
        var first = ConsumeWithin(consumer, TimeSpan.FromSeconds(30));
        Assert.NotNull(first);

        await Task.Delay(JobLength);

        var error = Assert.Throws<ConsumeException>(() => consumer.Consume(TimeSpan.FromSeconds(5)));
        Assert.Equal(ErrorCode.Local_MaxPollExceeded, error.Error.Code);
        consumer.Close();
    }

    private async Task<string> ProduceAsync(params string[] values)
    {
        var topic = "polling-" + Guid.NewGuid();
        await ProduceAsync(topic, Partition.Any, values);
        return topic;
    }

    private async Task ProduceAsync(string topic, Partition partition, params string[] values)
    {
        using var producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = redpanda.BootstrapAddress }).Build();
        foreach (var value in values)
        {
            await producer.ProduceAsync(
                new TopicPartition(topic, partition), new Message<string, string> { Key = "k", Value = value });
        }
    }

    // Automatic commit is off unless asked for, so a test can commit by hand and read the result: the
    // five-second automatic commit would otherwise commit first and leave the explicit call nothing.
    private IConsumer<string, string> Subscribe(string topic, string? group = null, bool autoCommit = false)
    {
        var config = ConsumerPolling.CreateConfig(redpanda.BootstrapAddress, group ?? "test-" + Guid.NewGuid());
        config.SessionTimeoutMs = PollIntervalMs;
        config.MaxPollIntervalMs = PollIntervalMs;
        config.EnableAutoCommit = autoCommit;
        var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topic);
        return consumer;
    }

    // Takes messages the way the workers do. Early polls return null while the group is still
    // assigning partitions.
    private static ConsumeResult<string, string>? ConsumeWithin(IConsumer<string, string> consumer, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (ConsumerPolling.ConsumeAndStore(consumer, TimeSpan.FromSeconds(1)) is { } result)
            {
                return result;
            }
        }

        return null;
    }

    // Forwards every call to a real consumer and counts the messages Consume returns.
    public class CountingConsumer : DispatchProxy
    {
        private IConsumer<string, string> inner = null!;
        private int returned;

        public static IConsumer<string, string> Wrap(IConsumer<string, string> inner)
        {
            var proxy = Create<IConsumer<string, string>, CountingConsumer>();
            ((CountingConsumer)(object)proxy).inner = inner;
            return proxy;
        }

        public static int ReturnedBy(IConsumer<string, string> proxy) => ((CountingConsumer)(object)proxy).returned;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var result = method!.Invoke(inner, args);
            if (method.Name == nameof(IConsumer<string, string>.Consume) && result is not null)
            {
                returned++;
            }

            return result;
        }
    }
}
