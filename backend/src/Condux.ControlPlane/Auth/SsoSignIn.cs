using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// The shared tail of every enterprise-SSO callback (OIDC and SAML): assert the IdP-returned email belongs
/// to the org's configured domain (a misconfigured or hostile IdP must not inject an unrelated account),
/// link-or-create the federated user, provision them into the org under the single-org invariant
/// (<see cref="MembershipProvisioning"/>), mark onboarding done and issue the session. Returns the browser
/// redirect target — the dashboard on success, the login page with an error code on refusal.
/// </summary>
internal static class SsoSignIn
{
    public static async Task<string> CompleteAsync(
        StoredSsoConfig config, string rawEmail, UserRepository users, OrgMemberRepository members,
        OrgRepository orgs, SessionRepository sessions, IConfiguration cfg, HttpContext http)
    {
        var email = Emails.Normalize(rawEmail);
        if (!string.Equals(Emails.Domain(email), config.EmailDomain, StringComparison.OrdinalIgnoreCase))
        {
            return OidcFlow.LoginUrl(cfg, "sso_domain_mismatch");
        }

        var user = await users.GetByEmailAsync(email, http.RequestAborted)
            ?? await users.CreateFederatedAsync(email, http.RequestAborted);
        var outcome = await MembershipProvisioning.JoinSingleOrgAsync(
            members, orgs, user.Id, config.OrgId, OrgRole.Member, http.RequestAborted);
        if (outcome == JoinOutcome.BlockedSharedOrg)
        {
            return OidcFlow.LoginUrl(cfg, "already_in_org");
        }

        await users.MarkOnboardedAsync(user.Id, http.RequestAborted);
        await Sessions.IssueAsync(user, sessions, http);
        return OidcFlow.DashboardUrl(cfg);
    }
}
