using Condux.Core.Plans;
using Condux.Core.Scrub;

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

/// <summary>
/// An authenticated project: its id, its effective plan tier (inherited from its org) and the salt its
/// pseudonymous user keys are derived with.
///
/// <para>The salt is a positional parameter and not an optional one, so every store that can produce a
/// project has to say where its salt comes from. An optional one would let a store default to empty and
/// silently hand back the unkeyed hash this replaced.</para>
///
/// <para><b>Never serialise this.</b> The relay answers with an event id and nothing else; the salt is
/// read on the ingest path and goes no further. Publishing it would return the pseudonyms to being
/// brute-forceable by whoever read it.</para>
/// </summary>
public sealed record Project(string Id, Tier Tier, string UserKeySalt);

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

/// <summary>
/// In-memory store for dev/tests, seeded with <c>(projectId, publicKey, tier)</c> DSNs.
///
/// <para>Each seeded project gets a fresh random salt, generated here rather than written down. A
/// constant would be a published salt, and this store activates whenever the relay has no Postgres
/// configured, which still leaves a consumer writing rows to ClickHouse. Per instance rather than per
/// process, so a test that builds two stores gets two salts and cannot pass by accident.</para>
/// </summary>
public sealed class InMemoryProjectStore : IProjectStore
{
    private readonly Dictionary<(string ProjectId, string PublicKey), Project> _byDsn;

    public InMemoryProjectStore(IEnumerable<(string ProjectId, string PublicKey, Tier Tier)> dsns) =>
        _byDsn = dsns.ToDictionary(
            d => (d.ProjectId, d.PublicKey),
            d => new Project(d.ProjectId, d.Tier, UserKeys.NewSalt()));

    public Task<Project?> AuthenticateAsync(string projectId, string publicKey, CancellationToken ct = default) =>
        Task.FromResult(_byDsn.GetValueOrDefault((projectId, publicKey)));
}
