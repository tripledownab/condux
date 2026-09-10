using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// HTTP-level test of the platform super-admin console (#96). The gate is an env allowlist
/// (CONDUX_PLATFORM_ADMIN_EMAILS): a listed user sees cross-tenant totals/orgs/users; everyone else
/// gets 404 (the surface is hidden, not 403), and an anonymous caller gets 401. <c>/api/auth/me</c>
/// reports the flag so the dashboard can gate its nav.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminEndpointsTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string AdminEmail = "boss@condux.test";

    private WebApplicationFactory<Program> CreateApp() =>
        ControlPlaneApp.Create(pg.ConnectionString, platformAdminEmails: AdminEmail);

    [Fact]
    public async Task Platform_admin_sees_the_console_and_everyone_else_is_hidden_from_it()
    {
        var app = CreateApp();

        // A platform admin (listed email) and an ordinary tenant user, each with their own session.
        var admin = app.CreateClient();
        await ApiAuth.SignUpAsync(admin, AdminEmail);
        var tenant = app.CreateClient();
        await ApiAuth.SignUpAsync(tenant);

        // Signup mints no org (ADR-0018), so each user creates their own for the cross-tenant counts.
        foreach (var client in new[] { admin, tenant })
        {
            await client.PostAsJsonAsync("/api/orgs",
                new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org" });
        }

        // The flag rides on /api/auth/me — true for the admin, false for the tenant.
        var adminMe = await admin.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.True(adminMe.GetProperty("isPlatformAdmin").GetBoolean());
        var tenantMe = await tenant.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.False(tenantMe.GetProperty("isPlatformAdmin").GetBoolean());

        // Overview counts at least the two orgs just created + the two signed-up users.
        var overview = await admin.GetFromJsonAsync<JsonElement>("/api/admin/overview");
        Assert.True(overview.GetProperty("orgs").GetInt64() >= 2);
        Assert.True(overview.GetProperty("users").GetInt64() >= 2);

        // The cross-tenant lists are visible to the admin.
        Assert.True((await admin.GetFromJsonAsync<JsonElement>("/api/admin/orgs")).GetArrayLength() >= 2);
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        Assert.True(users.GetArrayLength() >= 2);

        // Each user's primary org is surfaced so the console can "view as" it (ADR-0027) — the admin just
        // created an org named "Org", so their row carries it.
        var adminUser = users.EnumerateArray().First(u => u.GetProperty("email").GetString() == AdminEmail);
        Assert.NotEqual(JsonValueKind.Null, adminUser.GetProperty("orgId").ValueKind);
        Assert.Equal("Org", adminUser.GetProperty("orgName").GetString());

        // The tenant is not a platform admin → 404 on every admin route (existence hidden, not 403).
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync("/api/admin/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync("/api/admin/orgs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tenant.GetAsync("/api/admin/users")).StatusCode);

        // Anonymous → 401 (RequireAuthorization runs before the gate).
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await app.CreateClient().GetAsync("/api/admin/overview")).StatusCode);
    }
}
