using System.Net;
using System.Text;
using Condux.Core.Tickets;

namespace Condux.Linear.Tests;

/// <summary>Answers each request with the next scripted response for its path, and records what was sent.</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _script = [];

    public List<(string Path, string Body, string? Auth)> Sent { get; } = [];

    public ScriptedHandler On(string path, HttpStatusCode status, string body)
    {
        Enqueue(path, () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        return this;
    }

    public ScriptedHandler Throw(string path, Exception exception)
    {
        Enqueue(path, () => throw exception);
        return this;
    }

    public int Count(string path) => Sent.Count(s => s.Path == path);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        // Read now: the caller disposes the request and its content as soon as the call returns.
        Sent.Add((path, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct),
            request.Headers.Authorization?.Parameter));
        if (!_script.TryGetValue(path, out var queue) || queue.Count == 0)
        {
            throw new InvalidOperationException($"No scripted response left for {path}");
        }
        return queue.Dequeue()();
    }

    private void Enqueue(string path, Func<HttpResponseMessage> response)
    {
        if (!_script.TryGetValue(path, out var queue))
        {
            _script[path] = queue = new Queue<Func<HttpResponseMessage>>();
        }
        queue.Enqueue(response);
    }
}

/// <summary>An in memory connection store whose guards compare the stored refresh blob exactly, as the SQL does.</summary>
internal sealed class MemoryConnectionStore : ITrackerConnectionStore
{
    public TrackerConnection? Row { get; set; }

    /// <summary>Runs just before a guarded write, to let a test change the row underneath it.</summary>
    public Action? BeforeWrite { get; set; }

    public Task<TrackerConnection?> GetAsync(long orgId, TicketProvider provider, CancellationToken ct = default) =>
        Task.FromResult(Row is { } r && r.OrgId == orgId && r.Provider == provider ? r : null);

    public Task<bool> ReplaceTokensAsync(
        long orgId, TicketProvider provider, byte[] replacedRefreshSealed, byte[] accessSealed,
        byte[] refreshSealed, DateTimeOffset accessExpiresAt, CancellationToken ct = default)
    {
        BeforeWrite?.Invoke();
        if (Row is not { } r || !r.RefreshTokenSealed.SequenceEqual(replacedRefreshSealed))
        {
            return Task.FromResult(false);
        }
        Row = r with { AccessTokenSealed = accessSealed, RefreshTokenSealed = refreshSealed, AccessExpiresAt = accessExpiresAt };
        return Task.FromResult(true);
    }

    public Task<bool> MarkBrokenAsync(
        long orgId, TicketProvider provider, byte[] refusedRefreshSealed, CancellationToken ct = default)
    {
        BeforeWrite?.Invoke();
        if (Row is not { } r || !r.RefreshTokenSealed.SequenceEqual(refusedRefreshSealed))
        {
            return Task.FromResult(false);
        }
        Row = r with { BrokenAt = DateTimeOffset.UnixEpoch };
        return Task.FromResult(true);
    }
}
