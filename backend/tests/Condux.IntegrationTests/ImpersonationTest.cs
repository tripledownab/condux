using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Read-only "view as org" impersonation (ADR-0027). A platform admin can start a scoped, read-only
/// session over one org: GETs to that org's tenant surface succeed, every write is refused, /api/auth/me
/// still reports the admin's real identity plus the impersonated org, a different org stays hidden, and
/// stop clears it. Non-admins cannot start, and with no signing key configured the capability is off.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ImpersonationTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string SigningKey = "test-impersonation-signing-key";

    // Tests share one Postgres container, so each mints a unique admin email (see AdminOrgManagementTest).
    private static string NewAdminEmail() => $"boss-{Guid.NewGuid():N}@condux.test";

    [Fact]
    public async Task View_as_org_is_read_only_scoped_and_reversible()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var adminEmail = NewAdminEmail();
        var app = ControlPlaneApp.Create(
            pg.ConnectionString, platformAdminEmails: adminEmail, impersonationSigningKey: SigningKey);

        var admin = app.CreateClient();
        await ApiAuth.SignUpAsync(admin, adminEmail);
        var tenantA = app.CreateClient();
        await ApiAuth.SignUpAsync(tenantA);
        var targetOrg = await CreateOrgAsync(tenantA);
        var tenantB = app.CreateClient();
        await ApiAuth.SignUpAsync(tenantB);
        var otherOrg = await CreateOrgAsync(tenantB);

        // Before impersonation the admin is not a member, so a tenant read is hidden.
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/orgs/{targetOrg}/members")).StatusCode);

        // Start impersonating the target org.
        var start = await admin.PostAsync($"/api/admin/impersonation/{targetOrg}", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal(targetOrg, (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orgId").GetInt64());

        // Now a GET of the target org's tenant surface succeeds (scoped read grant).
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/orgs/{targetOrg}/members")).StatusCode);
        // But a different org stays hidden — the grant is scoped to the impersonated org only.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/orgs/{otherOrg}/members")).StatusCode);

        // Every write is refused while viewing (this blocks the fix POST too, so no allowance can burn).
        var write = await admin.PatchAsJsonAsync(
            $"/api/orgs/{targetOrg}", new { aiFixMode = 0, aiFixCostCapUsd = (decimal?)null });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal("impersonation_read_only",
            (await write.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // /api/auth/me carries the impersonation target, yet the identity stays the real admin.
        var me = await admin.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(adminEmail, me.GetProperty("email").GetString());
        Assert.True(me.GetProperty("isPlatformAdmin").GetBoolean());
        Assert.Equal(targetOrg, me.GetProperty("impersonation").GetProperty("orgId").GetInt64());

        // Stop works despite the read-only block (allowlisted) and clears the session.
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsync("/api/admin/impersonation/stop", null)).StatusCode);
        var after = await admin.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("impersonation").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/orgs/{targetOrg}/members")).StatusCode);

        // Start + stop were audited against the real admin.
        var audit = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/audit?orgId={targetOrg}");
        var actions = audit.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("impersonation.start", actions);
        Assert.Contains("impersonation.stop", actions);
    }

    [Fact]
    public async Task Non_admins_cannot_start_and_the_capability_is_off_without_a_signing_key()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);

        // A tenant (non-admin) is hidden from the start route even with the feature enabled.
        var enabled = ControlPlaneApp.Create(
            pg.ConnectionString, platformAdminEmails: NewAdminEmail(), impersonationSigningKey: SigningKey);
        var tenant = enabled.CreateClient();
        await ApiAuth.SignUpAsync(tenant);
        var orgId = await CreateOrgAsync(tenant);
        Assert.Equal(HttpStatusCode.NotFound,
            (await tenant.PostAsync($"/api/admin/impersonation/{orgId}", null)).StatusCode);

        // With no signing key, even the platform admin gets 404 (capability off).
        var offAdminEmail = NewAdminEmail();
        var off = ControlPlaneApp.Create(pg.ConnectionString, platformAdminEmails: offAdminEmail);
        var admin = off.CreateClient();
        await ApiAuth.SignUpAsync(admin, offAdminEmail);
        var adminOrg = await CreateOrgAsync(admin);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsync($"/api/admin/impersonation/{adminOrg}", null)).StatusCode);
    }

    private static async Task<long> CreateOrgAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }
}
