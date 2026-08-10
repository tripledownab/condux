using Condux.ControlPlane.Auth;
using Condux.Core.Auth;
using Condux.Core.FixEngine;
using Condux.Core.Plans;
using Condux.Core.Projects;
using Condux.Core.Quotas;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>Orgs, projects, and DSN keys (#34), tenancy-enforced (#48).</summary>
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
        // Signup mints the personal org, so this is reachable only for a user who left everything.
        app.MapPost("/api/orgs", async Task<Results<Created<Org>, Conflict<ErrorResponse>>> (
                CreateOrgRequest req, HttpContext http,
                OrgRepository orgs, OrgMemberRepository members) =>
            {
                var userId = OrgAuthorization.CurrentUserId(http.User);
                if ((await members.ListOrgsForUserAsync(userId)).Count > 0)
                {
                    return TypedResults.Conflict(new ErrorResponse("single_org_limit"));
                }
                var org = await orgs.CreateAsync(req.Slug, req.Name, (int)(req.Tier ?? Tier.Free));
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
                    return await orgs.UpdateSettingsAsync(orgId, req.AiFixMode, capUsd) is { } org
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
                    // ceiling, not "no cap".
                    var capUsd = org is null
                        ? null
                        : AiFixBudget.EffectiveCapUsd(org.AiFixCostCapUsd, PlanCatalog.For((Tier)org.Tier));
                    return TypedResults.Ok(new AiFixUsageResponse(monthToDate, capUsd, remaining, uncapped));
                })
            .WithName("aiFixUsage").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        // Create a project and mint its first DSN key; returns the ready-to-use DSN.
        app.MapPost("/api/orgs/{orgId:long}/projects",
                async (long orgId, CreateProjectRequest req,
                    ProjectRepository projects, DsnKeyRepository keys, IConfiguration cfg) =>
                {
                    var project = await projects.CreateAsync(orgId, req.Name, req.Platform ?? "other");
                    var key = await keys.CreateAsync(project.Id, "default", DsnKeyGenerator.NewPublicKey());
                    return TypedResults.Created($"/api/projects/{project.PublicId}",
                        new ProvisionProjectResponse(project, key, BuildDsn(cfg, key.PublicKey, project.PublicId)));
                })
            .WithName("createProject").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        app.MapGet("/api/orgs/{orgId:long}/projects", async (long orgId, ProjectRepository projects) =>
                TypedResults.Ok(await projects.ListByOrgAsync(orgId)))
            .WithName("listProjects").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        // A single project by its public UUID — the per-project detail page, reading one row instead of
        // the org list. The bigint id never appears in a URL (#125). Member+ on the owning org.
        app.MapGet("/api/projects/{projectId:guid}",
                async Task<Results<Ok<ProjectRecord>, NotFound>> (
                    Guid projectId, ProjectRepository projects) =>
                    await projects.GetByPublicIdAsync(projectId) is { } project
                        ? TypedResults.Ok(project)
                        : TypedResults.NotFound())
            .WithName("getProject").WithTags("Provisioning")
            .RequireAuthorization()
            .AddEndpointFilter(OrgAuthorization.RequireProjectRoleByPublicId(OrgRole.Member));

        // Rename a project (name + platform; the slug re-derives). Admin+ on the owning org.
        app.MapPatch("/api/projects/{projectId:guid}",
                async Task<Results<Ok<ProjectRecord>, NotFound, BadRequest<ErrorResponse>>> (
                    Guid projectId, UpdateProjectRequest req, ProjectRepository projects) =>
                {
                    if (string.IsNullOrWhiteSpace(req.Name))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_name"));
                    }
                    if (await projects.GetByPublicIdAsync(projectId) is not { } existing)
                    {
                        return TypedResults.NotFound();
                    }

                    return await projects.UpdateAsync(existing.Id, req.Name.Trim(), req.Platform ?? "other") is { } project
                        ? TypedResults.Ok(project)
                        : TypedResults.NotFound();
                })
            .WithName("updateProject").WithTags("Provisioning")
            .RequireAuthorization()
            .AddEndpointFilter(OrgAuthorization.RequireProjectRoleByPublicId(OrgRole.Admin));

        // Delete a project and everything scoped to it (DSN keys, issues, repos, alerts cascade). Admin+.
        app.MapDelete("/api/projects/{projectId:guid}",
                async Task<Results<NoContent, NotFound>> (Guid projectId, ProjectRepository projects) =>
                    await projects.GetByPublicIdAsync(projectId) is { } project
                    && await projects.DeleteAsync(project.Id)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound())
            .WithName("deleteProject").WithTags("Provisioning")
            .RequireAuthorization()
            .AddEndpointFilter(OrgAuthorization.RequireProjectRoleByPublicId(OrgRole.Admin));

        app.MapGet("/api/projects/{projectId:long}/keys", async (long projectId, DsnKeyRepository keys) =>
                TypedResults.Ok(await keys.ListByProjectAsync(projectId)))
            .WithName("listKeys").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        app.MapPost("/api/projects/{projectId:long}/keys",
                async Task<Results<Created<CreateKeyResponse>, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, CreateKeyRequest? req, DsnKeyRepository keys, ProjectRepository projects,
                    IConfiguration cfg) =>
                {
                    // A blank name falls back to "default" (the auto-minted first key), but a too-long one is
                    // rejected rather than silently truncated.
                    var trimmed = req?.Label?.Trim();
                    if (trimmed is { Length: > MaxLabelLength })
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_label"));
                    }
                    // The DSN carries the project's public UUID, so resolve it (the auth filter already
                    // confirmed the project exists for this caller).
                    if (await projects.GetAsync(projectId) is not { } project)
                    {
                        return TypedResults.NotFound();
                    }
                    var label = string.IsNullOrEmpty(trimmed) ? "default" : trimmed;
                    var key = await keys.CreateAsync(projectId, label, DsnKeyGenerator.NewPublicKey());
                    return TypedResults.Created($"/api/projects/{projectId}/keys/{key.Id}",
                        new CreateKeyResponse(key, BuildDsn(cfg, key.PublicKey, project.PublicId)));
                })
            .WithName("createKey").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        // Rename a key (label only). Admin+, tenant-scoped by project. A blank/too-long name is rejected.
        app.MapPatch("/api/projects/{projectId:long}/keys/{keyId:long}",
                async Task<Results<Ok<DsnKey>, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, long keyId, UpdateKeyRequest req, DsnKeyRepository keys) =>
                {
                    var label = req.Label?.Trim();
                    if (string.IsNullOrEmpty(label) || label.Length > MaxLabelLength)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_label"));
                    }
                    return await keys.UpdateLabelAsync(projectId, keyId, label) is { } key
                        ? TypedResults.Ok(key)
                        : TypedResults.NotFound();
                })
            .WithName("updateKey").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapPost("/api/projects/{projectId:long}/keys/{keyId:long}/revoke",
                async Task<Results<NoContent, NotFound>> (long projectId, long keyId, DsnKeyRepository keys) =>
                    await keys.RevokeAsync(projectId, keyId) ? TypedResults.NoContent() : TypedResults.NotFound())
            .WithName("revokeKey").WithTags("Provisioning")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));
    }

    // DSN key labels are cosmetic and user-supplied; cap the length so a create/rename can't store an
    // unbounded string. Matches the frontend input's maxLength.
    private const int MaxLabelLength = 100;

    // Build a client-facing DSN string from a public key + the project's public UUID (#126). The
    // sequential bigint never appears in a DSN; the relay resolves the UUID back to the numeric id.
    // internal (not private) so the DSN-construction contract is unit-tested — the ingest host/scheme come
    // from config (CONDUX_INGEST_HOST/SCHEME) so prod points DSNs at the public relay, not the dev default.
    internal static string BuildDsn(IConfiguration cfg, string publicKey, Guid publicId)
    {
        var scheme = cfg["CONDUX_INGEST_SCHEME"] ?? "http";
        var host = cfg["CONDUX_INGEST_HOST"] ?? "localhost:9010";
        return $"{scheme}://{publicKey}@{host}/{publicId}";
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
        // Lifetime first, mirroring the reservation order in RequestFix: a tier that defines a grant
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
