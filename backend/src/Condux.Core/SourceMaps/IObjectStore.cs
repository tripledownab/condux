namespace Condux.Core.SourceMaps;

/// <summary>
/// A minimal object-storage seam (ADR-0028): store and fetch opaque blobs by key. Backed by S3/MinIO in
/// the running system (<c>S3ObjectStore</c> in Condux.Storage) and a fake in tests. Deliberately small,
/// only what the source-map store needs; source maps are large binary artifacts that belong in object
/// storage, not Postgres/ClickHouse.
/// </summary>
public interface IObjectStore
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>The stored bytes, or null when no object exists at that key.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default);
}
