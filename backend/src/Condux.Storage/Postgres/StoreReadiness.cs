using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// Whether the stores a request actually needs are reachable right now.
///
/// The health endpoints answer "ok" unconditionally, which is fine for a container probe and useless for
/// uptime monitoring: a control-plane that has lost Postgres still answers 200, so a monitor agrees with
/// the outage instead of catching it. This is what makes readiness mean the product works rather than the
/// process is running.
///
/// Each check is a trivial round trip with a short timeout of its own. A readiness probe that hangs is
/// worse than one that fails, because a monitor cannot tell it apart from a network problem of its own.
/// </summary>
public static class StoreReadiness
{
    /// <summary>
    /// Long enough for a healthy store under load, short enough that the probe answers before any
    /// reasonable monitor gives up on it.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>Whether Postgres answers. False covers unreachable, unauthenticated and timed out alike:
    /// the probe reports that the store cannot be used, not why, which is the operator's job to find.</summary>
    public static async Task<bool> PostgresAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Timeout);

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(deadline.Token);
            await using var cmd = new NpgsqlCommand("SELECT 1;", conn);
            return await cmd.ExecuteScalarAsync(deadline.Token) is not null;
        }
        catch (Exception)
        {
            // Any failure is the same answer to a monitor: not ready.
            return false;
        }
    }

    /// <summary>Whether ClickHouse answers. Uses its own ping rather than a query, so a healthy but empty
    /// database is still ready.</summary>
    public static async Task<bool> ClickHouseAsync(
        HttpClient http, string baseUrl, string user, string password,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/ping");
            request.Headers.Add("X-ClickHouse-User", user);
            request.Headers.Add("X-ClickHouse-Key", password);

            using var response = await http.SendAsync(request, deadline.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
