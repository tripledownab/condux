using System.Text;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ObjectStore;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Exercises the real <c>S3ObjectStore</c> (AWSSDK.S3) against a MinIO container over the S3 API (ADR-0028):
/// a Put/Get round-trip returns the exact bytes, a re-Put of the same key overwrites (upload idempotence),
/// and a missing key returns null rather than throwing. This is the coverage the endpoint test's in-memory
/// fake cannot give — the actual S3 client, signing, and not-found handling.
/// </summary>
[Trait("Category", "Integration")]
public sealed class S3ObjectStoreTest(MinioFixture minio) : IClassFixture<MinioFixture>
{
    private S3ObjectStore Store() => new(new ObjectStoreConfig(
        minio.Endpoint, MinioFixture.Bucket, minio.AccessKey, minio.SecretKey, Region: null));

    [Fact]
    public async Task Put_then_Get_round_trips_the_exact_bytes()
    {
        using var store = Store();
        var bytes = Encoding.UTF8.GetBytes("{\"version\":3,\"sources\":[\"app.ts\"],\"mappings\":\"AAAA\"}");

        await store.PutAsync("sourcemaps/1/by-debug-id/abc123", bytes, "application/json");
        var read = await store.GetAsync("sourcemaps/1/by-debug-id/abc123");

        Assert.NotNull(read);
        Assert.Equal(bytes, read);
    }

    [Fact]
    public async Task Put_overwrites_the_same_key()
    {
        using var store = Store();
        const string key = "sourcemaps/1/by-release/v1/_/app.js";

        await store.PutAsync(key, Encoding.UTF8.GetBytes("first"), "application/json");
        await store.PutAsync(key, Encoding.UTF8.GetBytes("second"), "application/json");

        Assert.Equal("second", Encoding.UTF8.GetString((await store.GetAsync(key))!));
    }

    [Fact]
    public async Task Get_returns_null_for_a_missing_key()
    {
        using var store = Store();
        Assert.Null(await store.GetAsync("sourcemaps/1/by-debug-id/does-not-exist"));
    }
}
