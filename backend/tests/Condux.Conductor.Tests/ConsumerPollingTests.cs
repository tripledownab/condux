using System.Reflection;
using Confluent.Kafka;
using Xunit;

namespace Condux.Conductor.Tests;

/// <summary>
/// What <see cref="ConsumerPolling"/> does when the broker client fails mid-job. The broker-backed
/// behaviour is in the integration suite's ConsumerPollingTest.
/// </summary>
public class ConsumerPollingTests
{
    [Fact]
    public async Task A_failing_poll_waits_for_the_job_before_it_rethrows()
    {
        var job = new TaskCompletionSource<bool>();
        // The helper runs synchronously up to its first incomplete await, so a version that rethrows
        // at once hands back a task that has already failed.
        var run = ConsumerPolling.RunWhilePollingAsync(
            FailingConsumer.Create(), () => job.Task, TimeSpan.FromMilliseconds(10));

        Assert.False(run.IsCompleted);

        job.SetResult(true);
        await Assert.ThrowsAsync<KafkaException>(() => run);
    }

    [Fact]
    public async Task Work_that_throws_before_its_first_await_still_resumes_the_partitions()
    {
        var consumer = FailingConsumer.Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConsumerPolling.RunWhilePollingAsync<bool>(
            consumer, () => throw new InvalidOperationException(), TimeSpan.FromMilliseconds(10)));

        Assert.Equal(1, FailingConsumer.ResumesOf(consumer));
    }

    // Holds no partitions, counts resumes and fails every Consume with a broker error. Any other call
    // is unexpected.
    public class FailingConsumer : DispatchProxy
    {
        private int resumes;

        public static IConsumer<string, string> Create() => Create<IConsumer<string, string>, FailingConsumer>();

        public static int ResumesOf(IConsumer<string, string> proxy) => ((FailingConsumer)(object)proxy).resumes;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_" + nameof(IConsumer<string, string>.Assignment):
                    return new List<TopicPartition>();
                case nameof(IConsumer<string, string>.Pause):
                    return null;
                case nameof(IConsumer<string, string>.Resume):
                    resumes++;
                    return null;
                case nameof(IConsumer<string, string>.Consume):
                    throw new KafkaException(ErrorCode.Local_Transport);
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }
}
