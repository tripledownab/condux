namespace Condux.Core.SourceMaps;

/// <summary>
/// The deleting half of object storage, with the listing that finds what to delete, kept apart from
/// <see cref="IObjectStore"/> because only the deletion queue and the orphan sweep need it: the read and
/// upload paths never delete or list, and a fake store for them should not have to pretend to.
/// </summary>
public interface IObjectStoreCleanup
{
    /// <summary>Deletes one object. Deleting an object that is not there is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes every object under <paramref name="prefix"/> and returns how many it deleted.</summary>
    Task<int> DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>Every object under <paramref name="prefix"/>, a page at a time.</summary>
    IAsyncEnumerable<IReadOnlyList<StoredObject>> ListAsync(string prefix, CancellationToken cancellationToken = default);
}

/// <summary>One stored object: its key, and when it was last written, or null when the store did not say.</summary>
public sealed record StoredObject(string Key, DateTimeOffset? LastModified);
