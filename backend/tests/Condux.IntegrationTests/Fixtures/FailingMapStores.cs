using Condux.Core.SourceMaps;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>An object store that cannot be reached, for the tests that need symbolication to fail.</summary>
public sealed class UnreachableMapStore : IObjectStore
{
    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default) =>
        throw new HttpRequestException("object store unreachable");

    public Task<byte[]?> GetAsync(string key, CancellationToken ct = default) =>
        throw new HttpRequestException("object store unreachable");
}

/// <summary>An object store that never answers until the caller gives up, for the tests that bound the
/// wait for a map.</summary>
public sealed class HangingMapStore : IObjectStore
{
    /// <summary>Completes once a read has started waiting.</summary>
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default) =>
        Task.Delay(Timeout.Infinite, ct);

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        Reached.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return null;
    }
}
