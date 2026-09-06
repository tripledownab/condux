using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.Plans;
using Condux.Core.Secrets;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// The per-org enterprise-SSO OIDC config (#72): the org's IdP endpoints + client id + email domain, with
/// the client secret stored encrypted (<see cref="SecretBox"/>) and never read back — mirrors the BYO-key
/// registry (<see cref="LlmConfigEndpoints"/>). Opt-in behind <see cref="SecretsConfig"/> (404 when the
/// secret store isn't configured) and gated on the plan's <c>Sso</c> feature. Reads member+, writes admin+
/// (an org-level integration secret, like the GitHub connect / LLM key).
///
/// Also serves <c>/api/auth/sso/metadata</c>, which is the one route here that is NOT org-scoped: the
/// addresses an admin registers in their IdP are deployment-wide, and they have to be readable before a
/// config exists, since that is when they are needed. It lives beside the config it is used to fill in.
/// </summary>
internal static class SsoConfigEndpoints
{
    public static void MapSsoConfigEndpoints(this IEndpointRouteBuilder app)
    {
        // What the admin registers in their IdP. Deployment-wide, so it is not org-scoped and it answers
        // before any config exists, which is exactly when it is needed. Any signed-in user may read it:
        // the values are public by nature (the entity ID rides every AuthnRequest we send), so the only
        // reason to require a session at all is to keep deployment configuration off an anonymous route.
        app.MapGet("/api/auth/sso/metadata",
                Results<Ok<SsoMetadataResponse>, NotFound> (SecretsConfig secrets, IConfiguration cfg) =>
                    secrets.Enabled
                        ? TypedResults.Ok(new SsoMetadataResponse(
                            SsoUrls.RedirectUri(cfg), SsoUrls.SamlEntityId(cfg), SsoUrls.SamlAcsUrl(cfg)))
                        : TypedResults.NotFound())
            .WithName("getSsoMetadata").WithTags("Auth").RequireAuthorization();

        app.MapGet("/api/orgs/{orgId:long}/sso-config",
                async Task<Results<Ok<SsoConfigResponse>, NotFound>> (
                    long orgId, SecretsConfig secrets, HttpContext http) =>
                {
                    if (!secrets.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var store = http.RequestServices.GetRequiredService<PostgresSsoConfigStore>();
                    var config = await store.GetAsync(orgId, http.RequestAborted);
                    return config is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(config));
                })
            .WithName("getSsoConfig").WithTags("Auth")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        app.MapPut("/api/orgs/{orgId:long}/sso-config",
                async Task<Results<Ok<SsoConfigResponse>, NotFound, BadRequest<ErrorResponse>,
                    Conflict<ErrorResponse>>> (
                    long orgId, SetSsoConfigRequest request, SecretsConfig secrets, OrgRepository orgs,
                    HttpContext http) =>
                {
                    if (!secrets.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    // SSO is a plan feature (PlanCatalog.Sso) — Business/Enterprise only.
                    var org = await orgs.GetAsync(orgId);
                    if (org is null)
                    {
                        return TypedResults.NotFound();
                    }
                    if (!PlanCatalog.For((Tier)org.Tier).Sso)
                    {
                        return TypedResults.Conflict(new ErrorResponse("sso_requires_upgrade"));
                    }

                    var domain = (request.EmailDomain ?? "").Trim().TrimStart('@').ToLowerInvariant();
                    var protocol = (SsoProtocol)request.Protocol;
                    // Per protocol: OIDC needs absolute http(s) endpoints (we build the authorize redirect
                    // from one and POST the code exchange to the other) + the client credentials; SAML needs
                    // the SSO URL + a parseable IdP signing certificate (issuer holds the IdP entity ID).
                    var valid = domain.Length > 0 && domain.Contains('.')
                        && !string.IsNullOrWhiteSpace(request.Issuer)
                        && protocol switch
                        {
                            SsoProtocol.Oidc => IsHttpUrl(request.AuthorizationEndpoint)
                                && IsHttpUrl(request.TokenEndpoint)
                                && !string.IsNullOrWhiteSpace(request.ClientId)
                                && !string.IsNullOrWhiteSpace(request.ClientSecret),
                            SsoProtocol.Saml => IsHttpUrl(request.SamlSsoUrl)
                                && !string.IsNullOrWhiteSpace(request.SamlCertificate)
                                && SamlSso.TryLoadCertificate(request.SamlCertificate) is not null,
                            _ => false,
                        };
                    if (!valid)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_request"));
                    }

                    var store = http.RequestServices.GetRequiredService<PostgresSsoConfigStore>();
                    // email_domain is globally unique (the login routing key) — refuse if another org owns it.
                    if (await store.GetByEmailDomainAsync(domain, http.RequestAborted) is { } other
                        && other.OrgId != orgId)
                    {
                        return TypedResults.Conflict(new ErrorResponse("email_domain_taken"));
                    }

                    // Only the chosen protocol's fields are stored — a protocol switch clears the other
                    // half rather than leaving stale credentials behind.
                    var box = http.RequestServices.GetRequiredService<SecretBox>();
                    var stored = protocol == SsoProtocol.Saml
                        ? new StoredSsoConfig(
                            orgId, protocol, domain, request.Issuer, null, null, null, null,
                            request.SamlSsoUrl, request.SamlCertificate!.Trim(), DateTimeOffset.UtcNow)
                        : new StoredSsoConfig(
                            orgId, protocol, domain, request.Issuer,
                            request.AuthorizationEndpoint, request.TokenEndpoint, request.ClientId,
                            box.Seal(request.ClientSecret!), null, null, DateTimeOffset.UtcNow);
                    await store.UpsertAsync(stored, http.RequestAborted);
                    return TypedResults.Ok(ToResponse(stored));
                })
            .WithName("setSsoConfig").WithTags("Auth")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        app.MapDelete("/api/orgs/{orgId:long}/sso-config",
                async Task<Results<NoContent, NotFound>> (
                    long orgId, SecretsConfig secrets, HttpContext http) =>
                {
                    if (!secrets.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var store = http.RequestServices.GetRequiredService<PostgresSsoConfigStore>();
                    return await store.DeleteAsync(orgId, http.RequestAborted)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound();
                })
            .WithName("deleteSsoConfig").WithTags("Auth")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    // The client secret is never returned — write-only, like the LLM key. The SAML certificate is the
    // IdP's public signing certificate, so it echoes back for the admin to verify.
    private static SsoConfigResponse ToResponse(StoredSsoConfig c) => new(
        c.EmailDomain, c.Issuer, (int)c.Protocol, c.AuthorizationEndpoint, c.TokenEndpoint, c.ClientId,
        c.SamlSsoUrl, c.SamlCertificate, c.UpdatedAt);
}
