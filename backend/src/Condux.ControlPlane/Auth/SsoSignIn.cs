using Condux.Core.Auth;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// The shared tail of every enterprise-SSO callback (OIDC and SAML): assert the IdP-returned email belongs
/// to the org's configured domain (a misconfigured or hostile IdP must not inject an unrelated account),
/// resolve it to a user the org is entitled to sign in, mark onboarding done and issue the session.
/// Returns the browser redirect target: the dashboard on success, the login page with an error code on
/// refusal.
/// </summary>
internal static class SsoSignIn
{
    public static async Task<string> CompleteAsync(
        StoredSsoConfig config, string rawEmail, UserRepository users, OrgMemberRepository members,
        SessionRepository sessions, EmailAllowlist platformAdmins, IConfiguration cfg, HttpContext http)
    {
        var email = Emails.Normalize(rawEmail);
        if (!string.Equals(Emails.Domain(email), config.EmailDomain, StringComparison.OrdinalIgnoreCase))
        {
            return OidcFlow.LoginUrl(cfg, "sso_domain_mismatch");
        }

        // A platform-admin address never signs in through an organisation's IdP, member or not, and is
        // never created through one. That account gets the cross-tenant console, while an IdP is chosen by
        // one tenant, speaks for its domain only as far as the domain was proved (which a deployment may
        // skip), and is trusted to have applied its own second factor. The console's login stays with the
        // platform: Google, which proves the mailbox, or a password, with this product's second factor
        // when enrolled. The reason is named only for an account the org already holds as a member; any
        // other address gets the answer every address the org has no claim to gets.
        if (platformAdmins.Contains(email))
        {
            var member = await FindMemberAsync(email, config, users, members, http) is not null;
            return OidcFlow.LoginUrl(cfg, member ? "sso_platform_admin" : "sso_invite_required");
        }

        // An org chooses its own identity provider and names its own email domain. It must now prove
        // that domain by DNS before the claim routes anything, so the domain check above is backed by
        // real evidence. Proving a domain is not consent for a particular account, though, so the rule
        // below stands: an assertion is honoured for exactly two kinds of address, one nobody holds
        // yet, which this org provisions, and one held by a member it already has. An address belonging
        // to anybody else is refused, because the org has shown no claim to that person.
        //
        // The refusal has to live here rather than lean on the single-org invariant, which turns some of
        // these away as a side effect of keeping users in one org rather than by asking about consent.
        // This path also issues a full session with no second-factor challenge, deliberately, on the
        // grounds that the org's provider enforces its own. That is only sound while the account being
        // signed in belongs to the org, which is what this rule establishes.
        var user = await users.TryCreateFederatedMemberAsync(
                email, config.OrgId, OrgRole.Member, http.RequestAborted)
            ?? await FindMemberAsync(email, config, users, members, http);
        if (user is null)
        {
            return OidcFlow.LoginUrl(cfg, "sso_invite_required");
        }

        await users.MarkOnboardedAsync(user.Id, http.RequestAborted);
        await Sessions.IssueAsync(user, sessions, http);
        return OidcFlow.DashboardUrl(cfg);
    }

    // The account holding this address, when it is a member of the org; null when nobody holds it or the
    // holder is not one of the org's members.
    private static async Task<User?> FindMemberAsync(
        string email, StoredSsoConfig config, UserRepository users, OrgMemberRepository members, HttpContext http) =>
        await users.GetByEmailAsync(email, http.RequestAborted) is { } user
            && await members.GetRoleAsync(config.OrgId, user.Id, http.RequestAborted) is not null
            ? user
            : null;
}
