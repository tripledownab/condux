using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Condux.Messaging;

/// <summary>
/// The producer behind each publisher of a job that holds a reserved AI-fix run: the issue fix queue and the
/// CVE-bump queue. Disposing it flushes what is still queued, for a bounded time, and logs what it could not
/// confirm, because a job dropped at shutdown keeps the run it reserved.
/// </summary>
/// <remarks>A failed produce is passed through as it is, and its caller refunds the run. The client cannot
/// say whether a timed-out job was written: measured against Confluent.Kafka 2.5.3, a delivery timeout
/// reports the same persistence status whether the broker wrote the job or never received it.</remarks>
public sealed class KafkaJobProducer : IDisposable
{
    // Longer than a healthy broker needs by far. Each service's stop grace period has to cover this after
    // its own shutdown work, so raising it means raising those periods first. It bounds the wait for the
    // broker, not the delivery reports the flush runs meanwhile, so a log sink that blocks can stretch it.
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(5);

    private readonly IProducer<string, string> _producer;
    private readonly string _topic;
    private readonly ILogger _logger;
    // Held across the disposed check and the produce, and across setting the flag: the client passes its raw
    // handle to native code without checking it is open, so a produce that raced disposal would run against
    // a destroyed handle.
    private readonly Lock _gate = new();
    private bool _disposed;
    private int _pending;

    public KafkaJobProducer(string bootstrapServers, string topic, ILogger logger)
    {
        _producer = new ProducerBuilder<string, string>(
            new ProducerConfig
            {
                BootstrapServers = bootstrapServers,
                // Callers await this publish, inside an HTTP request or inside the consumer's drain. Bound how
                // long a produce can block so an unreachable broker fails the request in seconds rather than
                // hanging on rdkafka's 5-minute default (which left the dashboard stuck on "Requesting").
                MessageTimeoutMs = 10_000,
            }).Build();
        _topic = topic;
        _logger = logger;
    }

    /// <summary>Produces one job and waits for the broker's answer. Cancelling stops the wait, not the send,
    /// and the caller keeps the run reserved, so a job that then fails is logged here, where it is last seen.
    /// A job still unanswered when this producer is disposed never gets an answer, so wait with a token that
    /// shutdown cancels.</summary>
    public async Task ProduceAsync(string key, string value, CancellationToken cancellationToken)
    {
        var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = new WaitState();
        lock (_gate)
        {
            // Refused once disposal began: a job produced after the count was read would be counted nowhere.
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Increment(ref _pending);
            try
            {
                // The callback form, because librdkafka runs it before the flush counts the job as done. The
                // task-returning form completes a thread-pool hop later, after Flush may already have returned.
                _producer.Produce(
                    _topic, new Message<string, string> { Key = key, Value = value }, ToReportHandler(delivery, wait));
            }
            catch
            {
                // Refused before it was queued, so no report will follow.
                Interlocked.Decrement(ref _pending);
                throw;
            }
        }
        try
        {
            await delivery.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The answer may already be in, if the cancellation won the race inside WaitAsync. Then it is
            // the result: a failure goes to the caller, who refunds it like any other failed send, and a
            // success returns. Otherwise the report logs whatever comes, since nobody is waiting for it, which
            // includes a failure that lands in this same instant: that one is logged and its run kept.
            if (Interlocked.CompareExchange(ref wait.State, WaitState.Abandoned, WaitState.Waiting)
                == WaitState.Answered)
            {
                await delivery.Task;
                return;
            }
            throw;
        }
    }

    private Action<DeliveryReport<string, string>> ToReportHandler(TaskCompletionSource delivery, WaitState wait) =>
        report =>
        {
            try
            {
                if (report.Error.IsError)
                {
                    delivery.TrySetException(new ProduceException<string, string>(report.Error, report));
                }
                else
                {
                    delivery.TrySetResult();
                }
                // Logged here, inside the report, when the wait was already abandoned: a continuation could
                // lose the race with process exit, and this is the only record of a run kept and not run.
                if (Interlocked.Exchange(ref wait.State, WaitState.Answered) == WaitState.Abandoned)
                {
                    LogIfFailed(delivery.Task);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        };

    // Reading Exception also observes it, so an abandoned failure never surfaces as unobserved.
    private void LogIfFailed(Task delivery)
    {
        if (delivery.Exception is { } failure)
        {
            _logger.LogError(
                failure.InnerException,
                "A job for {Topic} whose wait was cancelled was not confirmed delivered. Its run stays reserved",
                _topic);
        }
    }

    private sealed class WaitState
    {
        public const int Waiting = 0, Abandoned = 1, Answered = 2;
        public int State;
    }

    // Producer.Dispose destroys the handle without flushing, so a job still queued at shutdown could be lost
    // while its reservation stayed taken. The jobs still unanswered after the flush are logged, since the
    // client's own warning goes to stderr only. They are counted here rather than by the flush's return,
    // which also counts client events that are not jobs, and the count drops inside each report, which the
    // flush waits for. The count is a snapshot: one still waiting for the broker's reply may be answered
    // before the handle closes, and may then be logged again as a failure. Any later call does nothing.
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        try
        {
            _producer.Flush(FlushTimeout);
            var undelivered = Volatile.Read(ref _pending);
            if (undelivered > 0)
            {
                _logger.LogError(
                    "{Count} job(s) for {Topic} were not confirmed delivered before shutdown. Their runs stay reserved",
                    undelivered, _topic);
            }
        }
        finally
        {
            _producer.Dispose();
        }
    }
}
