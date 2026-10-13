using System.Text;
using Condux.Core.DebugFiles;
using Condux.Core.SourceMaps;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ObjectStore;
using Condux.Storage.Postgres;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The queue of objects to delete, drained against real Postgres and the real S3 container: a queued key's
/// object goes and the key leaves the queue, a key a row holds again is dropped rather than deleted, a
/// re-upload that lands while its key is being deleted keeps its object, and so does one a second drain
/// reaches after the first has dealt with its key, a prefix takes everything under it and nothing beside
/// it, a store that fails keeps the key queued, and a delete the store never answers is given up.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ObjectDeletionQueueTest(PostgresFixture pg, S3Fixture s3)
    : IClassFixture<PostgresFixture>, IClassFixture<S3Fixture>
{
    private S3ObjectStore Store() => new(new ObjectStoreConfig(
        s3.Endpoint, S3Fixture.Bucket, s3.AccessKey, s3.SecretKey, Region: null));

    [Fact]
    public async Task A_queued_object_is_deleted_and_leaves_the_queue()
    {
        using var store = Store();
        var key = $"sourcemaps/{Unique()}/by-debug-id/a";
        await store.PutAsync(key, Encoding.UTF8.GetBytes("map"), "application/json");
        await QueueAsync(key, isPrefix: false);

        await new ObjectDeletionQueue(pg.ConnectionString, store).DrainAsync(10_000);

        Assert.Null(await store.GetAsync(key));
        Assert.False(await IsQueuedAsync(key));
    }

    [Fact]
    public async Task A_key_a_row_holds_again_is_dropped_not_deleted()
    {
        // CI uploaded the same artifact after it was evicted, so the object is the new upload's.
        using var store = Store();
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var key = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.SourceMaps, projectId) + "by-debug-id/b";
        await store.PutAsync(key, Encoding.UTF8.GetBytes("map"), "application/json");
        await QueueAsync(key, isPrefix: false);
        await new SourceMapArtifactRepository(pg.ConnectionString)
            .RecordAsync(projectId, "1.0.0", null, "b", "app.js", key, 3);

        await new ObjectDeletionQueue(pg.ConnectionString, store).DrainAsync(10_000);

        Assert.NotNull(await store.GetAsync(key));
        Assert.False(await IsQueuedAsync(key));
    }

    [Theory]
    [InlineData(ProjectObjectKeys.SourceMaps)]
    [InlineData(ProjectObjectKeys.DebugFiles)]
    public async Task A_reupload_landing_while_its_key_is_being_deleted_keeps_its_object(string root)
    {
        // The interleaving that used to lose the object: the drain has decided to delete the key, then CI
        // uploads the same artifact again (row first, then object), then the drain's delete runs. The hook
        // below starts the re-upload at the last moment before the delete. If its row write gets through,
        // the race is open, so the hook writes the object before the delete, as a real upload would have
        // had time to. If the row write waits for the drain instead, the object is written after it.
        using var store = Store();
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var key = ProjectObjectKeys.ToPrefix(root, projectId) + "reuploaded";
        await QueueAsync(key, isPrefix: false);

        Task? reupload = null;
        var objectWritten = false;
        var hook = new BeforeDelete(store, key, async () =>
        {
            reupload = RecordAsync(root, projectId, key);
            if (await Task.WhenAny(reupload, WaitForLockWaiterAsync(key)) == reupload)
            {
                await reupload;
                await store.PutAsync(key, [1], "application/octet-stream");
                objectWritten = true;
            }
        });

        await new ObjectDeletionQueue(pg.ConnectionString, hook).DrainAsync(10_000);
        Assert.NotNull(reupload);
        await reupload;
        if (!objectWritten)
        {
            await store.PutAsync(key, [1], "application/octet-stream");
        }

        Assert.NotNull(await store.GetAsync(key));
        Assert.False(await IsQueuedAsync(key));
    }

    [Fact]
    public async Task A_second_drain_does_not_delete_a_reupload_the_first_drain_has_already_dealt_with()
    {
        // Every replica drains, so two passes can take the same key. Drain B takes a prefix queued first and
        // then the key. While B works on the prefix, drain A deletes the key's object and takes it off the
        // queue, and CI uploads the artifact again. B then reaches the key from the list it took earlier: the
        // object it would delete is the re-upload's. The prefix is the pause point because a prefix holds no
        // key lock, so drain A can run to the end inside B's pass.
        using var store = Store();
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var key = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.DebugFiles, projectId) + "seen-by-two-drains";
        var pause = $"debug-files/{Unique()}/";
        await QueueAsync(pause, isPrefix: true, ago: TimeSpan.FromMinutes(1));
        await store.PutAsync(key, [1], "application/octet-stream");
        await QueueAsync(key, isPrefix: false);

        // Recorded rather than asserted inside the step: the step runs inside drain B's store call, whose
        // failures the drain counts and requeues, so an assertion there would never reach the test output.
        var deletedByDrainA = false;
        var drainB = new BeforePrefix(store, pause, async () =>
        {
            await new ObjectDeletionQueue(pg.ConnectionString, store).DrainAsync(10_000);
            deletedByDrainA = await store.GetAsync(key) is null;
            await RecordAsync(ProjectObjectKeys.DebugFiles, projectId, key);
            await store.PutAsync(key, [2], "application/octet-stream");
        });
        await new ObjectDeletionQueue(pg.ConnectionString, drainB).DrainAsync(10_000);

        Assert.True(deletedByDrainA);
        Assert.NotNull(await store.GetAsync(key));
    }

    [Fact]
    public async Task A_delete_the_store_never_answers_is_given_up_and_the_key_stays_queued()
    {
        // The drain holds the key's lock while the store deletes, and an upload of that key waits on the
        // lock, so a store that never answers must not hold it for longer than the drain's bound.
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var key = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.DebugFiles, projectId) + "store-hangs";
        await QueueAsync(key, isPrefix: false);

        var queue = new ObjectDeletionQueue(
            pg.ConnectionString, new NeverAnswers(key), deleteTimeout: TimeSpan.FromMilliseconds(200));
        // Cancelled here too, so a drain that never gives up fails this test and lets go of the key's lock,
        // rather than holding it for every later drain in this class.
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await queue.DrainAsync(10_000, giveUp.Token);

        Assert.True(await IsQueuedAsync(key));
    }

    [Fact]
    public async Task A_prefix_takes_everything_under_it_and_nothing_beside_it()
    {
        using var store = Store();
        var id = Unique();
        var gone = new[] { $"debug-files/{id}/one", $"debug-files/{id}/two" };
        var neighbour = $"debug-files/{id}0/three"; // project 10 beside project 1
        foreach (var key in gone.Append(neighbour))
        {
            await store.PutAsync(key, [1, 2, 3], "application/octet-stream");
        }
        await QueueAsync($"debug-files/{id}/", isPrefix: true);

        await new ObjectDeletionQueue(pg.ConnectionString, store).DrainAsync(10_000);

        foreach (var key in gone)
        {
            Assert.Null(await store.GetAsync(key));
        }
        Assert.NotNull(await store.GetAsync(neighbour));
        Assert.False(await IsQueuedAsync($"debug-files/{id}/"));
    }

    [Fact]
    public async Task A_store_that_fails_keeps_the_key_queued_and_says_so()
    {
        var key = $"sourcemaps/{Unique()}/by-debug-id/c";
        await QueueAsync(key, isPrefix: false);

        var result = await new ObjectDeletionQueue(pg.ConnectionString, new FailingCleanup(_ => true)).DrainAsync(10_000);

        Assert.True(result.Failed >= 1);
        Assert.NotNull(result.FirstFailure);
        Assert.True(await IsQueuedAsync(key));
    }

    [Fact]
    public async Task One_pass_goes_on_past_a_key_that_fails()
    {
        var failing = $"sourcemaps/{Unique()}/by-debug-id/failing";
        var next = $"sourcemaps/{Unique()}/by-debug-id/next";
        await QueueAsync(failing, isPrefix: false);
        await QueueAsync(next, isPrefix: false);

        var result = await new ObjectDeletionQueue(pg.ConnectionString, new FailingCleanup(key => key == failing))
            .DrainAsync(10_000);

        Assert.Equal(1, result.Failed);
        Assert.True(await IsQueuedAsync(failing));
        Assert.False(await IsQueuedAsync(next));
    }

    [Fact]
    public async Task A_key_that_keeps_failing_does_not_hold_up_the_keys_behind_it()
    {
        // Queued first, so it is taken first on every pass; stopping at it would stop the whole queue.
        var stuck = $"sourcemaps/{Unique()}/by-debug-id/stuck";
        var behind = $"sourcemaps/{Unique()}/by-debug-id/behind";
        await QueueAsync(stuck, isPrefix: false);
        await QueueAsync(behind, isPrefix: false);

        // One key a pass, the narrowest window: the stuck key must not be the only one ever taken. Keys other
        // tests in this class left queued go first, so allow a pass for each.
        var queue = new ObjectDeletionQueue(pg.ConnectionString, new FailingCleanup(key => key == stuck));
        var passes = await QueuedCountAsync() + 1;
        for (var pass = 0; pass < passes && await IsQueuedAsync(behind); pass++)
        {
            await queue.DrainAsync(1);
        }

        Assert.True(await IsQueuedAsync(stuck));
        Assert.False(await IsQueuedAsync(behind));
    }

    [Fact]
    public async Task One_worker_pass_takes_an_expired_artifact_out_of_the_index_and_out_of_storage()
    {
        using var store = Store();
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var key = ProjectObjectKeys.ToPrefix(ProjectObjectKeys.SourceMaps, projectId) + "by-debug-id/expired";
        await store.PutAsync(key, Encoding.UTF8.GetBytes("map"), "application/json");
        await new SourceMapArtifactRepository(pg.ConnectionString)
            .RecordAsync(projectId, "1.0.0", null, "expired", "app.js", key, 3);
        var worker = new Condux.ControlPlane.Uploads.UploadStorageWorker(
            new UploadStorage(pg.ConnectionString), new ObjectDeletionQueue(pg.ConnectionString, store),
            // A sweep that finds nothing, so only expiry can have queued the key this test watches.
            new OrphanObjectSweep(pg.ConnectionString, new ListsNothing()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Condux.ControlPlane.Uploads.UploadStorageWorker>.Instance,
            new Condux.Telemetry.ConduxSelfReporter(null));

        // A year on, past every tier's retention.
        await worker.RunOnceAsync(DateTimeOffset.UtcNow.AddDays(365), default);

        Assert.Null(await store.GetAsync(key));
        Assert.False(await IsQueuedAsync(key));
    }

    // Fails the keys the predicate names and deletes nothing else, so a test can fail one key alone.
    private sealed class FailingCleanup(Func<string, bool> fails) : IObjectStoreCleanup
    {
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            fails(key) ? throw new HttpRequestException("storage unreachable") : Task.CompletedTask;

        public Task<int> DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default) =>
            fails(prefix) ? throw new HttpRequestException("storage unreachable") : Task.FromResult(0);

        public IAsyncEnumerable<IReadOnlyList<StoredObject>> ListAsync(
            string prefix, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the drain never lists");
    }

    // Lists nothing and deletes nothing.
    private sealed class ListsNothing : IObjectStoreCleanup
    {
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public async IAsyncEnumerable<IReadOnlyList<StoredObject>> ListAsync(
            string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    // Runs a step just before the real delete of one key. Only that key: other tests leave keys queued, and
    // a step run while the drain holds another key's lock would test nothing.
    private sealed class BeforeDelete(IObjectStoreCleanup inner, string target, Func<Task> step) : IObjectStoreCleanup
    {
        public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            if (key == target)
            {
                await step();
            }
            await inner.DeleteAsync(key, cancellationToken);
        }

        public Task<int> DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default) =>
            inner.DeletePrefixAsync(prefix, cancellationToken);

        public IAsyncEnumerable<IReadOnlyList<StoredObject>> ListAsync(
            string prefix, CancellationToken cancellationToken = default) =>
            inner.ListAsync(prefix, cancellationToken);
    }

    // Never answers a delete of one key until the drain gives up on it. Every other key fails, so it stays
    // queued: earlier tests in this class leave keys queued, and reporting them deleted would lose them.
    private sealed class NeverAnswers(string target) : IObjectStoreCleanup
    {
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            key == target
                ? Task.Delay(Timeout.Infinite, cancellationToken)
                : throw new HttpRequestException("not this test's key");

        public Task<int> DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("not this test's key");

        public IAsyncEnumerable<IReadOnlyList<StoredObject>> ListAsync(
            string prefix, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the drain never lists");
    }

    // Runs a step just before the real delete of one prefix.
    private sealed class BeforePrefix(IObjectStoreCleanup inner, string target, Func<Task> step) : IObjectStoreCleanup
    {
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(key, cancellationToken);

        public async Task<int> DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default)
        {
            if (prefix == target)
            {
                await step();
            }
            return await inner.DeletePrefixAsync(prefix, cancellationToken);
        }

        public IAsyncEnumerable<IReadOnlyList<StoredObject>> ListAsync(
            string prefix, CancellationToken cancellationToken = default) =>
            inner.ListAsync(prefix, cancellationToken);
    }

    private Task RecordAsync(string root, long projectId, string key) => root == ProjectObjectKeys.DebugFiles
        ? new DebugFileRepository(pg.ConnectionString).RecordAsync(
            projectId, new ElfBuild(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString()), key, 1, sha256: null)
        : new SourceMapArtifactRepository(pg.ConnectionString)
            .RecordAsync(projectId, "1.0.0", null, Guid.NewGuid().ToString(), "app.js", key, 1);

    // Completes once some session waits for this key's lock. pg_locks lists the whole server, which other
    // test classes share with their own databases, so the lock is matched by database and key. It runs
    // beside the re-upload and loses the race when the re-upload's row write does not wait, so its bound
    // only matters when nothing waits. pg_locks shows the hash as an unsigned oid, hence the mask.
    private async Task WaitForLockWaiterAsync(string key)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT count(*) FROM pg_locks
            WHERE locktype = 'advisory' AND NOT granted AND classid = 1329744459
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
              AND objid::bigint = (hashtext(@k)::bigint & 4294967295)
            """, conn);
        cmd.Parameters.AddWithValue("k", key);
        for (var i = 0; i < 500; i++)
        {
            if ((long)(await cmd.ExecuteScalarAsync())! > 0)
            {
                return;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("no session waited for an object key lock");
    }

    // Far above any id the shared database hands out, so a test's keys never belong to a real project.
    private static long Unique() => 1_000_000_000 + Random.Shared.Next(1, 1_000_000_000);

    // ago backdates the entry, so a test can say which of its keys a drain takes first.
    private async Task QueueAsync(string key, bool isPrefix, TimeSpan ago = default)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO object_deletions (object_key, is_prefix, enqueued_at) VALUES (@k, @p, now() - @ago)", conn);
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("p", isPrefix);
        cmd.Parameters.AddWithValue("ago", ago);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> QueuedCountAsync()
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM object_deletions", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<bool> IsQueuedAsync(string key)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM object_deletions WHERE object_key = @k", conn);
        cmd.Parameters.AddWithValue("k", key);
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }
}
