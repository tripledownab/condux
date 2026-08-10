using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Pins the atomic upsert semantics of the per-org AI-fix allowance counter (#100): consume until the
/// limit guard rejects, refunds free a slot, a new calendar month resets the counter, and limit 0 is
/// uncapped while still tracking usage.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AiFixQuotaTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset July = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset August = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Consumes_refunds_and_rolls_the_period_over()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var org = await new OrgRepository(pg.ConnectionString)
            .CreateAsync("quota-" + Guid.NewGuid().ToString("N"), "Quota Org", tier: 1);
        var quota = new PostgresAiFixQuota(pg.ConnectionString);

        // Two runs fit a limit of 2; the third is rejected; a refund frees one slot.
        Assert.True(await quota.TryConsumeAsync(org.Id, 2, July));
        Assert.True(await quota.TryConsumeAsync(org.Id, 2, July));
        Assert.False(await quota.TryConsumeAsync(org.Id, 2, July));
        Assert.Equal(2, await quota.GetUsedAsync(org.Id, July));

        await quota.RefundAsync(org.Id, July);
        Assert.Equal(1, await quota.GetUsedAsync(org.Id, July));
        Assert.True(await quota.TryConsumeAsync(org.Id, 2, July));

        // A new calendar month resets the counter in the same atomic statement.
        Assert.True(await quota.TryConsumeAsync(org.Id, 2, August));
        Assert.Equal(1, await quota.GetUsedAsync(org.Id, August));
    }

    [Fact]
    public async Task Uncapped_still_tracks_usage()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var org = await new OrgRepository(pg.ConnectionString)
            .CreateAsync("quota-" + Guid.NewGuid().ToString("N"), "Ent Org", tier: 3);
        var quota = new PostgresAiFixQuota(pg.ConnectionString);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(await quota.TryConsumeAsync(org.Id, 0, July)); // 0 = uncapped
        }
        Assert.Equal(5, await quota.GetUsedAsync(org.Id, July));
    }
}
