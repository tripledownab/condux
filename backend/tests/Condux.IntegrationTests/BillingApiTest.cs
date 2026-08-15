using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The tenant billing surface (self-serve checkout + billing portal, ADR-0026). Verifies the opt-in gate
/// (routes 404 when Stripe is off) and the guard branches that reject before any Stripe call — a portal
/// request with no linked customer, a checkout for a non-purchasable tier. The happy-path Stripe calls need
/// the live API, so they are exercised in a real deploy; here the point is the guards and that the tier is
/// never written here (the webhook stays the single writer). Managed by an org admin (the owner qualifies).
/// </summary>
[Trait("Category", "Integration")]
public sealed class BillingApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly Action<IWebHostBuilder> StripeOn = b =>
    {
        b.UseSetting("CONDUX_STRIPE_SECRET_KEY", "sk_test_x");
        b.UseSetting("CONDUX_STRIPE_WEBHOOK_SECRET", "whsec_x");
        b.UseSetting("CONDUX_STRIPE_PRICE_TEAM", "price_team");
        b.UseSetting("CONDUX_STRIPE_PRICE_BUSINESS", "price_business");
    };

    [Fact]
    public async Task Checkout_and_portal_404_when_stripe_is_off_but_status_still_reads()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = ControlPlaneApp.Create(pg.ConnectionString);
        var (client, orgId) = await OwnerWithOrgAsync(app);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync($"/api/orgs/{orgId}/billing/portal", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"/api/orgs/{orgId}/billing/checkout", new { tier = "Team" })).StatusCode);

        var status = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/billing");
        Assert.False(status.GetProperty("billingEnabled").GetBoolean());
    }

    [Fact]
    public async Task Portal_and_checkout_guards_reject_before_calling_stripe()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = ControlPlaneApp.Create(pg.ConnectionString).WithWebHostBuilder(StripeOn);
        var (client, orgId) = await OwnerWithOrgAsync(app);

        var status = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/billing");
        Assert.True(status.GetProperty("billingEnabled").GetBoolean());
        Assert.Equal("Free", status.GetProperty("tier").GetString());
        Assert.False(status.GetProperty("hasSubscription").GetBoolean());
        Assert.Equal(2, status.GetProperty("purchasableTiers").GetArrayLength());

        // No linked customer -> portal refused; a non-purchasable tier -> checkout refused. Neither hits Stripe.
        await AssertBadRequest(client, $"/api/orgs/{orgId}/billing/portal", null, "no_subscription");
        await AssertBadRequest(client, $"/api/orgs/{orgId}/billing/checkout", new { tier = "Nope" }, "tier_not_purchasable");
    }

    private static async Task AssertBadRequest(HttpClient client, string url, object? body, string error)
    {
        var resp = await client.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(error, (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    // Not static: reaches pg.ConnectionString to set the tier out of band, since POST /api/orgs
    // always creates Free and only the Stripe webhook moves an org off it.
    private async Task<(HttpClient Client, long OrgId)> OwnerWithOrgAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var resp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        resp.EnsureSuccessStatusCode();
        var orgId = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 0);
        return (client, orgId);
    }
}
