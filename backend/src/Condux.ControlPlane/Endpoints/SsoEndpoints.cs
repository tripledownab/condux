using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.Secrets;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Enterprise SSO sign-in (per-org, #72 + ADR-0032/0037). Email-first: the user enters their work email
/// and we route to their org's IdP by the globally-unique <c>sso_configs.email_domain</c> — an OIDC config
/// runs the same authorization-code flow as Google sign-in (shared <see cref="OidcExchange"/> +
/// <see cref="OidcFlow"/>), a SAML config redirects via <see cref="SamlSsoEndpoints"/>. Both land in the
/// shared provisioning tail (<see cref="SsoSignIn"/>). Opt-in behind <see cref="SecretsConfig"/>; when
/// unset the routes 404 and the login page hides the SSO option. Every refuse path is a redirect back to
/// login with an error code (a browser flow can't return the JSON 409s the config API uses).
/// </summary>
internal static class SsoEndpoints
{
    private const string StateCookie = "condux_sso_state";
    // Which org's IdP the in-flight login belongs to — shared with the SAML ACS.
    internal const string OrgCookie = "condux_sso_org";

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

                    // A null means the stored row is not a usable config for its protocol (the columns are
                    // nullable because the registry holds both shapes) — refuse cleanly rather than 500.
                    var idpRedirect = config.Protocol == SsoProtocol.Saml
                        ? SamlSsoEndpoints.Start(config, cfg, http)
                        : StartOidc(config, cfg, http);
                    return TypedResults.Redirect(idpRedirect ?? OidcFlow.LoginUrl(cfg, "sso_failed"));
                })
            .WithName("ssoStart").WithTags("Auth");

        // OIDC callback: verify state, exchange the code against the org's IdP, then the shared tail.
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
                    if (await store.GetAsync(orgId, http.RequestAborted) is not { } config
                        || config.Protocol != SsoProtocol.Oidc || config.ClientSecretEncrypted is null)
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

                    return TypedResults.Redirect(await SsoSignIn.CompleteAsync(
                        config, identity.Email, users, members, orgs, sessions, cfg, http));
                })
            .WithName("ssoCallback").WithTags("Auth");
    }

    private static string? StartOidc(StoredSsoConfig config, IConfiguration cfg, HttpContext http)
    {
        if (config.AuthorizationEndpoint is null || config.ClientId is null)
        {
            return null;
        }

        var state = OidcFlow.RandomToken();
        http.Response.Cookies.Append(StateCookie, state, OidcFlow.StateCookieOptions(http));
        http.Response.Cookies.Append(OrgCookie, config.OrgId.ToString(), OidcFlow.StateCookieOptions(http));

        return config.AuthorizationEndpoint
            + (config.AuthorizationEndpoint.Contains('?') ? "&" : "?")
            + "response_type=code"
            + "&client_id=" + Uri.EscapeDataString(config.ClientId)
            + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri(cfg))
            + "&scope=" + Uri.EscapeDataString("openid email")
            + "&state=" + Uri.EscapeDataString(state);
    }

    // Our own well-known OIDC callback URL, registered in each org's IdP. Absolute (the token exchange must
    // send an identical redirect_uri), built from the app base URL like invite links.
    private static string RedirectUri(IConfiguration cfg) => $"{AppUrls.BaseUrl(cfg)}/api/auth/sso/callback";
}
