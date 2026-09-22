using System.Globalization;
using Condux.Core.Plans;
using Condux.Core.Projects;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// Authenticates ingest against Postgres: a (projectId, publicKey) pair is valid
/// iff there is an <c>is_active</c> DSN key for that project. Returns the project's
/// effective tier (its org's plan). Wrap in <see cref="CachingProjectStore"/> on
/// the relay so this is queried only on cache misses.
/// </summary>
public sealed class PostgresProjectStore(string connectionString) : IProjectStore
{
    // The DSN path segment is the project's public UUID. The query returns the numeric p.id, which is
    // what flows downstream (the Kafka key, ClickHouse events.project_id, the issues FK) — so the UUID
    // never leaves the relay.
    //
    // p.user_key_salt rides the same row rather than a second lookup: the pseudonymous user key is
    // derived on this path, and a per-project salt that cost a round trip per event would not be
    // affordable there. It is read and used inside the relay and is never part of any response.
    private const string Sql = """
        SELECT p.id, o.tier, p.user_key_salt
        FROM dsn_keys k
        JOIN projects p ON p.id = k.project_id
        JOIN orgs o     ON o.id = p.org_id
        WHERE p.public_id = @pid AND k.public_key = @key AND k.is_active
        LIMIT 1;
        """;

    public async Task<Project?> AuthenticateAsync(
        string projectId, string publicKey, CancellationToken ct = default)
    {
        // A DSN carries the project's public UUID; a non-UUID path segment can never match.
        if (!Guid.TryParse(projectId, out var publicId))
        {
            return null;
        }

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("pid", publicId);
        cmd.Parameters.AddWithValue("key", publicKey);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var id = reader.GetInt64(0).ToString(CultureInfo.InvariantCulture);
        var tier = (Tier)reader.GetInt16(1);
        return new Project(id, tier, reader.GetString(2));
    }
}
