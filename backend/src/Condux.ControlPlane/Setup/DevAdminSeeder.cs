using Condux.Core.Auth;
using Condux.Core.Plans;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Setup;

/// <summary>
/// Seeds a single local admin account at startup, so you can sign in without going through signup.
/// Creates the dev user (Argon2id) plus an org they own — what signup + the onboarding create-org
/// step produce (signup itself mints no org, ADR-0018). Idempotent - it skips if
/// the account already exists. Registered only when both <c>CONDUX_SEED_ADMIN_EMAIL</c> and
/// <c>CONDUX_SEED_ADMIN_PASSWORD</c> are set (dev values come from deploy/.env), so production - which
/// leaves them unset - seeds nothing. The password comes from the environment, never source.
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

            var user = await users.CreateAsync(normalized, PasswordHasher.Hash(password));
            var org = await orgs.CreateAsync(
                $"personal-{user.Id}", $"{normalized.Split('@')[0]}'s org", (int)Tier.Free);
            await members.AddAsync(org.Id, user.Id, OrgRole.Owner);
            logger.LogInformation(
                "Seeded dev admin {Email} (owner of personal org {OrgId}).", normalized, org.Id);
        }
        catch (Exception ex)
        {
            // Dev convenience only - a seed failure must never take the control-plane down.
            logger.LogWarning(ex, "Dev admin seed failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
