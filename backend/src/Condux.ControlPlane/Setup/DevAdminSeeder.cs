using Condux.Core.Auth;
using Condux.Core.Plans;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Setup;

// The optional admin seed: the hosted service that creates the account, and its registration.

/// <summary>
/// Seeds a single admin account at startup, so you can sign in without going through signup: a local
/// stack's login, and the way to create a platform-admin account anywhere without an identity provider,
/// since signup refuses that address. Creates the user (Argon2id) plus an org they own, which is what signup and the
/// onboarding create-org step produce (signup itself mints no org, ADR-0018). Idempotent - it skips if
/// the account already exists. Registered only when both <c>CONDUX_SEED_ADMIN_EMAIL</c> and
/// <c>CONDUX_SEED_ADMIN_PASSWORD</c> are set (see <see cref="DevAdminSeederRegistration"/>). The
/// password comes from the environment, never source.
/// </summary>
internal sealed class DevAdminSeeder(
    string email,
    string password,
    UserRepository users,
    OrgRepository orgs,
    OrgMemberRepository members,
    ILogger<DevAdminSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!Emails.IsValid(email) || password.Length < 8)
            {
                logger.LogWarning(
                    "Dev admin seed skipped: CONDUX_SEED_ADMIN_EMAIL/PASSWORD are missing or invalid.");
                return;
            }

            var normalized = Emails.Normalize(email);
            if (await users.GetByEmailAsync(normalized) is not null)
            {
                logger.LogInformation("Dev admin {Email} already exists; not reseeding.", normalized);
                return;
            }

            // Null means another replica seeded it between the lookup above and here, which is the
            // same outcome as finding it already there: nothing to do.
            if (await users.TryCreateAsync(normalized, PasswordHasher.Hash(password)) is not { } user)
            {
                logger.LogInformation("Admin {Email} was seeded concurrently; not reseeding.", normalized);
                return;
            }

            var org = await orgs.CreateAsync(
                $"personal-{user.Id}", $"{normalized.Split('@')[0]}'s org", (int)Tier.Free);
            await members.AddAsync(org.Id, user.Id, OrgRole.Owner);
            logger.LogInformation(
                "Seeded dev admin {Email} (owner of personal org {OrgId}).", normalized, org.Id);
        }
        catch (Exception ex)
        {
            // Best effort - a seed failure must never take the control-plane down; the log says it failed.
            logger.LogWarning(ex, "Admin seed failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Registers <see cref="DevAdminSeeder"/> when both seed variables are set.</summary>
internal static class DevAdminSeederRegistration
{
    public static IServiceCollection AddAdminSeed(this IServiceCollection services, IConfiguration cfg)
    {
        // Registered only when both env vars are set, so a local stack gets a ready-to-use login and a
        // deployment that leaves them unset seeds nothing. Gated on the two values and NOT on the
        // environment, deliberately: signup refuses a platform-admin address, so this is how that account
        // is created without an identity provider, production included.
        var seedEmail = cfg["CONDUX_SEED_ADMIN_EMAIL"];
        var seedPassword = cfg["CONDUX_SEED_ADMIN_PASSWORD"];
        if (!string.IsNullOrEmpty(seedEmail) && !string.IsNullOrEmpty(seedPassword))
        {
            services.AddHostedService(sp => new DevAdminSeeder(
                seedEmail, seedPassword,
                sp.GetRequiredService<UserRepository>(),
                sp.GetRequiredService<OrgRepository>(),
                sp.GetRequiredService<OrgMemberRepository>(),
                sp.GetRequiredService<ILogger<DevAdminSeeder>>()));
        }

        return services;
    }
}
