using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Plans;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// POST /api/orgs must never honour a tier the caller sends.
///
/// <para>It used to: <c>CreateAsync(req.Slug, req.Name, (int)(req.Tier ?? Tier.Free))</c>. Any user
/// could sign up and post <c>tier: 3</c> to take Enterprise, which is unlimited event ingest
/// (<c>MonthlyEvents: 0</c>), uncapped Conductor runs (<c>AiFixesPerMonth: 0</c>) and no dollar ceiling
/// (<c>FixComputeCapUsd</c> null, and <c>AiFixBudget.IsOverCap</c> never blocks on null). With no BYO key
/// configured the fix engine falls back to the process default key, so those runs bill the platform.</para>
///
/// <para>The tier is Stripe's to write (ADR-0026). This pins that the request body cannot move it.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrgTierEscalationTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(1)] // Team
    [InlineData(2)] // Business
    [InlineData(3)] // Enterprise: the one worth stealing
    public async Task Requested_tier_is_ignored_and_the_org_is_created_free(int requestedTier)
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        await ApiAuth.SignUpAsync(client);

        var resp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "esc-" + Guid.NewGuid().ToString("N"), name = "Escalation", tier = requestedTier });

        // The request still succeeds: an unknown property is ignored, not rejected. What must not happen
        // is the tier being honoured, so assert on the persisted value rather than the status code.
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)Tier.Free, body.GetProperty("tier").GetInt32());
    }

    [Fact]
    public async Task Omitting_the_tier_also_yields_free()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        await ApiAuth.SignUpAsync(client);

        var resp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "plain-" + Guid.NewGuid().ToString("N"), name = "Plain" });

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)Tier.Free, body.GetProperty("tier").GetInt32());
    }
}
