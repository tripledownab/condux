using Condux.Core.SourceMaps;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A queued deletion: one object, or every object under a prefix.</summary>
public sealed record QueuedDeletion(string ObjectKey, bool IsPrefix);

/// <summary>What one drain did: objects deleted, keys that failed and stay queued, and the first failure.</summary>
public sealed record DrainResult(int Deleted, int Failed, Exception? FirstFailure);

/// <summary>
/// Drains <c>object_deletions</c>, which <see cref="UploadStorage"/> and the project delete trigger fill. A
/// key leaves the queue only after its delete succeeded, so a store that is down is retried on the next
/// pass, and a key that fails goes to the back of the queue rather than holding up the keys behind it. A key
/// that a row references again, because CI uploaded the same artifact after it was queued, is dropped from
/// the queue rather than deleted. A single key is taken off the queue, checked and deleted under its lock
/// (<see cref="ObjectKeyLocks"/>), which an upload's row write also takes, and a key that is no longer
/// queued by then is left alone, since another drain has dealt with it since this pass took it. So a drain
/// asks the store to delete a key's object only while no row names the key, and only when that drain is the
/// one that took the key's queue entry.
/// </summary>
public sealed class ObjectDeletionQueue(
    string connectionString, IObjectStoreCleanup store, TimeSpan? deleteTimeout = null)
{
    // A single key's delete runs holding its lock, and an upload of that key waits on the lock, so the delete
    // gets a bound of its own rather than whatever the store client allows. Running out of time is a failed
    // delete: the claim rolls back and the key goes to the back of the queue.
    private readonly TimeSpan _deleteTimeout = deleteTimeout ?? TimeSpan.FromSeconds(30);

    // Oldest first.
    private const string TakeSql = """
        SELECT object_key, is_prefix FROM object_deletions ORDER BY enqueued_at LIMIT @limit;
        """;

    // Takes one key off the queue and says whether a row names it again (CI re-uploaded it), in which case
    // the object is the re-upload's and stays. No row back means the key is no longer queued: another drain
    // has dealt with it since this pass read the queue, either deleting the object or finding a row that
    // names it, and the object there now is not this pass's to delete. Run under the key's lock, so no
    // upload row can land between this and the delete that follows.
    private const string ClaimSql = """
        DELETE FROM object_deletions q
        WHERE q.object_key = @key
        RETURNING EXISTS (SELECT 1 FROM sourcemap_artifacts s WHERE s.object_key = q.object_key)
               OR EXISTS (SELECT 1 FROM debug_files d WHERE d.object_key = q.object_key);
        """;

    private const string ForgetSql = "DELETE FROM object_deletions WHERE object_key = @key;";

    // A key that failed goes to the back, so keys that fail for good cannot fill the oldest-first window
    // every pass takes and keep the rest of the queue from ever being reached.
    private const string RequeueSql = "UPDATE object_deletions SET enqueued_at = now() WHERE object_key = @key;";

    /// <summary>Deletes up to <paramref name="limit"/> queued keys and returns how many objects went and
    /// how many keys failed. A key that failed stays queued for the next pass.</summary>
    public async Task<DrainResult> DrainAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        var taken = new List<QueuedDeletion>();
        await using (var take = new NpgsqlCommand(TakeSql, conn))
        {
            take.Parameters.AddWithValue("limit", limit);
            await using var reader = await take.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                taken.Add(new QueuedDeletion(reader.GetString(0), reader.GetBoolean(1)));
            }
        }

        var deleted = 0;
        Exception? firstFailure = null;
        var failed = 0;
        foreach (var item in taken)
        {
            try
            {
                deleted += await DeleteAsync(conn, item, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed++;
                firstFailure ??= ex;
                await using var requeue = new NpgsqlCommand(RequeueSql, conn);
                requeue.Parameters.AddWithValue("key", item.ObjectKey);
                await requeue.ExecuteNonQueryAsync(ct);
            }
        }
        return new DrainResult(deleted, failed, firstFailure);
    }

    // Deletes what one queued key stands for, takes the key off the queue, and returns how many objects went.
    // A single key's claim and delete commit together under its lock, so a delete that fails puts it back.
    private async Task<int> DeleteAsync(NpgsqlConnection conn, QueuedDeletion item, CancellationToken ct)
    {
        if (item.IsPrefix)
        {
            var count = await store.DeletePrefixAsync(item.ObjectKey, ct);
            await using var forget = new NpgsqlCommand(ForgetSql, conn);
            forget.Parameters.AddWithValue("key", item.ObjectKey);
            await forget.ExecuteNonQueryAsync(ct);
            return count;
        }
        await using var tx = await conn.BeginTransactionAsync(ct);
        await ObjectKeyLocks.LockAsync(conn, tx, item.ObjectKey, ct);
        await using var claim = new NpgsqlCommand(ClaimSql, conn, tx);
        claim.Parameters.AddWithValue("key", item.ObjectKey);
        if (await claim.ExecuteScalarAsync(ct) is not false)
        {
            await tx.CommitAsync(ct);
            return 0;
        }
        using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            bounded.CancelAfter(_deleteTimeout);
            await store.DeleteAsync(item.ObjectKey, bounded.Token);
        }
        await tx.CommitAsync(ct);
        return 1;
    }
}
