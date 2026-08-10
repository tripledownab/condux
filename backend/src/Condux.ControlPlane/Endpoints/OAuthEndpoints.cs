using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.Secrets;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// "Sign in with Google" (OIDC authorization-code flow, #71). Opt-in behind <see cref="GoogleOAuthConfig"/>;
/// when unset the routes 404. <c>/start</c> sends the browser to Google with an unguessable state kept in
/// a short-lived cookie (a double-submit CSRF defense — no server-side state table). <c>/callback</c>
/// verifies the state, exchanges the code for the user's Google-verified email, finds-or-creates the local
/// user (federated accounts carry no password) and issues the same first-party session cookie as password
/// login. <c>/providers</c> is the public flag the login page reads to decide which sign-in options to show.
/// The CSRF-state + redirect mechanics are the shared <see cref="OidcFlow"/> (also used by enterprise SSO).
/// </summary>
internal static class OAuthEndpoints
{
    private const string StateCookie = "condux_oauth_state";

    public static void MapOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Public: lets the (unauthenticated) login page know which sign-in options to render. SSO is
        // available when the secret store is configured (the per-org client secret is sealed at rest).
        app.MapGet("/api/auth/providers",
                (GoogleOAuthConfig google, SecretsConfig secrets) =>
                    TypedResults.Ok(new AuthProvidersResponse(google.Enabled, secrets.Enabled)))
            .WithName("authProviders").WithTags("Auth");

        // Kick off the flow: set the state cookie, redirect to Google's consent screen.
        app.MapGet("/api/auth/oauth/google/start",
                Results<RedirectHttpResult, NotFound> (GoogleOAuthConfig config, HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var state = OidcFlow.RandomToken();
                    http.Response.Cookies.Append(StateCookie, state, OidcFlow.StateCookieOptions(http));

                    var url = "https://accounts.google.com/o/oauth2/v2/auth"
                        + "?response_type=code"
                        + "&client_id=" + Uri.EscapeDataString(config.ClientId!)
                        + "&redirect_uri=" + Uri.EscapeDataString(config.RedirectUri!)
                        + "&scope=" + Uri.EscapeDataString("openid email profile")
                        + "&state=" + Uri.EscapeDataString(state)
                        + "&prompt=select_account";
                    return TypedResults.Redirect(url);
                })
            .WithName("googleOAuthStart").WithTags("Auth");

        // Google redirects the browser back here with ?code&state. Verify, exchange, sign in.
        app.MapGet("/api/auth/oauth/google/callback",
                async Task<Results<RedirectHttpResult, NotFound>> (
                    GoogleOAuthConfig config, GoogleOidcClient oidc, UserRepository users,
                    SessionRepository sessions, IConfiguration cfg, HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var expectedState = http.Request.Cookies[StateCookie];
                    http.Response.Cookies.Delete(StateCookie);
                    var code = http.Request.Query["code"].ToString();

                    if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(expectedState)
                        || !OidcFlow.FixedTimeEquals(expectedState, http.Request.Query["state"].ToString()))
                    {
                        return TypedResults.Redirect(OidcFlow.LoginUrl(cfg, "oauth_failed"));
                    }

                    var identity = await oidc.ExchangeCodeAsync(code, DateTimeOffset.UtcNow, http.RequestAborted);
                    if (identity is null)
                    {
                        return TypedResults.Redirect(OidcFlow.LoginUrl(cfg, "oauth_failed"));
                    }

                    // Link by Google-verified email: an existing (password or federated) account with that
                    // email signs in, otherwise a new federated account is created. ADR-0018: no org here —
                    // a new user picks up their tenant in onboarding or by accepting an invite.
                    var email = Emails.Normalize(identity.Email);
                    var user = await users.GetByEmailAsync(email, http.RequestAborted)
                        ?? await users.CreateFederatedAsync(email, http.RequestAborted);
                    await Sessions.IssueAsync(user, sessions, http);
                    return TypedResults.Redirect(OidcFlow.DashboardUrl(cfg));
                })
            .WithName("googleOAuthCallback").WithTags("Auth");
    }
}
