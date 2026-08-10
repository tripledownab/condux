using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Admin billing control (ADR-0027). Verifies the gate (all action routes 404 when Stripe is off) and
/// every guard branch (no subscription / no customer / non-purchasable tier) for an org with no Stripe
/// linkage — none of which touch the network. The happy-path Stripe calls are exercised against the live
/// API only in a real deploy; here the point is that the endpoints never write the tier themselves (the
/// webhook stays the single writer, ADR-0026), so a guard-rejected action leaves the tier untouched.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminBillingTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    // Tests share one Postgres container, so each mints a unique admin email (see AdminOrgManagementTest).
    private static string NewAdminEmail() => $"boss-{Guid.NewGuid():N}@condux.test";

    private static readonly Action<IWebHostBuilder> StripeOn = b =>
    {
        b.UseSetting("CONDUX_STRIPE_SECRET_KEY", "sk_test_x");
        b.UseSetting("CONDUX_STRIPE_WEBHOOK_SECRET", "whsec_x");
        b.UseSetting("CONDUX_STRIPE_PRICE_TEAM", "price_team");
        b.UseSetting("CONDUX_STRIPE_PRICE_BUSINESS", "price_business");
    };

    [Fact]
    public async Task Action_routes_404_when_stripe_is_off_but_status_still_reads()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var adminEmail = NewAdminEmail();
        var app = ControlPlaneApp.Create(pg.ConnectionString, platformAdminEmails: adminEmail);
        var (admin, orgId) = await AdminWithOrgAsync(app, adminEmail);

        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsync($"/api/admin/orgs/{orgId}/billing/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsync($"/api/admin/orgs/{orgId}/billing/portal", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(
            $"/api/admin/orgs/{orgId}/billing/plan", new { tier = "Team" })).StatusCode);

        // Status reads regardless: it just reports billing is disabled.
        var status = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/orgs/{orgId}/billing");
        Assert.False(status.GetProperty("billingEnabled").GetBoolean());
    }

    [Fact]
    public async Task Guards_reject_a_stripe_org_with_no_subscription_or_customer_and_leave_the_tier_alone()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var adminEmail = NewAdminEmail();
        var app = ControlPlaneApp.Create(
            pg.ConnectionString, platformAdminEmails: adminEmail, configure: StripeOn);
        var (admin, orgId) = await AdminWithOrgAsync(app, adminEmail);

        // Status: Stripe enabled, Free tier, Team+Business purchasable, no subscription.
        var status = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/orgs/{orgId}/billing");
        Assert.True(status.GetProperty("billingEnabled").GetBoolean());
        Assert.Equal("Free", status.GetProperty("tier").GetString());
        Assert.False(status.GetProperty("hasSubscription").GetBoolean());
        Assert.Equal(2, status.GetProperty("purchasableTiers").GetArrayLength());

        // No subscription -> cancel + plan change refused; no customer -> portal refused.
        await AssertBadRequest(admin, $"/api/admin/orgs/{orgId}/billing/cancel", null, "no_subscription");
        await AssertBadRequest(admin, $"/api/admin/orgs/{orgId}/billing/plan", new { tier = "Team" }, "no_subscription");
        await AssertBadRequest(admin, $"/api/admin/orgs/{orgId}/billing/plan", new { tier = "Nope" }, "tier_not_purchasable");
        await AssertBadRequest(admin, $"/api/admin/orgs/{orgId}/billing/portal", null, "no_customer");

        // The tier was never written by any of the above (the webhook is the only writer).
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/orgs/{orgId}");
        Assert.Equal(0, detail.GetProperty("tier").GetInt32());
    }

    private static async Task AssertBadRequest(HttpClient client, string url, object? body, string error)
    {
        var resp = await client.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(error, (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    private static async Task<(HttpClient Admin, long OrgId)> AdminWithOrgAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, string adminEmail)
    {
        var admin = app.CreateClient();
        await ApiAuth.SignUpAsync(admin, adminEmail);
        var resp = await admin.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Acme", tier = 0 });
        resp.EnsureSuccessStatusCode();
        var orgId = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        return (admin, orgId);
    }
}
