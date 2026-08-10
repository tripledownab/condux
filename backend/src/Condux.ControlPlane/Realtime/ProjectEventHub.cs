using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Condux.ControlPlane.Realtime;

/// <summary>In-process fan-out of "this project changed" pings to connected SSE clients (ADR-0030). The
/// single Postgres LISTEN connection (<see cref="ProjectEventListener"/>) calls <see cref="Publish"/>;
/// each open SSE request holds a <see cref="Subscription"/> and reads pings for its one project. The
/// per-subscriber channel is bounded to one slot that drops extra writes, so a ping only ever means
/// "something changed, refetch" (they coalesce) and a stalled client can neither grow memory nor block
/// the publisher.</summary>
public sealed class ProjectEventHub
{
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<Guid, Channel<byte>>> subscribers = new();

    /// <summary>Register interest in a project's pings. Dispose the result when the SSE request ends.</summary>
    public Subscription Subscribe(long projectId)
    {
        var channel = Channel.CreateBounded<byte>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var perProject = subscribers.GetOrAdd(projectId, _ => new ConcurrentDictionary<Guid, Channel<byte>>());
        var id = Guid.NewGuid();
        perProject[id] = channel;
        return new Subscription(this, projectId, id, channel.Reader);
    }

    /// <summary>Nudge every client currently watching this project.</summary>
    public void Publish(long projectId)
    {
        if (subscribers.TryGetValue(projectId, out var perProject))
        {
            foreach (var channel in perProject.Values)
            {
                channel.Writer.TryWrite(1);
            }
        }
    }

    private void Remove(long projectId, Guid id)
    {
        if (subscribers.TryGetValue(projectId, out var perProject))
        {
            perProject.TryRemove(id, out _);
            if (perProject.IsEmpty)
            {
                subscribers.TryRemove(projectId, out _);
            }
        }
    }

    public sealed class Subscription(ProjectEventHub hub, long projectId, Guid id, ChannelReader<byte> reader)
        : IDisposable
    {
        public ChannelReader<byte> Reader => reader;

        public void Dispose() => hub.Remove(projectId, id);
    }
}
