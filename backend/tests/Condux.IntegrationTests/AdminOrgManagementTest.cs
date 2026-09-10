using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Auth;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The admin management console (ADR-0027): a platform admin can view and edit any org and manage its
/// members cross-tenant, everyone else is hidden (404), and every mutation lands an audit row. Also pins
/// the invariant that admin org edits never touch the tier (that stays Stripe/webhook owned).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminOrgManagementTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    // Tests in a class share one Postgres container, so each mints a unique admin email (signup is
    // one-per-email) and puts that same email on the platform-admin allowlist.
    private static string NewAdminEmail() => $"boss-{Guid.NewGuid():N}@condux.test";

    [Fact]
    public async Task Non_admins_and_anon_are_hidden_from_the_management_routes()
    {
        var app = ControlPlaneApp.Create(pg.ConnectionString, platformAdminEmails: NewAdminEmail());

        var tenant = app.CreateClient();
        await ApiAuth.SignUpAsync(tenant);
        var orgId = await CreateOrgAsync(tenant);

        // A tenant (not a platform admin) gets 404 on every new admin route — existence hidden, not 403.
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync($"/api/admin/orgs/{orgId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync($"/api/admin/orgs/{orgId}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync("/api/admin/spend")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync("/api/admin/audit")).StatusCode);

        // Anonymous → 401 (RequireAuthorization runs before the gate).
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.CreateClient().GetAsync($"/api/admin/orgs/{orgId}")).StatusCode);
    }

    [Fact]
    public async Task Admin_edits_an_org_without_touching_the_tier_and_it_is_audited()
    {
        var adminEmail = NewAdminEmail();
        var app = ControlPlaneApp.Create(pg.ConnectionString, platformAdminEmails: adminEmail);

        var admin = app.CreateClient();
        await ApiAuth.SignUpAsync(admin, adminEmail);
        var tenant = app.CreateClient();
        await ApiAuth.SignUpAsync(tenant);
        var orgId = await CreateOrgAsync(tenant, tier: 1); // Team, so auto-fix is allowed

        var patch = await admin.PatchAsJsonAsync($"/api/admin/orgs/{orgId}",
            new { name = "Renamed Co", aiFixMode = 1, aiFixCostCapUsd = 42.5m });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var updated = await patch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Renamed Co", updated.GetProperty("name").GetString());
        Assert.Equal(1, updated.GetProperty("aiFixMode").GetInt32());
        Assert.Equal(1, updated.GetProperty("tier").GetInt32()); // tier untouched by the admin edit

        // The detail endpoint reflects the edit.
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/orgs/{orgId}");
        Assert.Equal("Renamed Co", detail.GetProperty("name").GetString());
        Assert.Equal(42.5m, detail.GetProperty("aiFixCostCapUsd").GetDecimal());

        // Both the rename and the settings change were audited against the org.
        var audit = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/audit?orgId={orgId}");
        var actions = audit.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("org.rename", actions);
        Assert.Contains("org.settings", actions);
        Assert.All(audit.EnumerateArray(), e => Assert.Equal(adminEmail, e.GetProperty("actorEmail").GetString()));
    }

    [Fact]
    public async Task Admin_manages_members_cross_tenant_and_cannot_strand_the_last_owner()
    {
        var adminEmail = NewAdminEmail();
        var app = ControlPlaneApp.Create(pg.ConnectionString, platformAdminEmails: adminEmail);

        var admin = app.CreateClient();
        await ApiAuth.SignUpAsync(admin, adminEmail);
        var tenant = app.CreateClient();
        var owner = await ApiAuth.SignUpAsync(tenant);
        var orgId = await CreateOrgAsync(tenant);

        // Seed a second member directly so the admin has someone to act on.
        var member = await ApiAuth.SignUpAsync(app.CreateClient());
        await new OrgMemberRepository(pg.ConnectionString).AddAsync(orgId, member.UserId, OrgRole.Member);

        // Promote the member to admin (cross-tenant), then confirm + audit.
        var promote = await admin.PatchAsJsonAsync(
            $"/api/admin/orgs/{orgId}/members/{member.UserId}", new { role = "admin" });
        Assert.Equal(HttpStatusCode.NoContent, promote.StatusCode);
        var members = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/orgs/{orgId}/members");
        Assert.Equal("admin", members.EnumerateArray()
            .First(m => m.GetProperty("userId").GetInt64() == member.UserId).GetProperty("role").GetString());

        // Demoting the sole owner is refused (409 last_owner).
        var demote = await admin.PatchAsJsonAsync(
            $"/api/admin/orgs/{orgId}/members/{owner.UserId}", new { role = "member" });
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);

        // Removing the added member succeeds and is audited.
        var remove = await admin.DeleteAsync($"/api/admin/orgs/{orgId}/members/{member.UserId}");
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        var audit = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/audit?orgId={orgId}");
        var actions = audit.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("member.role", actions);
        Assert.Contains("member.remove", actions);
    }

    [Fact]
    public async Task Admin_reads_the_unfiltered_audit_log()
    {
        var adminEmail = NewAdminEmail();
        var app = ControlPlaneApp.Create(pg.ConnectionString, platformAdminEmails: adminEmail);

        var admin = app.CreateClient();
        await ApiAuth.SignUpAsync(admin, adminEmail);
        var tenant = app.CreateClient();
        await ApiAuth.SignUpAsync(tenant);
        var orgId = await CreateOrgAsync(tenant, tier: 1);

        // An admin edit lands audit rows to read back.
        var patch = await admin.PatchAsJsonAsync($"/api/admin/orgs/{orgId}",
            new { name = "Audited Co", aiFixMode = 1, aiFixCostCapUsd = 10m });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        // The unfiltered audit list (no org filter) must return, not 500: a null org filter binds as a typed
        // bigint parameter, so Postgres can resolve it under "@org IS NULL" instead of failing with 42P08.
        var audit = await admin.GetAsync("/api/admin/audit");
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        var entries = await audit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("org.rename",
            entries.EnumerateArray().Select(e => e.GetProperty("action").GetString()));
    }

    // Not static: needs the connection string to set the tier out of band, since POST /api/orgs always
    // creates Free and only the Stripe webhook moves an org off it.
    private async Task<long> CreateOrgAsync(HttpClient client, int tier = 0)
    {
        var resp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        resp.EnsureSuccessStatusCode();
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, id, tier);
        return id;
    }
}
