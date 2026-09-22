using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The admin seed (env-gated): with CONDUX_SEED_ADMIN_EMAIL/PASSWORD set, the control-plane seeds a
/// ready-to-use account at startup; with them unset it seeds nothing. Not gated on the environment,
/// because a platform-admin address cannot be signed up for and this is how that account is created
/// anywhere. Local-only, like the other auth integration tests (CI runs Category!=Integration).
/// </summary>
[Trait("Category", "Integration")]
public sealed class DevAdminSeedTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static WebApplicationFactory<Program> AppWithSeed(string postgres, string email, string password) =>
        ControlPlaneApp.Create(postgres).WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_SEED_ADMIN_EMAIL", email);
            b.UseSetting("CONDUX_SEED_ADMIN_PASSWORD", password);
        });

    [Fact]
    public async Task Seeds_an_admin_that_can_log_in_and_reseeding_is_idempotent()
    {
        var email = $"seed-{Guid.NewGuid():N}@condux.test";
        const string password = "seeded-admin-password";

        // First boot: starting the host runs the hosted seeder, so a clean client can log in.
        using (var app = AppWithSeed(pg.ConnectionString, email, password))
        {
            var login = await app.CreateClient()
                .PostAsJsonAsync("/api/auth/login", new { email, password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        // Second boot with the same creds must not fail or duplicate the account - login still works.
        using (var app = AppWithSeed(pg.ConnectionString, email, password))
        {
            var login = await app.CreateClient()
                .PostAsJsonAsync("/api/auth/login", new { email, password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
    }

    [Fact]
    public async Task The_seeded_account_is_a_platform_admin_when_its_address_is_allowlisted()
    {
        // The documented way to create a platform admin, end to end. Sign-up refuses an allowlisted
        // address, so this path is the only one left, and until now nothing proved it arrives at an
        // account that can actually open the console.
        var email = $"seed-admin-{Guid.NewGuid():N}@condux.test";
        const string password = "seeded-admin-password";
        using var app = ControlPlaneApp.Create(
                pg.ConnectionString, platformAdminEmails: email, seedPlatformAdmin: false)
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("CONDUX_SEED_ADMIN_EMAIL", email);
                b.UseSetting("CONDUX_SEED_ADMIN_PASSWORD", password);
            });
        var client = app.CreateClient();

        // Signing up as it is refused, which is what makes the seed the only way in.
        var signup = await client.PostAsJsonAsync(
            "/api/auth/signup", new { email, password = "an-attackers-password-123" });
        Assert.Equal(HttpStatusCode.Conflict, signup.StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/auth/login", new { email, password })).StatusCode);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.True(me.GetProperty("isPlatformAdmin").GetBoolean());

        // The flag is only a hint for the dashboard; the console itself is the thing that matters.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/overview")).StatusCode);
    }

    [Fact]
    public async Task Without_the_seed_env_no_account_is_created()
    {
        var email = $"noseed-{Guid.NewGuid():N}@condux.test";

        using var app = ControlPlaneApp.Create(pg.ConnectionString);
        var login = await app.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new { email, password = "whatever-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }
}
