using Condux.Core.SourceMaps;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>An object store holding one source map under the given keys, and nothing else, for the tests
/// that need symbolication to find a map without an S3 server behind it.</summary>
public sealed class InMemoryMapStore(byte[] bytes, params string[] keys) : IObjectStore
{
    public Task PutAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<byte[]?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<byte[]?>(keys.Contains(key) ? bytes : null);
}
