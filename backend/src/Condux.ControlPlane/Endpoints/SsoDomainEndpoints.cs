using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.Plans;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Proving that an org controls the domain its SSO config claims (ADR-0043). Separate from
/// <see cref="SsoConfigEndpoints"/>, which registers the IdP: naming a domain and proving one are
/// different acts, and the org does them minutes or days apart.
///
/// Owner, matching the config write. The ADR proposed admin+, and that is the one detail the tree
/// overtook: writing the config became owner-only because whoever holds it names the provider whose
/// assertion becomes a session. Verification is the switch that makes a claim route, so admin here would
/// be a second way to the same authority, and the strictest gate would only be the slowest route.
/// </summary>
internal static class SsoDomainEndpoints
{
    public static void MapSsoDomainEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/orgs/{orgId:long}/sso-config/verify",
                async Task<Results<Ok<VerifySsoDomainResponse>, NotFound, Conflict<ErrorResponse>>> (
                    long orgId, SecretsConfig secrets, OrgRepository orgs, HttpContext http) =>
                {
                    if (!secrets.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    // The same plan gate the config write has. A config row outlives a downgrade, so
                    // without this an org that has dropped off an SSO tier could newly switch on the
                    // routing for a claim it saved while entitled. The BYO-key model list was the same
                    // shape: a second route on one resource that skipped the gate its sibling applied.
                    var org = await orgs.GetAsync(orgId);
                    if (org is null)
                    {
                        return TypedResults.NotFound();
                    }
                    if (!PlanCatalog.For((Tier)org.Tier).Sso)
                    {
                        return TypedResults.Conflict(new ErrorResponse("sso_requires_upgrade"));
                    }

                    // Resolved from the request rather than taken as a parameter, like the store beside it:
                    // both register only when the secret store is configured, and minimal APIs bind an
                    // unregistered class as the request body. That would answer 400 to a caller this route
                    // owes a 404, and it put the type in the published OpenAPI document as a schema.
                    var verifier = http.RequestServices.GetRequiredService<SsoDomainVerifier>();
                    var store = http.RequestServices.GetRequiredService<PostgresSsoConfigStore>();
                    if (await store.GetAsync(orgId, http.RequestAborted) is not { } config)
                    {
                        return TypedResults.NotFound();
                    }

                    var outcome = await verifier.CheckAsync(config, http.RequestAborted);
                    if (outcome != DomainCheckOutcome.Verified)
                    {
                        // Not an error: we asked and got an answer. The org's record is not published yet,
                        // or we could not reach a resolver, and the two are reported apart so an admin is
                        // never told their DNS is wrong during an outage of ours.
                        return TypedResults.Ok(new VerifySsoDomainResponse(Name(outcome), null));
                    }

                    DateTimeOffset? verifiedAt;
                    try
                    {
                        // The domain the check just proved, not the one on the row now: if the config moved
                        // between the two, no row matches and the claim is simply not stamped.
                        verifiedAt = await store.MarkVerifiedAsync(
                            orgId, config.EmailDomain, http.RequestAborted);
                    }
                    catch (DomainAlreadyVerifiedException)
                    {
                        return TypedResults.Conflict(new ErrorResponse("email_domain_taken"));
                    }

                    return verifiedAt is null
                        ? TypedResults.Conflict(new ErrorResponse("sso_config_changed"))
                        : TypedResults.Ok(
                            new VerifySsoDomainResponse(Name(DomainCheckOutcome.Verified), verifiedAt));
                })
            .WithName("verifySsoDomain").WithTags("Auth")
            // pinned by SsoDomainVerificationApiTest
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Owner));
    }

    // Wire names for the outcome, so the dashboard keys on a stable string rather than an enum ordinal
    // that would silently re-map if a case were ever inserted. Exhaustive rather than defaulted: a new
    // outcome must be given its own name here, instead of being reported as a resolver failure.
    private static string Name(DomainCheckOutcome outcome) => outcome switch
    {
        DomainCheckOutcome.Verified => "verified",
        DomainCheckOutcome.RecordMissing => "record_missing",
        DomainCheckOutcome.ResolverUnavailable => "resolver_unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}
