using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Core.FixEngine;
using Condux.Core.Orgs;
using Condux.Core.Plans;
using Condux.Core.Quotas;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>The org resource (#34), tenancy-enforced (#48): the caller's orgs, creating one and an
/// org's settings and AI-fix usage meter. Projects live in <see cref="ProjectEndpoints"/> and a
/// project's DSN keys in <see cref="DsnKeyEndpoints"/>.</summary>
internal static class ProvisioningEndpoints
{
    public static void MapProvisioningEndpoints(this IEndpointRouteBuilder app)
    {
        // The caller's orgs, with their role in each.
        app.MapGet("/api/orgs", async (HttpContext http, OrgMemberRepository members) =>
                TypedResults.Ok((await members.ListOrgsForUserAsync(OrgAuthorization.CurrentUserId(http.User)))
                    .Select(m => new OrgMembershipResponse(m.Org, OrgRoles.Name(m.Role))).ToList()))
            .WithName("listMyOrgs").WithTags("Provisioning").RequireAuthorization();

        // A user belongs to exactly one org (ADR-0018): creating another while a member is refused.
        // This is how a user gets their first one. Signup deliberately creates none, so a brand-new
        // account belongs to nothing until it comes through here or accepts an invite.
        //
        // The name is checked here because a non-nullable string on the request record is a compile-time
        // claim only: a body omitting it deserializes to null, reached Npgsql and answered 500. The slug
        // is derived from the name rather than accepted, so a caller cannot supply one at all.
        app.MapPost("/api/orgs", async Task<Results<Created<Org>, BadRequest<ErrorResponse>, Conflict<ErrorResponse>>> (
                CreateOrgRequest req, HttpContext http,
                OrgRepository orgs, OrgMemberRepository members) =>
            {
                if (!OrgNames.IsValid(req.Name))
                {
                    return TypedResults.BadRequest(new ErrorResponse("invalid_name"));
                }
                var userId = OrgAuthorization.CurrentUserId(http.User);
                if ((await members.ListOrgsForUserAsync(userId)).Count > 0)
                {
                    return TypedResults.Conflict(new ErrorResponse("single_org_limit"));
                }
                var name = req.Name.Trim();
                var org = await orgs.CreateAsync(OrgNames.ToSlug(name), name, (int)Tier.Free);
                await members.AddAsync(org.Id, userId, OrgRole.Owner);
                return TypedResults.Created($"/api/orgs/{org.Id}", org);
            })
            .WithName("createOrg").WithTags("Provisioning").RequireAuthorization();

        app.MapGet("/api/orgs/{orgId:long}",
                async Task<Results<Ok<Org>, NotFound>> (long orgId, OrgRepository orgs) =>
                    await orgs.GetAsync(orgId) is { } org ? TypedResults.Ok(org) : TypedResults.NotFound())
            .WithName("getOrg").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        // Update the org's AI-fix settings — mode (#101) and the optional cost cap (#120). Admin+.
        // Switching to auto needs a plan with AI fixes (auto would otherwise silently never fire), so a
        // Free org gets an upgrade prompt. A negative cap is rejected; null clears the cap.
        app.MapPatch("/api/orgs/{orgId:long}",
                async Task<Results<Ok<Org>, NotFound, BadRequest<ErrorResponse>, Conflict<ErrorResponse>>> (
                    long orgId, UpdateOrgRequest req, OrgRepository orgs) =>
                {
                    if (!Enum.IsDefined((AiFixMode)req.AiFixMode))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_ai_fix_mode"));
                    }
                    if (req.AiFixCostCapUsd is < 0)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_cost_cap"));
                    }
                    if (req.FixExecution is { } requested && !Enum.IsDefined((FixExecution)requested))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_fix_execution"));
                    }
                    if (await orgs.GetAsync(orgId) is not { } current)
                    {
                        return TypedResults.NotFound();
                    }
                    // Gated on AutoFix, not AiFixes: Free includes Conductor runs but stays manual, so it
                    // must still be refused here even though the tier can run fixes.
                    if ((AiFixMode)req.AiFixMode == AiFixMode.Auto
                        && !PlanCatalog.For((Tier)current.Tier).AutoFix)
                    {
                        return TypedResults.Conflict(new ErrorResponse("ai_fixes_requires_upgrade"));
                    }

                    // A customer can set a spend cap only on a BYO tier — there it's their own budget on
                    // their own key (ADR-0027). On the platform-billed tiers the cap is a Condux-owned
                    // fair-use compute ceiling (the PlanCatalog default, raised per-org only by ops via the
                    // admin console), so a customer PATCH cannot change it — keep the stored value.
                    var capUsd = PlanCatalog.For((Tier)current.Tier).ByoKey
                        ? req.AiFixCostCapUsd
                        : current.AiFixCostCapUsd;

                    // Omitted means unchanged, so a client that predates runners cannot move an org's work
                    // by not mentioning it.
                    var execution = req.FixExecution ?? current.FixExecution;

                    // Running fixes on your own machines is a self-host capability (ADR-0033 slice 4c).
                    // Refused rather than ignored: silently keeping the hosted setting would leave an org
                    // watching a runner that is never given work, with nothing saying why.
                    if ((FixExecution)execution == FixExecution.Runner
                        && !PlanCatalog.For((Tier)current.Tier).SelfHostedRunner)
                    {
                        return TypedResults.Conflict(new ErrorResponse("self_hosted_runner_requires_upgrade"));
                    }

                    return await orgs.UpdateSettingsAsync(orgId, req.AiFixMode, capUsd, execution) is { } org
                        ? TypedResults.Ok(org)
                        : TypedResults.NotFound();
                })
            .WithName("updateOrg").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        // The org's Conductor spend this calendar month against its cost cap (#120), for the settings
        // meter. Member+ — reading spend is day-to-day, like the issue list. The role filter already 404s
        // a non-member, so reaching here means the org exists.
        app.MapGet("/api/orgs/{orgId:long}/ai-fix-usage",
                async Task<Ok<AiFixUsageResponse>> (
                    long orgId, HttpContext http, OrgRepository orgs, IAiFixSpend spend, IAiFixQuota quota) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var org = await orgs.GetAsync(orgId, http.RequestAborted);
                    var monthToDate = await spend.MonthToDateUsdAsync(orgId, now, http.RequestAborted);
                    var (remaining, uncapped) = org is null
                        ? (0, false)
                        : await RemainingFixesAsync(org, quota, now, http.RequestAborted);
                    // The meter shows the EFFECTIVE ceiling: the org's own cap if set, else the tier's
                    // fair-use compute default (ADR-0020/0027) — so a Team/Business org sees its plan
                    // ceiling, not "no cap". It follows the org's execution mode, so an org running its own
                    // runner is not shown a ceiling that no longer gates it.
                    var capUsd = org is null
                        ? null
                        : AiFixBudget.EffectiveCapUsd(
                            org.AiFixCostCapUsd, PlanCatalog.For((Tier)org.Tier), (FixExecution)org.FixExecution);
                    return TypedResults.Ok(new AiFixUsageResponse(monthToDate, capUsd, remaining, uncapped));
                })
            .WithName("aiFixUsage").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));


    }

    // The runs the org can still start this period, for the Suggest-fix confirmation (#128): the tier's
    // monthly allowance (0 = Enterprise uncapped → null count), falling back to the dormant lifetime grant
    // (ADR-0035). Reads the same live counters RequestFix reserves from.
    //
    // NOTE: this is the run count only. RequestFix also refuses once month-to-date spend reaches the cost
    // cap, which this does not model, so an org can hold a non-zero count here and still be refused.
    private static async Task<(int? Remaining, bool Uncapped)> RemainingFixesAsync(
        Org org, IAiFixQuota quota, DateTimeOffset now, CancellationToken ct)
    {
        var limits = PlanCatalog.For((Tier)org.Tier);
        // Lifetime first: a tier that defines a grant
        // spends it instead of the monthly counter. Dormant today (ADR-0035).
        if (limits.AiFixesLifetime > 0)
        {
            var lifetimeUsed = await quota.GetLifetimeUsedAsync(org.Id, ct);
            return (Math.Max(0, limits.AiFixesLifetime - lifetimeUsed), false);
        }
        if (limits.UnlimitedAiFixes)
        {
            return (null, true);
        }
        var used = await quota.GetUsedAsync(org.Id, now, ct);
        return (Math.Max(0, limits.AiFixesPerMonth - used), false);
    }
}
