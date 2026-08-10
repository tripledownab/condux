using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Condux.Core.SourceMaps;

namespace Condux.Storage.ObjectStore;

/// <summary>
/// <see cref="IObjectStore"/> over the S3 API via the AWS SDK (ADR-0028). Works against real AWS S3 and
/// dev MinIO (a custom service URL + path-style addressing). The AWS SDK handles SigV4 request signing,
/// which we deliberately do not hand-roll (ADR-0008 prefer-vetted-packages). The bucket is expected to
/// exist (prod provisions it; dev compose creates it), so this does not manage buckets.
/// </summary>
public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly IAmazonS3 client;
    private readonly string bucket;
    private readonly ServerSideEncryptionMethod? serverSideEncryption;

    public S3ObjectStore(ObjectStoreConfig config)
    {
        bucket = config.Bucket!;
        serverSideEncryption = string.IsNullOrEmpty(config.ServerSideEncryption)
            ? null
            : ServerSideEncryptionMethod.FindValue(config.ServerSideEncryption);
        var s3Config = new AmazonS3Config();
        if (!string.IsNullOrEmpty(config.Endpoint))
        {
            s3Config.ServiceURL = config.Endpoint;
            s3Config.ForcePathStyle = true; // MinIO / custom endpoint
        }
        else if (!string.IsNullOrEmpty(config.Region))
        {
            s3Config.RegionEndpoint = RegionEndpoint.GetBySystemName(config.Region);
        }

        client = new AmazonS3Client(config.AccessKey, config.SecretKey, s3Config);
    }

    public async Task PutAsync(
        string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(content, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
        };
        if (serverSideEncryption is not null)
        {
            request.ServerSideEncryptionMethod = serverSideEncryption;
        }

        await client.PutObjectAsync(request, cancellationToken);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client.GetObjectAsync(bucket, key, cancellationToken);
            await using var body = response.ResponseStream;
            using var buffer = new MemoryStream();
            await body.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public void Dispose() => client.Dispose();
}
