using Condux.Core.Plans;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Exercises the full provisioning path against real Postgres: create org → project
/// → DSN key, then prove the relay's <see cref="PostgresProjectStore"/> authenticates
/// that DSN and rejects wrong/revoked keys. This is the "back the relay's real DSN
/// validation" guarantee for #34. Opt-in via <c>--filter Category=Integration</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogProvisioningTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ProvisionThenAuthenticate_And_RevokeInvalidates()
    {
        var orgs = new OrgRepository(pg.ConnectionString);
        var projects = new ProjectRepository(pg.ConnectionString);
        var keys = new DsnKeyRepository(pg.ConnectionString);
        var store = new PostgresProjectStore(pg.ConnectionString);

        // Provision an org + project + key.
        var org = await orgs.CreateAsync("acme", "Acme Inc", (int)Tier.Business);
        var project = await projects.CreateAsync(org.Id, "Backend", "python");
        var key = await keys.CreateAsync(project.Id, "default", "pubkey-abc");

        // #126: a DSN carries the project's PUBLIC UUID (not the bigint); the relay store resolves it back
        // to the numeric id used downstream, so authenticate with the UUID and expect the numeric id.
        var dsn = project.PublicId.ToString();

        // The relay store authenticates the provisioned DSN with the org's tier.
        var authed = await store.AuthenticateAsync(dsn, "pubkey-abc");
        Assert.NotNull(authed);
        Assert.Equal(project.Id.ToString(), authed!.Id);
        Assert.Equal(Tier.Business, authed.Tier);

        // Wrong key and unknown project are rejected (a non-UUID segment can never match either).
        Assert.Null(await store.AuthenticateAsync(dsn, "not-the-key"));
        Assert.Null(await store.AuthenticateAsync(Guid.NewGuid().ToString(), "pubkey-abc"));

        // Revoking the key (tenant-scoped) invalidates ingest with it.
        Assert.False(await keys.RevokeAsync(9_999_999, key.Id)); // wrong project: no-op
        Assert.True(await keys.RevokeAsync(project.Id, key.Id));
        Assert.Null(await store.AuthenticateAsync(dsn, "pubkey-abc"));
        Assert.False(await keys.RevokeAsync(project.Id, key.Id)); // already revoked
    }

    [Fact]
    public async Task ProjectsAreListedPerOrg_WithMultipleKeys()
    {
        var orgs = new OrgRepository(pg.ConnectionString);
        var projects = new ProjectRepository(pg.ConnectionString);
        var keys = new DsnKeyRepository(pg.ConnectionString);

        var org = await orgs.CreateAsync("globex-" + Guid.NewGuid().ToString("N"), "Globex", (int)Tier.Team);
        var web = await projects.CreateAsync(org.Id, "Web", "javascript");
        await projects.CreateAsync(org.Id, "API", "go");

        // A second key on the same project (rotation) — both authenticate.
        await keys.CreateAsync(web.Id, "primary", "web-key-1");
        await keys.CreateAsync(web.Id, "rotation", "web-key-2");

        var listed = await projects.ListByOrgAsync(org.Id);
        Assert.Equal(2, listed.Count);

        var webKeys = await keys.ListByProjectAsync(web.Id);
        Assert.Equal(2, webKeys.Count);
        Assert.All(webKeys, k => Assert.True(k.IsActive));

        // #126: authenticate by the project's public UUID (the DSN segment), not the bigint.
        var store = new PostgresProjectStore(pg.ConnectionString);
        Assert.NotNull(await store.AuthenticateAsync(web.PublicId.ToString(), "web-key-1"));
        Assert.NotNull(await store.AuthenticateAsync(web.PublicId.ToString(), "web-key-2"));
    }

    [Fact]
    public async Task RenameKey_IsTenantScoped_AndReturnsUpdatedRow()
    {
        var orgs = new OrgRepository(pg.ConnectionString);
        var projects = new ProjectRepository(pg.ConnectionString);
        var keys = new DsnKeyRepository(pg.ConnectionString);

        var org = await orgs.CreateAsync("initech-" + Guid.NewGuid().ToString("N"), "Initech", (int)Tier.Team);
        var project = await projects.CreateAsync(org.Id, "Web", "javascript");
        var key = await keys.CreateAsync(project.Id, "default", "rename-key-" + Guid.NewGuid().ToString("N"));

        // Renaming under the wrong project is a no-op (tenant-scoped) → null, nothing changed.
        Assert.Null(await keys.UpdateLabelAsync(9_999_999, key.Id, "hacked"));

        // Renaming within the project returns the updated row (same key, only the label changed)...
        var renamed = await keys.UpdateLabelAsync(project.Id, key.Id, "Production");
        Assert.NotNull(renamed);
        Assert.Equal("Production", renamed!.Label);
        Assert.Equal(key.PublicKey, renamed.PublicKey);

        // ...and it persists on read.
        var listed = await keys.ListByProjectAsync(project.Id);
        Assert.Equal("Production", Assert.Single(listed).Label);
    }
}
