using System.Globalization;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>Emits a lightweight Postgres <c>NOTIFY</c> on the shared <see cref="Channel"/> carrying a
/// project id, so a listener (the control-plane's SSE hub) can nudge that project's dashboard badges to
/// refetch the moment something changes. Best-effort by design — the caller ignores failures and a missed
/// notify is caught by the badge's safety poll (ADR-0030). The payload is just the numeric project id,
/// well under the 8000-byte NOTIFY limit. Reusable: any writer (issues today, fixes later) can nudge a
/// project's badges through the one channel.</summary>
public sealed class ProjectEventNotifier(string connectionString)
{
    /// <summary>The Postgres LISTEN/NOTIFY channel carrying project ids of changed projects.</summary>
    public const string Channel = "condux_project_events";

    public async Task NotifyAsync(long projectId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", conn);
        cmd.Parameters.AddWithValue("channel", Channel);
        cmd.Parameters.AddWithValue("payload", projectId.ToString(CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
