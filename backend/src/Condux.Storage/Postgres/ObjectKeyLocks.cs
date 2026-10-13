using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// The lock that orders an upload's row write against the deletion of the object under the same key. An
/// upload writes its row before its object, and <see cref="ObjectDeletionQueue"/> deletes a queued key's
/// object only while holding this lock and having found no row that references the key. So an upload's row
/// either lands first, and the queue sees it and leaves the object alone, or waits until the queue's delete
/// has returned or been given up, and the upload's own object is written after that.
/// </summary>
public static class ObjectKeyLocks
{
    // The two-key form, with the first key naming this use ("OBJK" as an int), so another use of advisory
    // locks with its own first key can never collide with these.
    private const string LockSql = "SELECT pg_advisory_xact_lock(1329744459, hashtext(@key));";

    /// <summary>Takes the lock for <paramref name="objectKey"/> until <paramref name="transaction"/> ends.
    /// Two keys may hash alike, which only makes one wait for the other.</summary>
    public static async Task LockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string objectKey, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(LockSql, connection, transaction);
        cmd.Parameters.AddWithValue("key", objectKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
