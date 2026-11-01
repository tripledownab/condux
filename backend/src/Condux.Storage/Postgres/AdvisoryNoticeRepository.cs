using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// Which projects a new dependency advisory notifies, recorded per repository link, advisory, affected
/// package and UTC day (<c>advisory_notices</c>). GitHub raises one alert per manifest, so a monorepo hears
/// of one advisory several times at once, and it sends a delivery again on request; neither notifies anyone
/// twice in a day. The same advisory reintroduced later that day is not notified again either.
/// </summary>
public sealed class AdvisoryNoticeRepository(string connectionString)
{
    // A project hears of a repository only through a link whose org holds the installation the delivery
    // came through. The name alone would not do, since a repository name is whatever any org typed when
    // linking. It is compared without regard to case because a link may spell it differently from the
    // delivery. The insert is the claim, so a project is returned only by the statement that recorded it.
    private const string ClaimSql = """
        INSERT INTO advisory_notices (repo_link_id, ghsa_id, package, day, project_id)
        SELECT l.id, @ghsa, @package, @day, l.project_id
        FROM repo_links l
        JOIN projects p ON p.id = l.project_id
        JOIN github_installations g ON g.org_id = p.org_id
        WHERE lower(l.repo_full_name) = lower(@repo) AND g.installation_id = @installation
        ON CONFLICT DO NOTHING
        RETURNING project_id;
        """;

    /// <summary>Records the advisory for <paramref name="package"/> against every link the delivery reaches
    /// for <paramref name="day"/> and returns the projects newly recorded, each once.</summary>
    public async Task<IReadOnlyList<long>> ClaimAsync(
        long installationId, string repoFullName, string ghsaId, string package, DateOnly day,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ClaimSql, conn);
        cmd.Parameters.AddWithValue("ghsa", ghsaId);
        cmd.Parameters.AddWithValue("package", package);
        cmd.Parameters.AddWithValue("day", day);
        cmd.Parameters.AddWithValue("installation", installationId);
        cmd.Parameters.AddWithValue("repo", repoFullName);
        var projects = new HashSet<long>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            projects.Add(reader.GetInt64(0));
        }
        return [.. projects];
    }
}
