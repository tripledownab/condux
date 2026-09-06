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
        SessionRepository sessions, IConfiguration cfg, HttpContext http)
    {
        var email = Emails.Normalize(rawEmail);
        if (!string.Equals(Emails.Domain(email), config.EmailDomain, StringComparison.OrdinalIgnoreCase))
        {
            return OidcFlow.LoginUrl(cfg, "sso_domain_mismatch");
        }

        // An org chooses its own identity provider and names its own email domain, and nothing yet proves
        // it owns that domain (ADR-0032 defers DNS verification, ADR-0042 records this rule). So an
        // assertion from that provider is honoured for exactly two kinds of address: one nobody holds
        // yet, which this org provisions, and one held by a member it already has. An address belonging
        // to anybody else is refused, because the org has shown no claim to it.
        //
        // The refusal has to live here rather than lean on the single-org invariant, which turns some of
        // these away as a side effect of keeping users in one org rather than by asking about consent.
        // This path also issues a full session with no second-factor challenge, deliberately, on the
        // grounds that the org's provider enforces its own. That is only sound while the account being
        // signed in belongs to the org, which is what this rule establishes.
        var user = await users.TryCreateFederatedMemberAsync(
            email, config.OrgId, OrgRole.Member, http.RequestAborted);
        if (user is null)
        {
            user = await users.GetByEmailAsync(email, http.RequestAborted);
            if (user is null
                || await members.GetRoleAsync(config.OrgId, user.Id, http.RequestAborted) is null)
            {
                return OidcFlow.LoginUrl(cfg, "sso_invite_required");
            }
        }

        await users.MarkOnboardedAsync(user.Id, http.RequestAborted);
        await Sessions.IssueAsync(user, sessions, http);
        return OidcFlow.DashboardUrl(cfg);
    }
}
