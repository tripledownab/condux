using Condux.Core.Plans;

namespace Condux.Core.Projects;

/// <summary>A parsed DSN: <c>publicKey@host/projectId</c>.</summary>
public sealed record Dsn(string PublicKey, string Host, string ProjectId)
{
    public static bool TryParse(string dsn, out Dsn? result)
    {
        result = null;
        if (!Uri.TryCreate(dsn, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var publicKey = uri.UserInfo.Split(':', 2)[0];
        var projectId = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(projectId))
        {
            return false;
        }

        result = new Dsn(publicKey, uri.Host, projectId);
        return true;
    }
}

/// <summary>An authenticated project: its id and effective plan tier (inherited from its org).</summary>
public sealed record Project(string Id, Tier Tier);

/// <summary>
/// Resolves and authenticates a project from a DSN (project id + public key). The
/// relay calls this on the ingest path. Implementations: in-memory (dev/tests), a
/// Postgres-backed store (real), and a caching wrapper so the hot path rarely hits
/// the database.
/// </summary>
public interface IProjectStore
{
    /// <summary>Returns the project iff <paramref name="publicKey"/> is an active DSN key for it; otherwise null.</summary>
    Task<Project?> AuthenticateAsync(string projectId, string publicKey, CancellationToken ct = default);
}

/// <summary>In-memory store for dev/tests, seeded with <c>(projectId, publicKey, tier)</c> DSNs.</summary>
public sealed class InMemoryProjectStore : IProjectStore
{
    private readonly Dictionary<(string ProjectId, string PublicKey), Project> _byDsn;

    public InMemoryProjectStore(IEnumerable<(string ProjectId, string PublicKey, Tier Tier)> dsns) =>
        _byDsn = dsns.ToDictionary(d => (d.ProjectId, d.PublicKey), d => new Project(d.ProjectId, d.Tier));

    public Task<Project?> AuthenticateAsync(string projectId, string publicKey, CancellationToken ct = default) =>
        Task.FromResult(_byDsn.GetValueOrDefault((projectId, publicKey)));
}
