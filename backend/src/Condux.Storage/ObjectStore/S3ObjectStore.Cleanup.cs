using System.Runtime.CompilerServices;
using Amazon.S3.Model;
using Condux.Core.SourceMaps;

namespace Condux.Storage.ObjectStore;

/// <summary>
/// The deleting half of the S3 store, used by the deletion queue and the orphan sweep. Objects under a
/// prefix are deleted one request each rather than in a multi-object delete, so this needs nothing from the
/// server beyond a listing and the single delete. The queue is not in a hurry.
/// </summary>
public sealed partial class S3ObjectStore : IObjectStoreCleanup
{
    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        await client.DeleteObjectAsync(bucket, key, cancellationToken);

    public async Task<int> DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        await foreach (var page in ListAsync(prefix, cancellationToken))
        {
            foreach (var stored in page)
            {
                await client.DeleteObjectAsync(bucket, stored.Key, cancellationToken);
                deleted++;
            }
        }
        return deleted;
    }

    public async IAsyncEnumerable<IReadOnlyList<StoredObject>> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await client.ListObjectsV2Async(request, cancellationToken);
            yield return (response.S3Objects ?? [])
                .Select(entry => new StoredObject(entry.Key, ToInstant(entry.LastModified)))
                .ToList();
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);
    }

    // The SDK hands back a DateTime whose kind it does not document; read anything but an explicit UTC as
    // local, which is what DateTime.ToUniversalTime does.
    private static DateTimeOffset? ToInstant(DateTime? lastModified) =>
        lastModified is { } value ? new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero) : null;
}
