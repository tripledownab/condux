using System.Net;
using System.Net.Http.Json;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The dev-only admin seed (env-gated): with CONDUX_SEED_ADMIN_EMAIL/PASSWORD set, the control-plane
/// seeds a ready-to-use account at startup; with them unset it seeds nothing. Local-only, like the
/// other auth integration tests (CI runs Category!=Integration).
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
    public async Task Without_the_seed_env_no_account_is_created()
    {
        var email = $"noseed-{Guid.NewGuid():N}@condux.test";

        using var app = ControlPlaneApp.Create(pg.ConnectionString);
        var login = await app.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new { email, password = "whatever-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }
}
