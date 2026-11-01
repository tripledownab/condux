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
        var audit = new AdminAuditRepository(pg.ConnectionString);
        await audit.WriteAsync(1, "boss@condux.test", "billing.cancel", targetOrgId: 11, targetUserId: null, "{}");
        await audit.WriteAsync(1, "boss@condux.test", "billing.cancel", targetOrgId: 12, targetUserId: null, "{}");

        var entries = await audit.ListAsync(orgId: 11);

        // The same parameter is used a second time as an equality, so the cast has to leave filtering intact.
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Equal(11, e.TargetOrgId));
    }

    /// <summary>
    /// Postgres refuses <c>\u0000</c> in jsonb with 22P05, and every jsonb column here is written from caller
    /// or model text, so a NUL in one failed the statement (a runner fix request answered 503, a fix audit
    /// row was dropped unseen). The jsonb parameter replaces it with U+FFFD; the text <c>\u0000</c>, a
    /// backslash and five letters, is not a NUL and is kept as written.
    /// </summary>
    [Fact]
    public async Task A_nul_in_jsonb_details_is_stored_as_the_replacement_character()
    {
        var audit = new AdminAuditRepository(pg.ConnectionString);
        await audit.WriteAsync(1, "boss@condux.test", "org.update", targetOrgId: 13, targetUserId: null,
            """{"name":"a\u0000b","literal":"x\\u0000"}""");

        var details = Assert.Single(await audit.ListAsync(orgId: 13)).Details;

        Assert.Equal("a\uFFFDb", details.GetProperty("name").GetString());
        Assert.Equal("x\\u0000", details.GetProperty("literal").GetString());
    }
}
