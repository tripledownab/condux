using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Auth;
using Condux.Core.Plans;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Org AI-fix settings (#101/#120): admin+ toggles <c>ai_fix_mode</c> and the optional cost cap via
/// PATCH /api/orgs/{id}; switching to auto needs a plan with AI fixes (Free 409s), a member cannot mutate,
/// and a non-member is hidden (404). The cost cap round-trips and drives the ai-fix-usage meter.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrgSettingsTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private HttpClient CreateClient() => ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

    // Not static: needs the connection string to set the tier out of band, since POST /api/orgs always
    // creates Free and only the Stripe webhook moves an org off it.
    private async Task<long> CreateOrgAsync(HttpClient client, int tier)
    {
        var resp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org" });
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, id, tier);
        return id;
    }

    [Fact]
    public async Task Admin_switches_to_auto_on_a_paid_plan_and_free_is_gated()
    {
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);

        // A Team org (AI fixes included) can go auto.
        var teamOrg = await CreateOrgAsync(owner, (int)Tier.Team);
        var patch = await owner.PatchAsJsonAsync($"/api/orgs/{teamOrg}", new { aiFixMode = 1 });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal(1, (await patch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("aiFixMode").GetInt32());

        // Switching back to manual always works.
        var back = await owner.PatchAsJsonAsync($"/api/orgs/{teamOrg}", new { aiFixMode = 0 });
        Assert.Equal(0, (await back.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("aiFixMode").GetInt32());

        // An invalid mode is rejected.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await owner.PatchAsJsonAsync($"/api/orgs/{teamOrg}", new { aiFixMode = 7 })).StatusCode);
    }

    [Fact]
    public async Task Free_org_cannot_enable_auto_and_non_admins_are_blocked()
    {
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var freeOrg = await CreateOrgAsync(owner, (int)Tier.Free);

        // Free includes Conductor runs but not auto mode, so auto is refused with an upgrade prompt while
        // manual stays allowed. The gate is the tier's AutoFix flag, not whether it has an allowance.
        Assert.Equal(HttpStatusCode.Conflict,
            (await owner.PatchAsJsonAsync($"/api/orgs/{freeOrg}", new { aiFixMode = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await owner.PatchAsJsonAsync($"/api/orgs/{freeOrg}", new { aiFixMode = 0 })).StatusCode);

        // A plain member cannot mutate (403); a non-member is hidden (404). A user belongs to one org
        // (ADR-0018), so a second owner mints the team org this checks.
        var owner2 = CreateClient();
        await ApiAuth.SignUpAsync(owner2);
        var teamOrg = await CreateOrgAsync(owner2, (int)Tier.Team);
        var memberClient = CreateClient();
        var member = await ApiAuth.SignUpAsync(memberClient);
        await new OrgMemberRepository(pg.ConnectionString).AddAsync(teamOrg, member.UserId, OrgRole.Member);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await memberClient.PatchAsJsonAsync($"/api/orgs/{teamOrg}", new { aiFixMode = 1 })).StatusCode);

        var outsider = CreateClient();
        await ApiAuth.SignUpAsync(outsider);
        Assert.Equal(HttpStatusCode.NotFound,
            (await outsider.PatchAsJsonAsync($"/api/orgs/{teamOrg}", new { aiFixMode = 1 })).StatusCode);
    }

    [Fact]
    public async Task Team_cap_is_the_plan_fair_use_ceiling_customers_cannot_change()
    {
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var org = await CreateOrgAsync(owner, (int)Tier.Team);

        // A Team customer cannot set their own cap — it's a Condux-owned fair-use compute ceiling
        // (ADR-0020/0027). A PATCH carrying a cap is ignored, so the stored override stays null.
        var patch = await owner.PatchAsJsonAsync($"/api/orgs/{org}", new { aiFixMode = 1, aiFixCostCapUsd = 25 });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal(JsonValueKind.Null,
            (await patch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("aiFixCostCapUsd").ValueKind);

        // The usage meter still shows a ceiling — the tier's fair-use default, not "no cap" — plus the run
        // allowance (25 monthly fixes, none used yet).
        var usage = await owner.GetFromJsonAsync<JsonElement>($"/api/orgs/{org}/ai-fix-usage");
        Assert.Equal(0m, usage.GetProperty("monthToDateUsd").GetDecimal());
        Assert.Equal(PlanCatalog.For(Tier.Team).FixComputeCapUsd!.Value, usage.GetProperty("capUsd").GetDecimal());
        Assert.Equal(25, usage.GetProperty("remainingFixes").GetInt32());
        Assert.False(usage.GetProperty("uncappedFixes").GetBoolean());
    }

    [Fact]
    public async Task Free_org_meter_reports_its_monthly_allowance_and_fair_use_ceiling()
    {
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var org = await CreateOrgAsync(owner, (int)Tier.Free);

        // This is the exact payload Settings -> General renders, and it had no Free coverage at all: the
        // meter was only asserted for Team and Enterprise. That blind spot is how a stale client-side tier
        // mirror hid the whole compute section from Free orgs without a single test failing.
        var limits = PlanCatalog.For(Tier.Free);
        var usage = await owner.GetFromJsonAsync<JsonElement>($"/api/orgs/{org}/ai-fix-usage");

        Assert.Equal(0m, usage.GetProperty("monthToDateUsd").GetDecimal());
        // Read from the catalog rather than a literal: the point is that the tier's own ceiling reaches the
        // meter, not that it happens to equal today's number.
        Assert.Equal(limits.FixComputeCapUsd!.Value, usage.GetProperty("capUsd").GetDecimal());
        Assert.Equal(limits.AiFixesPerMonth, usage.GetProperty("remainingFixes").GetInt32());
        // Free is metered, so it must never read as uncapped — that is the 0-means-unlimited footgun.
        Assert.False(usage.GetProperty("uncappedFixes").GetBoolean());
    }

    [Fact]
    public async Task Enterprise_customer_sets_their_own_byo_budget_which_round_trips()
    {
        var owner = CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var org = await CreateOrgAsync(owner, (int)Tier.Enterprise);

        // BYO/Enterprise sets a real budget on its own key — it sticks and drives the meter. Enterprise has
        // unlimited runs, so the budget is its only throttle (uncappedFixes true).
        var patch = await owner.PatchAsJsonAsync($"/api/orgs/{org}", new { aiFixMode = 1, aiFixCostCapUsd = 200 });
        Assert.Equal(200m,
            (await patch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("aiFixCostCapUsd").GetDecimal());
        var usage = await owner.GetFromJsonAsync<JsonElement>($"/api/orgs/{org}/ai-fix-usage");
        Assert.Equal(200m, usage.GetProperty("capUsd").GetDecimal());
        Assert.True(usage.GetProperty("uncappedFixes").GetBoolean());

        // A negative cap is rejected; null clears it → uncapped (their money, their choice).
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync(
            $"/api/orgs/{org}", new { aiFixMode = 1, aiFixCostCapUsd = -5 })).StatusCode);
        var cleared = await owner.PatchAsJsonAsync(
            $"/api/orgs/{org}", new { aiFixMode = 1, aiFixCostCapUsd = (decimal?)null });
        Assert.Equal(JsonValueKind.Null,
            (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("aiFixCostCapUsd").ValueKind);
        var clearedUsage = await owner.GetFromJsonAsync<JsonElement>($"/api/orgs/{org}/ai-fix-usage");
        Assert.Equal(JsonValueKind.Null, clearedUsage.GetProperty("capUsd").ValueKind);
    }
}
