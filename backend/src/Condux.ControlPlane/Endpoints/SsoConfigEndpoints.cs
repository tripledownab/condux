using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.Http;
using Condux.Core.Plans;
using Condux.Core.Secrets;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// The per-org enterprise-SSO OIDC config (#72): the org's IdP endpoints + client id + email domain, with
/// the client secret stored encrypted (<see cref="SecretBox"/>) and never read back, following the BYO-key
/// registry (<see cref="LlmConfigEndpoints"/>). Opt-in behind <see cref="SecretsConfig"/> (404 when the
/// secret store isn't configured) and gated on the plan's <c>Sso</c> feature. Reads member+; writes AND
/// deletes need OWNER. Every other org-level integration secret (the LLM key, GitHub connect, the
/// notification channels, runner tokens) settles for admin; this one is gated like <c>MemberEndpoints</c>
/// instead, for the reason given on the PUT below. It is the only writer of the org's IdP settings, so
/// that gate is the whole of them. Proving the claimed domain is <see cref="SsoDomainEndpoints"/>, which
/// writes the verification columns and nothing else.
///
/// A saved config is provisional (ADR-0043): it does not route a login until the org proves it controls
/// the domain, so the response carries the record to publish and whether the claim is proved yet.
///
/// Also serves <c>/api/auth/sso/metadata</c>, which is the one route here that is NOT org-scoped: the
/// addresses an owner registers in their IdP are deployment-wide, and they have to be readable before a
/// config exists, since that is when they are needed. It lives beside the config it is used to fill in.
/// </summary>
internal static class SsoConfigEndpoints
{
    public static void MapSsoConfigEndpoints(this IEndpointRouteBuilder app)
    {
        // What the owner registers in their IdP. Deployment-wide, so it is not org-scoped and it answers
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
                    // A domain is taken by whoever PROVED it, not by whoever saved first (ADR-0043). Several
                    // orgs may hold a provisional claim on one domain, and none of them routes a login, so
                    // squatting on a name no longer locks the real owner out of configuring SSO at all.
                    // The partial unique index is what actually enforces this; the read is here to answer
                    // the admin with a reason rather than a write failure.
                    if (await store.GetVerifiedByEmailDomainAsync(domain, http.RequestAborted) is { } other
                        && other.OrgId != orgId)
                    {
                        return TypedResults.Conflict(new ErrorResponse("email_domain_taken"));
                    }

                    // Only the chosen protocol's fields are stored — a protocol switch clears the other
                    // half rather than leaving stale credentials behind.
                    var box = http.RequestServices.GetRequiredService<SecretBox>();
                    // A fresh challenge every save, used by the statement only when the claimed domain
                    // actually changed. Minting one unconditionally keeps the caller out of a decision that
                    // has to be made against the row as it stands.
                    var challenge = DomainVerification.NewToken();
                    var stored = protocol == SsoProtocol.Saml
                        ? new StoredSsoConfig(
                            orgId, protocol, domain, request.Issuer, null, null, null, null,
                            request.SamlSsoUrl, request.SamlCertificate!.Trim(), DateTimeOffset.UtcNow,
                            challenge, null, null)
                        : new StoredSsoConfig(
                            orgId, protocol, domain, request.Issuer,
                            request.AuthorizationEndpoint, request.TokenEndpoint, request.ClientId,
                            box.Seal(request.ClientSecret!), null, null, DateTimeOffset.UtcNow,
                            challenge, null, null);
                    return TypedResults.Ok(ToResponse(await store.UpsertAsync(stored, http.RequestAborted)));
                })
            .WithName("setSsoConfig").WithTags("Auth")
            // Owner, and the reason is a privilege boundary rather than taste. Whoever writes this row
            // names the identity provider whose assertion SsoSignIn turns into a session, and that
            // session can be for any member of the org, an owner included, with no password and no
            // second factor. An admin holding it would therefore hold a way to become an owner, while
            // PATCH /api/orgs/{orgId}/members/{userId} is owner-only and InviteEndpoints refuses to
            // grant a role above the caller's own. The same authority has to be answered the same way
            // wherever it is reachable, or the strictest gate is only the slowest route.
            //
            // Delete is owner too, because turning SSO off is the same authority read backwards: a
            // federated account holds no password, so removing the config is what decides whether the
            // org's people can sign in at all. Splitting the pair would leave the next reader working
            // out which half they hold.
            // pinned by SsoConfigApiTest
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Owner));

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
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Owner));
    }

    // An SSO endpoint must be present as well as well-formed, which is this policy's difference from the
    // model provider's optional base URL. The shape check itself is shared, so the two cannot drift.
    private static bool IsHttpUrl(string? value) => HttpUrls.IsAbsoluteHttp(value);

    // The client secret is never returned — write-only, like the LLM key. The SAML certificate is the
    // IdP's public signing certificate, so it echoes back for the admin to verify. The challenge is
    // returned in full: it is published in public DNS, so there is nothing here to withhold.
    //
    // The apex is the name offered, which is the ADR's choice and the form Google and Microsoft use, so it
    // is the one an admin recognises. The challenge subdomain is accepted too and deliberately not
    // advertised, since two names in the instructions is how an admin publishes neither correctly.
    private static SsoConfigResponse ToResponse(StoredSsoConfig c) => new(
        c.EmailDomain, c.Issuer, (int)c.Protocol, c.AuthorizationEndpoint, c.TokenEndpoint, c.ClientId,
        c.SamlSsoUrl, c.SamlCertificate, c.UpdatedAt,
        DomainVerification.RecordNames(c.EmailDomain)[0],
        DomainVerification.RecordValue(c.VerificationToken), c.VerifiedAt, c.VerificationLostAt,
        // A deadline only while the claim is still routing. Once it has lapsed the same arithmetic gives a
        // date in the past, which reads as a warning about something that already happened.
        c.VerifiedAt is not null && c.VerificationLostAt is { } lostAt
            ? DomainVerification.LapsesAt(lostAt)
            : null);
}
