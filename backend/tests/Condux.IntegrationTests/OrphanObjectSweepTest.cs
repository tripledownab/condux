using Condux.Core.DebugFiles;
using Condux.Core.Plans;
using Condux.Core.SourceMaps;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ObjectStore;
using Condux.Storage.Postgres;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The sweep for objects no row names, against real Postgres and the real S3 container: an orphan under
/// either root is queued and then deleted by the drain, an object a row names is neither, an artifact
/// uploaded again between the sweep and the drain keeps its object, a key outside the layout is left
/// alone, an object not yet past every tier's retention, or with no time at all, is not judged, the worker
/// sweeps once a day, and a sweep that fails neither stops the drain nor waits a day. The bucket is this class's own, and its tests run one at a time, so every assertion here is
/// about keys the test itself wrote.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrphanObjectSweepTest(PostgresFixture pg, S3Fixture s3)
    : IClassFixture<PostgresFixture>, IClassFixture<S3Fixture>
{
    private S3ObjectStore Store() => new(new ObjectStoreConfig(
        s3.Endpoint, S3Fixture.Bucket, s3.AccessKey, s3.SecretKey, Region: null));

    // Past the default minimum age of everything a test writes, so the sweep judges it.
    private static readonly DateTimeOffset Later = DateTimeOffset.UtcNow.AddDays(365);

    private OrphanObjectSweep Sweep(IObjectStoreCleanup store) => new(pg.ConnectionString, store);

    [Theory]
    [InlineData(ProjectObjectKeys.SourceMaps)]
    [InlineData(ProjectObjectKeys.DebugFiles)]
    public async Task An_object_no_row_names_is_queued_and_then_deleted(string root)
    {
        using var store = Store();
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var orphan = ProjectObjectKeys.ToPrefix(root, projectId) + "orphan";
        var kept = ProjectObjectKeys.ToPrefix(root, projectId) + "kept";
        await store.PutAsync(orphan, new byte[] { 1 }, "application/octet-stream");
        await store.PutAsync(kept, new byte[] { 1 }, "application/octet-stream");
        await RecordAsync(root, projectId, kept);

        var result = await Sweep(store).SweepAsync(Later);

        Assert.True(result.Queued >= 1);
        Assert.True(await IsQueuedAsync(orphan));
        Assert.False(await IsQueuedAsync(kept));

        await new ObjectDeletionQueue(pg.ConnectionString, store).DrainAsync(10_000);
        Assert.Null(await store.GetAsync(orphan));
        Assert.NotNull(await store.GetAsync(kept));
    }

    [Fact]
    public async Task An_artifact_uploaded_again_after_the_sweep_keeps_its_object()
    {
        // The sweep found no row, then CI uploaded the same artifact: the drain's claim decides, not the
        // sweep, so the object stays.
        using var store = Store();
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var key = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.DebugFiles, projectId) + "uploaded-again";
        await store.PutAsync(key, new byte[] { 1 }, "application/octet-stream");

        await Sweep(store).SweepAsync(Later);
        Assert.True(await IsQueuedAsync(key));
        await RecordAsync(ProjectObjectKeys.DebugFiles, projectId, key);
        await store.PutAsync(key, new byte[] { 2 }, "application/octet-stream");
        await new ObjectDeletionQueue(pg.ConnectionString, store).DrainAsync(10_000);

        Assert.NotNull(await store.GetAsync(key));
        Assert.False(await IsQueuedAsync(key));
    }

    [Fact]
    public async Task A_key_outside_the_layout_is_counted_and_left_alone()
    {
        using var store = Store();
        var key = $"{ProjectObjectKeys.SourceMaps}not-a-project-{Guid.NewGuid():N}/x";
        await store.PutAsync(key, new byte[] { 1 }, "application/octet-stream");

        var result = await Sweep(store).SweepAsync(Later);

        Assert.True(result.Unrecognised >= 1);
        Assert.False(await IsQueuedAsync(key));
        Assert.NotNull(await store.GetAsync(key));
    }

    [Fact]
    public async Task One_worker_pass_sweeps_once_a_day_and_not_on_every_pass()
    {
        using var store = Store();
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var worker = new Condux.ControlPlane.Uploads.UploadStorageWorker(
            new UploadStorage(pg.ConnectionString), new ObjectDeletionQueue(pg.ConnectionString, store),
            Sweep(store),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Condux.ControlPlane.Uploads.UploadStorageWorker>.Instance,
            new Condux.Telemetry.ConduxSelfReporter(null));
        var now = Later;
        var first = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.SourceMaps, projectId) + "first";
        var second = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.SourceMaps, projectId) + "second";

        await store.PutAsync(first, new byte[] { 1 }, "application/json");
        await worker.RunOnceAsync(now, default);
        Assert.Null(await store.GetAsync(first));

        // Fifteen minutes on, inside the day: the next pass drains but does not sweep.
        await store.PutAsync(second, new byte[] { 1 }, "application/json");
        await worker.RunOnceAsync(now.AddMinutes(15), default);
        Assert.NotNull(await store.GetAsync(second));

        await worker.RunOnceAsync(now.AddDays(1), default);
        Assert.Null(await store.GetAsync(second));
    }

    [Fact]
    public async Task A_sweep_that_fails_still_lets_the_pass_drain_and_is_tried_again_next_pass()
    {
        using var store = Store();
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var queued = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.SourceMaps, projectId) + "queued";
        var orphan = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.SourceMaps, projectId) + "orphan";
        await store.PutAsync(queued, new byte[] { 1 }, "application/json");
        await store.PutAsync(orphan, new byte[] { 1 }, "application/json");
        await QueueAsync(queued);
        var listing = new FailingListing(store);
        var worker = new Condux.ControlPlane.Uploads.UploadStorageWorker(
            new UploadStorage(pg.ConnectionString), new ObjectDeletionQueue(pg.ConnectionString, store),
            Sweep(listing),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Condux.ControlPlane.Uploads.UploadStorageWorker>.Instance,
            new Condux.Telemetry.ConduxSelfReporter(null));
        var now = Later;

        await worker.RunOnceAsync(now, default);
        Assert.Null(await store.GetAsync(queued));
        Assert.NotNull(await store.GetAsync(orphan));

        // The listing works again, and the next pass, fifteen minutes on, sweeps rather than waiting a day.
        listing.Fails = false;
        await worker.RunOnceAsync(now.AddMinutes(15), default);
        Assert.Null(await store.GetAsync(orphan));
    }

    [Fact]
    public async Task An_object_not_yet_past_every_tiers_retention_is_left_alone()
    {
        using var store = Store();
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var key = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.DebugFiles, projectId) + "young";
        await store.PutAsync(key, new byte[] { 1 }, "application/octet-stream");

        // A day short of the longest retention plus its margin: still too recent to judge.
        var result = await Sweep(store).SweepAsync(DateTimeOffset.UtcNow.AddDays(
            Enum.GetValues<Tier>().Max(t => PlanCatalog.For(t).RetentionDays) + 13));

        Assert.True(result.Recent >= 1);
        Assert.False(await IsQueuedAsync(key));
    }

    [Fact]
    public async Task An_object_the_store_gives_no_time_for_is_treated_as_too_recent()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var key = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.SourceMaps, projectId) + "no-time";
        var listing = new Listing([[new StoredObject(key, LastModified: null)]]);

        var result = await new OrphanObjectSweep(pg.ConnectionString, listing).SweepAsync(Later);

        Assert.Equal(1, result.Recent);
        Assert.False(await IsQueuedAsync(key));
    }

    [Fact]
    public async Task Every_page_is_judged_and_every_orphan_queued_past_one_batch()
    {
        // Two listing pages and more orphans than one queueing batch, so a sweep that stopped at either
        // boundary would leave keys behind.
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var prefix = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.SourceMaps, projectId);
        var old = DateTimeOffset.UtcNow.AddDays(-400);
        var objects = Enumerable.Range(0, 1001).Select(i => new StoredObject($"{prefix}{i:D4}", old)).ToList();
        var listing = new Listing([objects[..1000], objects[1000..]]);

        try
        {
            var result = await new OrphanObjectSweep(pg.ConnectionString, listing).SweepAsync(DateTimeOffset.UtcNow);

            Assert.Equal(1001, result.Listed);
            Assert.Equal(1001, result.Queued);
            Assert.True(await IsQueuedAsync(objects[^1].Key));
        }
        finally
        {
            // These name no stored object, and left queued they would fill the window the worker tests'
            // drains take, which is oldest first, failing them too whenever this test fails.
            await using var conn = new NpgsqlConnection(pg.ConnectionString);
            await conn.OpenAsync();
            await using var forget = new NpgsqlCommand("DELETE FROM object_deletions WHERE object_key LIKE @p", conn);
            forget.Parameters.AddWithValue("p", prefix + "%");
            await forget.ExecuteNonQueryAsync();
        }
    }

    // Lists the given pages under the source-map root and nothing elsewhere; never deletes.
    private sealed class Listing(IReadOnlyList<IReadOnlyList<StoredObject>> pages) : IObjectStoreCleanup
    {
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the sweep never deletes");

        public Task<int> DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the sweep never deletes");

        public async IAsyncEnumerable<IReadOnlyList<StoredObject>> ListAsync(
            string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var page in prefix == ProjectObjectKeys.SourceMaps ? pages : [])
            {
                yield return page;
            }
        }
    }

    // Lists through the real store unless told to fail.
    private sealed class FailingListing(IObjectStoreCleanup inner) : IObjectStoreCleanup
    {
        public bool Fails { get; set; } = true;

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(key, cancellationToken);

        public Task<int> DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default) =>
            inner.DeletePrefixAsync(prefix, cancellationToken);

        public IAsyncEnumerable<IReadOnlyList<StoredObject>> ListAsync(
            string prefix, CancellationToken cancellationToken = default) =>
            Fails ? throw new HttpRequestException("storage unreachable") : inner.ListAsync(prefix, cancellationToken);
    }

    private async Task QueueAsync(string key)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("INSERT INTO object_deletions (object_key) VALUES (@k)", conn);
        cmd.Parameters.AddWithValue("k", key);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task RecordAsync(string root, long projectId, string key) => root == ProjectObjectKeys.DebugFiles
        ? new DebugFileRepository(pg.ConnectionString).RecordAsync(
            projectId, new DebugBuild(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString()), key, 1, sha256: null)
        : new SourceMapArtifactRepository(pg.ConnectionString)
            .RecordAsync(projectId, "1.0.0", null, Guid.NewGuid().ToString(), "app.js", key, 1);

    private async Task<bool> IsQueuedAsync(string key)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM object_deletions WHERE object_key = @k", conn);
        cmd.Parameters.AddWithValue("k", key);
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }
}
