using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The audit list against real Postgres. The unfiltered read is the one the console actually makes on
/// every page load, and it was the only path with no coverage, which is how it reached production
/// throwing 42P08: the org parameter is first used in "IS NULL", where Postgres has no column to infer a
/// type from, so a null value left the parameter type unresolved however Npgsql declared it client-side.
/// A server-side cast pins it. This needs a real server because the failure is Postgres resolving types,
/// which no fake reproduces.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminAuditRepositoryTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Lists_every_org_when_no_org_filter_is_given()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var audit = new AdminAuditRepository(pg.ConnectionString);
        await audit.WriteAsync(1, "boss@condux.test", "org.update", targetOrgId: 7, targetUserId: null, "{}");
        await audit.WriteAsync(1, "boss@condux.test", "org.update", targetOrgId: 9, targetUserId: null, "{}");

        var entries = await audit.ListAsync(orgId: null);

        // Both orgs come back, so the null is genuinely reaching the query as "no filter" rather than
        // matching nothing.
        Assert.Contains(entries, e => e.TargetOrgId == 7);
        Assert.Contains(entries, e => e.TargetOrgId == 9);
    }

    [Fact]
    public async Task Filters_to_one_org_when_given_one()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var audit = new AdminAuditRepository(pg.ConnectionString);
        await audit.WriteAsync(1, "boss@condux.test", "billing.cancel", targetOrgId: 11, targetUserId: null, "{}");
        await audit.WriteAsync(1, "boss@condux.test", "billing.cancel", targetOrgId: 12, targetUserId: null, "{}");

        var entries = await audit.ListAsync(orgId: 11);

        // The same parameter is used a second time as an equality, so the cast has to leave filtering intact.
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Equal(11, e.TargetOrgId));
    }
}
