using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.Secrets;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Enterprise SSO sign-in (per-org OIDC, #72). Email-first: the user enters their work email and we route
/// to their org's IdP by the globally-unique <c>sso_configs.email_domain</c>, run the same OIDC
/// authorization-code flow as Google sign-in (shared <see cref="OidcExchange"/> + <see cref="OidcFlow"/>),
/// then provision them into the org under the single-org invariant (<see cref="MembershipProvisioning"/>).
/// Opt-in behind <see cref="SecretsConfig"/> (the per-org client secret is sealed at rest); when unset the
/// routes 404 and the login page hides the SSO option. Every refuse path is a redirect back to login with an
/// error code (a browser flow can't return the JSON 409s the config API uses).
/// </summary>
internal static class SsoEndpoints
{
    private const string StateCookie = "condux_sso_state";
    private const string OrgCookie = "condux_sso_org";

    public static void MapSsoEndpoints(this IEndpointRouteBuilder app)
    {
        // Start: resolve the email's domain to an org's IdP, set the CSRF-state + org cookies, redirect.
        app.MapGet("/api/auth/sso/start",
                async Task<Results<RedirectHttpResult, NotFound>> (
                    string? email, SecretsConfig secrets, IConfiguration cfg, HttpContext http) =>
                {
                    if (!secrets.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var domain = email is null ? null : Emails.Domain(email);
                    var store = http.RequestServices.GetRequiredService<PostgresSsoConfigStore>();
                    var config = domain is null ? null : await store.GetByEmailDomainAsync(domain, http.RequestAborted);
                    if (config is null)
                    {
                        return TypedResults.Redirect(OidcFlow.LoginUrl(cfg, "sso_not_available"));
                    }

                    var state = OidcFlow.RandomToken();
                    http.Response.Cookies.Append(StateCookie, state, OidcFlow.StateCookieOptions(http));
                    http.Response.Cookies.Append(OrgCookie, config.OrgId.ToString(), OidcFlow.StateCookieOptions(http));

                    var url = config.AuthorizationEndpoint
                        + (config.AuthorizationEndpoint.Contains('?') ? "&" : "?")
                        + "response_type=code"
                        + "&client_id=" + Uri.EscapeDataString(config.ClientId)
                        + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri(cfg))
                        + "&scope=" + Uri.EscapeDataString("openid email")
                        + "&state=" + Uri.EscapeDataString(state);
                    return TypedResults.Redirect(url);
                })
            .WithName("ssoStart").WithTags("Auth");

        // Callback: verify state, exchange the code against the org's IdP, domain-check, provision + sign in.
        app.MapGet("/api/auth/sso/callback",
                async Task<Results<RedirectHttpResult, NotFound>> (
                    SecretsConfig secrets, SsoOidcClient oidc, UserRepository users,
                    OrgMemberRepository members, OrgRepository orgs, SessionRepository sessions,
                    IConfiguration cfg, HttpContext http) =>
                {
                    if (!secrets.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var expectedState = http.Request.Cookies[StateCookie];
                    var orgCookie = http.Request.Cookies[OrgCookie];
                    http.Response.Cookies.Delete(StateCookie);
                    http.Response.Cookies.Delete(OrgCookie);
                    var code = http.Request.Query["code"].ToString();

                    if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(expectedState)
                        || !OidcFlow.FixedTimeEquals(expectedState, http.Request.Query["state"].ToString())
                        || !long.TryParse(orgCookie, out var orgId))
                    {
                        return TypedResults.Redirect(OidcFlow.LoginUrl(cfg, "sso_failed"));
                    }

                    // The org cookie is unsigned, but it can't be used to cross tenants: the code is exchanged
                    // against THIS org's IdP (a code minted for another org's IdP fails the exchange) and the
                    // returned email must match this org's domain below — so a tampered orgId dead-ends.
                    var store = http.RequestServices.GetRequiredService<PostgresSsoConfigStore>();
                    var box = http.RequestServices.GetRequiredService<SecretBox>();
                    if (await store.GetAsync(orgId, http.RequestAborted) is not { } config)
                    {
                        return TypedResults.Redirect(OidcFlow.LoginUrl(cfg, "sso_failed"));
                    }

                    var identity = await oidc.ExchangeCodeAsync(
                        config, box.Open(config.ClientSecretEncrypted), RedirectUri(cfg), code,
                        DateTimeOffset.UtcNow, http.RequestAborted);
                    if (identity is null)
                    {
                        return TypedResults.Redirect(OidcFlow.LoginUrl(cfg, "sso_failed"));
                    }

                    // The IdP-verified email must belong to the org's configured domain — otherwise a
                    // misconfigured or hostile IdP could inject an unrelated account into the org.
                    var email = Emails.Normalize(identity.Email);
                    if (!string.Equals(Emails.Domain(email), config.EmailDomain, StringComparison.OrdinalIgnoreCase))
                    {
                        return TypedResults.Redirect(OidcFlow.LoginUrl(cfg, "sso_domain_mismatch"));
                    }

                    var user = await users.GetByEmailAsync(email, http.RequestAborted)
                        ?? await users.CreateFederatedAsync(email, http.RequestAborted);
                    var outcome = await MembershipProvisioning.JoinSingleOrgAsync(
                        members, orgs, user.Id, config.OrgId, OrgRole.Member, http.RequestAborted);
                    if (outcome == JoinOutcome.BlockedSharedOrg)
                    {
                        return TypedResults.Redirect(OidcFlow.LoginUrl(cfg, "already_in_org"));
                    }

                    await users.MarkOnboardedAsync(user.Id, http.RequestAborted);
                    await Sessions.IssueAsync(user, sessions, http);
                    return TypedResults.Redirect(OidcFlow.DashboardUrl(cfg));
                })
            .WithName("ssoCallback").WithTags("Auth");
    }

    // Our own well-known callback URL, registered in each org's IdP. Absolute (the token exchange must send
    // an identical redirect_uri), built from the app base URL like invite links.
    private static string RedirectUri(IConfiguration cfg) => $"{AppUrls.BaseUrl(cfg)}/api/auth/sso/callback";
}
