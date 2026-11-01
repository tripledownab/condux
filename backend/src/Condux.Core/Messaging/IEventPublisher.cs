using Condux.Core.Events;

namespace Condux.Core.Messaging;

/// <summary>Kafka header names carried alongside a published event (policy/routing metadata, not event data).</summary>
public static class EventHeaders
{
    /// <summary>The project's plan-tier retention in days, stamped by the relay and read by the consumer.</summary>
    public const string RetentionDays = "retention-days";

    /// <summary>When the sender says it sent the event, in Unix milliseconds: a Sentry envelope's
    /// <c>sent_at</c>, read by the consumer to correct a sender's clock drift. Absent when it did not say.</summary>
    public const string SentAt = "sent-at";
}

/// <summary>
/// Publishes normalized events to the ingest buffer (Redpanda in prod). <paramref name="retentionDays"/> is
/// the project's plan-tier retention, carried so the consumer can stamp it on the ClickHouse row (whose
/// column-driven TTL then expires the event; see migration 0002). <paramref name="sentAtUnixMs"/> is when
/// the sender says it sent the event, 0 when it did not say.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(
        string projectId, Event e, int retentionDays, long sentAtUnixMs, CancellationToken cancellationToken = default);
}

/// <summary>Captures published events in memory — for unit tests and local dev.</summary>
public sealed class InMemoryEventPublisher : IEventPublisher
{
    private readonly List<(string ProjectId, Event Event, int RetentionDays, long SentAtUnixMs)> _published = [];

    public IReadOnlyList<(string ProjectId, Event Event, int RetentionDays, long SentAtUnixMs)> Published
    {
        get
        {
            lock (_published)
            {
                return _published.ToList();
            }
        }
    }

    public Task PublishAsync(
        string projectId, Event e, int retentionDays, long sentAtUnixMs, CancellationToken cancellationToken = default)
    {
        lock (_published)
        {
            _published.Add((projectId, e, retentionDays, sentAtUnixMs));
        }
        return Task.CompletedTask;
    }
}
