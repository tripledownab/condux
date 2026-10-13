using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Ephemeral S3-compatible server (RustFS) for exercising the real <c>S3ObjectStore</c> over the S3 API.
/// The source-map bucket is created here through the SDK because the store does not manage buckets (prod
/// provisions the BYO bucket; dev compose pre-creates it). Uses the generic container builder, like the
/// ClickHouse fixture, and the same pinned image the compose stack uses (pinned by S3FixtureImageTest).
/// Pinned rather than floating because a moving tag on a third-party image turns their publishing
/// decisions into our outage, and pulled from ghcr.io because Docker Hub rate-limits anonymous pulls.
/// RustFS replaced MinIO once no registry would serve MinIO to an anonymous pull, and it was chosen over
/// the lighter mocks because it verifies SigV4: a double that accepts any secret would let a signing
/// regression in <c>S3ObjectStore</c> pass.
/// </summary>
public sealed class S3Fixture : IAsyncLifetime
{
    private const string User = "condux";
    private const string Password = "conduxsecret";
    public const string Bucket = "condux-sourcemaps";
    public const string Image = "ghcr.io/rustfs/rustfs:1.0.0";

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage(Image)
        .WithEnvironment("RUSTFS_ACCESS_KEY", User)
        .WithEnvironment("RUSTFS_SECRET_KEY", Password)
        .WithPortBinding(9000, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/health/ready")))
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
