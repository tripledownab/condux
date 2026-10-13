using System.Net;
using System.Net.Sockets;
using Condux.Core.FixEngine;
using Condux.IntegrationTests.Fixtures;
using Condux.Messaging;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// What <see cref="KafkaJobProducer"/> does with a job it cannot confirm, against a broker that is not there.
/// A cancelled wait leaves the message in the producer's queue, which is the state a shutdown finds.
/// </summary>
[Trait("Category", "Integration")]
public class KafkaJobProducerTest
{
    [Fact]
    public async Task Disposing_waits_for_a_queued_job_and_logs_one_it_could_not_confirm()
    {
        var logger = new RecordingLogger();
        var producer = new KafkaJobProducer(KafkaTestBrokers.Unreachable(), "fix-requests", logger);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => producer.ProduceAsync("42", "job", cancelled.Token));

        var started = System.Diagnostics.Stopwatch.StartNew();
        producer.Dispose();
        var waited = started.Elapsed;
        producer.Dispose();

        // The flush is bounded at 5s and the job cannot be delivered, so it waits the whole bound.
        Assert.True(waited >= TimeSpan.FromSeconds(4.5), $"Dispose returned after {waited}");
        Assert.Equal(
            ["1 job(s) for fix-requests were not confirmed delivered before shutdown. Their runs stay reserved"],
            logger.Errors);
    }

    // The client hands its raw handle to native code without checking it is open, so this has to be refused
    // here: the alternative is a produce against a destroyed handle, which is a lost job or a crash.
    [Fact]
    public async Task A_produce_after_disposal_is_refused()
    {
        var producer = new KafkaJobProducer(KafkaTestBrokers.Unreachable(), "fix-requests", new RecordingLogger());
        producer.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => producer.ProduceAsync("42", "job", CancellationToken.None));
    }
}

// Its own class because it waits out the producer's 10s delivery timeout.
[Trait("Category", "Integration")]
public class KafkaJobProducerCancelledWaitTest
{
    [Fact]
    public async Task A_job_whose_wait_was_cancelled_is_logged_once_when_it_fails()
    {
        var logger = new RecordingLogger();
        var producer = new KafkaJobProducer(KafkaTestBrokers.Unreachable(), "fix-requests", logger);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => producer.ProduceAsync("42", "job", cancelled.Token));

        await logger.FirstError.WaitAsync(TimeSpan.FromSeconds(30));
        producer.Dispose();

        Assert.Equal(
            ["A job for fix-requests whose wait was cancelled was not confirmed delivered. Its run stays reserved"],
            logger.Errors);
    }
}

// Typed for the publisher, which asks for its own logger; the producer takes any ILogger.
internal sealed class RecordingLogger : ILogger<KafkaFixRequestPublisher>
{
    private readonly TaskCompletionSource _firstError = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<string> Errors { get; } = [];

    public Task FirstError => _firstError.Task;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Error)
        {
            lock (Errors)
            {
                Errors.Add(formatter(state, exception));
            }
            _firstError.TrySetResult();
        }
    }
}

internal static class KafkaTestBrokers
{
    // A port nothing listens on, so a queued job can never be delivered.
    public static string Unreachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"127.0.0.1:{port}";
    }
}

// Delivery against a real broker, and a clean shutdown logs nothing. On its own this cannot prove the flush
// runs: the client often sends a queued message while its handle shuts down, which is why the flush has its
// own test above. Nor does it reliably catch a count that drops a thread-pool hop late, since that is a race.
[Trait("Category", "Integration")]
public class KafkaJobProducerFlushTest(RedpandaFixture redpanda) : IClassFixture<RedpandaFixture>
{
    [Fact]
    public async Task Disposing_the_publisher_delivers_a_job_still_queued()
    {
        var topic = "fix-requests-" + Guid.NewGuid().ToString("N");
        var job = new FixJob(42, "acme/app", "main", "requester", "model");

        // Created up front, so a job that never arrives fails the assertion rather than the subscribe.
        using (var admin = new AdminClientBuilder(
                   new AdminClientConfig { BootstrapServers = redpanda.BootstrapAddress }).Build())
        {
            await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 1 }]);
        }

        var logger = new RecordingLogger();
        using (var publisher = new KafkaFixRequestPublisher(redpanda.BootstrapAddress, logger, topic))
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => publisher.PublishAsync(job, cancelled.Token));
        }

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = redpanda.BootstrapAddress,
            GroupId = "test-" + Guid.NewGuid(),
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(topic);

        // Poll to a deadline: early polls can return null while the group is still being assigned.
        ConsumeResult<string, string>? result = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (result is null && DateTime.UtcNow < deadline)
        {
            result = consumer.Consume(TimeSpan.FromSeconds(1));
        }

        consumer.Close();
        Assert.Equal("42", result?.Message.Key);
        Assert.Empty(logger.Errors);
    }
}
