using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.Secrets;
using Condux.Storage.Postgres;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.MvcCore;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// The SAML half of enterprise SSO (ADR-0037): the outbound redirect-binding AuthnRequest (built for
/// <see cref="SsoEndpoints"/>'s email-first start) and the Assertion Consumer Service the IdP posts the
/// signed response back to. The library validates signature/audience/conditions against the org's pinned
/// certificate (<see cref="SamlSso"/>); the endpoint adds the browser binding — RelayState must equal the
/// state cookie and InResponseTo the request-id cookie — so an unsolicited (IdP-initiated) or replayed
/// response is refused. SP-initiated only, by design.
/// </summary>
internal static class SamlSsoEndpoints
{
    // The SAML flow needs its own cookies because the ACS is a cross-site POST, which SameSite=Lax
    // cookies do not ride (the OIDC callback is a top-level GET, where Lax is enough).
    private const string StateCookie = "condux_saml_state";
    private const string RequestCookie = "condux_saml_req";

    /// <summary>Build the IdP redirect for an org's SAML config and set the binding cookies.
    /// Null when the stored row is not a usable SAML config (the caller bounces to login).</summary>
    public static string? Start(StoredSsoConfig config, IConfiguration cfg, HttpContext http)
    {
        if (config.SamlSsoUrl is null || config.SamlCertificate is null)
        {
            return null;
        }

        var state = OidcFlow.RandomToken();
        var binding = new Saml2RedirectBinding { RelayState = state };
        var request = new Saml2AuthnRequest(SamlSso.Configuration(config, cfg));
        binding.Bind(request);

        var cookieOptions = OidcFlow.StateCookieOptions(http, crossSite: true);
        http.Response.Cookies.Append(StateCookie, state, cookieOptions);
        http.Response.Cookies.Append(RequestCookie, request.IdAsString, cookieOptions);
        http.Response.Cookies.Append(SsoEndpoints.OrgCookie, config.OrgId.ToString(), cookieOptions);
        return binding.RedirectLocation.OriginalString;
    }

    public static void MapSamlSsoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/sso/saml/acs",
                async Task<Results<RedirectHttpResult, NotFound>> (
                    SecretsConfig secrets, UserRepository users, OrgMemberRepository members,
                    SessionRepository sessions, IConfiguration cfg,
                    ILoggerFactory loggerFactory, HttpContext http) =>
                {
                    if (!secrets.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var expectedState = http.Request.Cookies[StateCookie];
                    var expectedRequestId = http.Request.Cookies[RequestCookie];
                    var orgCookie = http.Request.Cookies[SsoEndpoints.OrgCookie];
                    http.Response.Cookies.Delete(StateCookie);
                    http.Response.Cookies.Delete(RequestCookie);
                    http.Response.Cookies.Delete(SsoEndpoints.OrgCookie);

                    var store = http.RequestServices.GetRequiredService<PostgresSsoConfigStore>();
                    if (string.IsNullOrEmpty(expectedState) || string.IsNullOrEmpty(expectedRequestId)
                        || !long.TryParse(orgCookie, out var orgId)
                        || await store.GetAsync(orgId, http.RequestAborted) is not { } config
                        || config.Protocol != SsoProtocol.Saml
                        || config.SamlSsoUrl is null || config.SamlCertificate is null)
                    {
                        return TypedResults.Redirect(OidcFlow.LoginUrl(cfg, "sso_failed"));
                    }

                    // The org cookie is unsigned like the OIDC flow's: a tampered orgId dead-ends because the
                    // response signature is validated against THIS org's pinned certificate and the asserted
                    // email must match this org's domain in the shared tail.
                    Saml2AuthnResponse response;
                    try
                    {
                        var genericRequest = http.Request.ToGenericHttpRequest(validate: true);
                        response = new Saml2AuthnResponse(SamlSso.Configuration(config, cfg));
                        genericRequest.Binding.ReadSamlResponse(genericRequest, response);
                        if (response.Status != Saml2StatusCodes.Success)
                        {
                            return TypedResults.Redirect(OidcFlow.LoginUrl(cfg, "sso_failed"));
                        }
                        genericRequest.Binding.Unbind(genericRequest, response);

                        if (!OidcFlow.FixedTimeEquals(expectedState, genericRequest.Binding.RelayState ?? "")
                            || !OidcFlow.FixedTimeEquals(expectedRequestId, response.InResponseToAsString ?? ""))
                        {
                            return TypedResults.Redirect(OidcFlow.LoginUrl(cfg, "sso_failed"));
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Signature/audience/conditions failures all surface as library exceptions.
                        loggerFactory.CreateLogger(nameof(SamlSsoEndpoints))
                            .LogWarning(ex, "SAML response rejected org={OrgId}", orgId);
                        return TypedResults.Redirect(OidcFlow.LoginUrl(cfg, "sso_failed"));
                    }

                    var email = SamlSso.Email(response.ClaimsIdentity);
                    if (email is null)
                    {
                        return TypedResults.Redirect(OidcFlow.LoginUrl(cfg, "sso_failed"));
                    }

                    return TypedResults.Redirect(await SsoSignIn.CompleteAsync(
                        config, email, users, members, sessions, cfg, http));
                })
            .WithName("ssoSamlAcs").WithTags("Auth").ExcludeFromDescription();
    }
}
