using Condux.Core.FixEngine;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>Records the fix jobs a request or the auto-fix dispatcher publishes, instead of sending them
/// to the broker, so a test can read what the Conductor would have been asked to do.</summary>
public sealed class CapturingFixPublisher : IFixRequestPublisher
{
    public List<FixJob> Jobs { get; } = [];

    /// <summary>The most recent job, or null when none was published.</summary>
    public FixJob? Job => Jobs.LastOrDefault();

    public Task PublishAsync(FixJob job, CancellationToken cancellationToken = default)
    {
        Jobs.Add(job);
        return Task.CompletedTask;
    }
}
