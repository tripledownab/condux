using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Ephemeral MinIO (S3-compatible) container for exercising the real <c>S3ObjectStore</c> over the S3 API.
/// The source-map bucket is pre-created here because the store does not manage buckets (prod provisions the
/// BYO bucket; dev compose creates it via minio-init). Uses the generic container builder, like the
/// ClickHouse fixture, and the <c>minio/minio:latest</c> image the compose stack uses.
/// </summary>
public sealed class MinioFixture : IAsyncLifetime
{
    private const string User = "condux";
    private const string Password = "conduxsecret";
    public const string Bucket = "condux-sourcemaps";

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("minio/minio:latest")
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
