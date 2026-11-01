using System.Text;
using Condux.Core.SourceMaps;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ObjectStore;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Exercises the real <c>S3ObjectStore</c> (AWSSDK.S3) against an S3-compatible container over the S3 API
/// (ADR-0028): a Put/Get round-trip returns the exact bytes, a re-Put of the same key overwrites (upload
/// idempotence), a slice of a larger buffer is stored as itself, a missing key returns null rather than
/// throwing, and a wrong secret is refused. This is
/// the coverage the endpoint test's in-memory fake cannot give: the actual S3 client, signing, and
/// not-found handling. The refusal case also holds the fixture to a server that checks signatures, since
/// every other case here passes against one that does not.
/// </summary>
[Trait("Category", "Integration")]
public sealed class S3ObjectStoreTest(S3Fixture s3) : IClassFixture<S3Fixture>
{
    private S3ObjectStore Store(string? secretKey = null) => new(new ObjectStoreConfig(
        s3.Endpoint, S3Fixture.Bucket, s3.AccessKey, secretKey ?? s3.SecretKey, Region: null));

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
    public async Task A_slice_of_a_larger_buffer_is_stored_as_itself()
    {
        // A universal debug file's architectures are stored as slices of the one body, so the bytes either
        // side of a slice must not reach the object.
        using var store = Store();
        var body = Encoding.UTF8.GetBytes("before|the slice|after");
        var slice = body.AsMemory(7, "the slice".Length);

        await store.PutAsync("debug-files/1/slice", slice, "application/octet-stream");

        Assert.Equal("the slice", Encoding.UTF8.GetString((await store.GetAsync("debug-files/1/slice"))!));
    }

    [Fact]
    public async Task Get_returns_null_for_a_missing_key()
    {
        using var store = Store();
        Assert.Null(await store.GetAsync("sourcemaps/1/by-debug-id/does-not-exist"));
    }

    [Fact]
    public async Task A_wrong_secret_is_refused()
    {
        using var store = Store(secretKey: "not-the-secret");
        await Assert.ThrowsAnyAsync<Exception>(
            () => store.PutAsync("sourcemaps/1/by-debug-id/forged", Encoding.UTF8.GetBytes("x"), "application/json"));
    }

    [Fact]
    public async Task Listing_and_prefix_delete_go_past_the_first_page()
    {
        // S3 answers a listing 1000 keys at a time, so one more than that is the smallest case that needs
        // the continuation token.
        using var store = Store();
        var prefix = $"debug-files/{Guid.NewGuid():N}/";
        var keys = Enumerable.Range(0, 1001).Select(i => $"{prefix}{i:D4}").ToList();
        await Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (key, ct) => await store.PutAsync(key, new byte[] { 1 }, "application/octet-stream", ct));

        var listed = new List<string>();
        var pages = 0;
        await foreach (var page in store.ListAsync(prefix))
        {
            pages++;
            listed.AddRange(page.Select(o => o.Key));
        }

        Assert.True(pages >= 2);
        Assert.Equal(keys, listed.Order(StringComparer.Ordinal));
        Assert.Equal(1001, await store.DeletePrefixAsync(prefix));
        Assert.Null(await store.GetAsync(keys[^1]));
    }

    [Fact]
    public async Task A_listed_object_says_when_it_was_written()
    {
        // The sweep judges age by this. The SDK parses the time as UTC, so the conversion is a no-op; on a
        // host whose clock is UTC this cannot tell a zone mistake apart, and pins only that the time is
        // there and current.
        using var store = Store();
        var key = $"sourcemaps/{Guid.NewGuid():N}/written";
        await store.PutAsync(key, new byte[] { 1 }, "application/json");

        var listed = new List<StoredObject>();
        await foreach (var page in store.ListAsync(key))
        {
            listed.AddRange(page);
        }

        var written = Assert.Single(listed).LastModified;
        Assert.NotNull(written);
        Assert.InRange(written.Value, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(1));
    }
}
