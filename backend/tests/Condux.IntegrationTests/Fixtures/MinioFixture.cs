using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Ephemeral MinIO (S3-compatible) container for exercising the real <c>S3ObjectStore</c> over the S3 API.
/// The source-map bucket is pre-created here because the store does not manage buckets (prod provisions the
/// BYO bucket; dev compose creates it via minio-init). Uses the generic container builder, like the
/// ClickHouse fixture, and the same pinned quay.io image the compose stack uses. Quay rather than Docker
/// Hub because the Hub registry stopped serving this image to anonymous pulls, and pinned rather than
/// floating because a moving tag on a third-party image turns their publishing decisions into our
/// outage. The compose file carries the detail; keep the two references the same.
/// </summary>
public sealed class MinioFixture : IAsyncLifetime
{
    private const string User = "condux";
    private const string Password = "conduxsecret";
    public const string Bucket = "condux-sourcemaps";

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z")
        .WithEnvironment("MINIO_ROOT_USER", User)
        .WithEnvironment("MINIO_ROOT_PASSWORD", Password)
        .WithCommand("server", "/data")
        .WithPortBinding(9000, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/minio/health/live")))
        .Build();

    public string Endpoint => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(9000)}";
    public string AccessKey => User;
    public string SecretKey => Password;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        using var s3 = new AmazonS3Client(
            User, Password, new AmazonS3Config { ServiceURL = Endpoint, ForcePathStyle = true });
        await s3.PutBucketAsync(Bucket);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
