using Microsoft.Extensions.Configuration;

namespace Condux.Storage.ObjectStore;

/// <summary>
/// Object-storage configuration from env (<c>CONDUX_S3_*</c>), for the source-map artifact store
/// (ADR-0028). Opt-in: <see cref="Enabled"/> only when the bucket and credentials are set. A partial
/// config fails fast (a likely typo), matching the Stripe/GitHub configs. When unset the
/// <c>/api/sourcemaps</c> routes 404. <see cref="Endpoint"/> is set for a custom endpoint (dev MinIO,
/// path-style); left unset for real AWS S3, where <see cref="Region"/> selects the endpoint.
/// <see cref="ServerSideEncryption"/> (<c>CONDUX_S3_SSE</c>, e.g. "AES256") requests at-rest encryption
/// per object; leave it unset to rely on the bucket's default encryption (AWS S3 encrypts by default, and
/// dev MinIO has no KMS, so the default stays off there).
/// </summary>
public sealed record ObjectStoreConfig(
    string? Endpoint, string? Bucket, string? AccessKey, string? SecretKey, string? Region,
    string? ServerSideEncryption = null)
{
    public bool Enabled => !string.IsNullOrEmpty(Bucket) && !string.IsNullOrEmpty(AccessKey)
        && !string.IsNullOrEmpty(SecretKey);

    public static ObjectStoreConfig FromEnv(IConfiguration cfg)
    {
        var endpoint = cfg["CONDUX_S3_ENDPOINT"];
        var bucket = cfg["CONDUX_S3_BUCKET"];
        var accessKey = cfg["CONDUX_S3_ACCESS_KEY"];
        var secretKey = cfg["CONDUX_S3_SECRET_KEY"];
        var region = cfg["CONDUX_S3_REGION"];
        var sse = cfg["CONDUX_S3_SSE"];

        var anySet = new[] { endpoint, bucket, accessKey, secretKey, region }.Any(v => !string.IsNullOrEmpty(v));
        if (!anySet)
        {
            return new ObjectStoreConfig(null, null, null, null, null); // feature off
        }

        if (string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
        {
            throw new InvalidOperationException(
                "Object storage is partially configured. Set CONDUX_S3_BUCKET, CONDUX_S3_ACCESS_KEY, and "
                + "CONDUX_S3_SECRET_KEY (plus CONDUX_S3_ENDPOINT for a non-AWS endpoint like MinIO, or "
                + "CONDUX_S3_REGION for AWS), or unset them all.");
        }

        return new ObjectStoreConfig(endpoint, bucket, accessKey, secretKey, region, sse);
    }
}
