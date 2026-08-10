namespace Condux.Core.FixEngine;

/// <summary>Publishes a fix request onto the queue the Conductor worker drains. Kafka-backed in
/// production; a fake captures the job in tests.</summary>
public interface IFixRequestPublisher
{
    Task PublishAsync(FixJob job, CancellationToken cancellationToken = default);
}
