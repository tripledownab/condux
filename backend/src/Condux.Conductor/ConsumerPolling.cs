using Confluent.Kafka;

namespace Condux.Conductor;

/// <summary>
/// Keeps a Kafka consumer inside its group while a job it consumed runs for longer than
/// <c>max.poll.interval.ms</c> (librdkafka's default is five minutes). A fix run has no upper bound:
/// the anthropic provider makes calls of up to ten minutes each and the managed-agents provider waits
/// up to forty by default. Running one inline between two <c>Consume</c> calls made librdkafka declare
/// the consumer stuck and leave its group, and the worker's next <c>Consume</c> then threw. Raising the
/// interval would only move that cliff, so instead the partitions are paused and the consumer keeps
/// polling until the job finishes.
/// </summary>
public static class ConsumerPolling
{
    /// <summary>
    /// The consumer config both Conductor workers use. Offsets are stored by
    /// <see cref="ConsumeAndStore"/>, not by librdkafka on every message <c>Consume</c> returns: a
    /// message <see cref="RunWhilePollingAsync{T}"/> hands back unprocessed must not have its offset
    /// queued for the next commit.
    /// </summary>
    public static ConsumerConfig CreateConfig(string bootstrap, string groupId) => new()
    {
        BootstrapServers = bootstrap,
        GroupId = groupId,
        AutoOffsetReset = AutoOffsetReset.Earliest,
        EnableAutoOffsetStore = false,
    };

    /// <summary>
    /// Takes the next message and stores its offset at once, which is when librdkafka's automatic
    /// store would have, so delivery stays at most once: a job whose run fails is not delivered again.
    /// </summary>
    public static ConsumeResult<string, string>? ConsumeAndStore(IConsumer<string, string> consumer, TimeSpan timeout)
    {
        var result = consumer.Consume(timeout);
        if (result is not null)
        {
            consumer.StoreOffset(result);
        }

        return result;
    }

    /// <summary>
    /// Runs <paramref name="work"/> with the assigned partitions paused, calling <c>Consume</c> every
    /// <paramref name="pollEvery"/> so the poll interval never lapses, then resumes them. The pause keeps
    /// waiting jobs on the broker instead of fetched and handed back on every poll. A message that
    /// arrives anyway comes from a partition assigned during the job: that partition is paused too and
    /// the consumer seeks back, so the message is delivered after the job, to this consumer or to
    /// whichever member then holds its partition. It takes no cancellation token of its own:
    /// <paramref name="work"/> carries one, and returning while the work still runs would leave it
    /// writing after the worker has stopped.
    /// </summary>
    public static async Task<T> RunWhilePollingAsync<T>(
        IConsumer<string, string> consumer, Func<Task<T>> work, TimeSpan pollEvery)
    {
        consumer.Pause(consumer.Assignment);
        try
        {
            var job = work();
            try
            {
                while (!job.IsCompleted)
                {
                    if (consumer.Consume(pollEvery) is { } early)
                    {
                        consumer.Pause([early.TopicPartition]);
                        consumer.Seek(early.TopicPartitionOffset);
                    }
                }
            }
            catch
            {
                // The job still runs. Returning now would let the worker take the next job beside it.
                await Task.WhenAny(job);
                throw;
            }

            return await job;
        }
        finally
        {
            // Every path out resumes, or the worker would never take another job.
            consumer.Resume(consumer.Assignment);
        }
    }
}
