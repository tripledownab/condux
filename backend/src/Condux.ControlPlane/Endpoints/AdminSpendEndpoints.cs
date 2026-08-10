using Condux.ControlPlane.Auth;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Cross-org Conductor spend for the admin console (ADR-0027): a platform-wide rollup (total + per-model
/// + per-org), a single-org rollup for the detail page, and a per-run drill-down. All read-only, behind
/// the platform-admin gate, priced via <see cref="Condux.Core.Plans.ModelPricing"/>. The window is a
/// <c>days</c> query param (clamped 1..365, default 30), matching the per-project fix cost rollup.
/// </summary>
internal static class AdminSpendEndpoints
{
    private const int DefaultDays = 30;

    public static void MapAdminSpendEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/spend",
                async (HttpContext http, AdminSpendRepository spend, int days = DefaultDays) =>
                    TypedResults.Ok(ToResponse(await spend.RollupAsync(Since(days), null, http.RequestAborted))))
            .WithName("adminSpendRollup").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());

        app.MapGet("/api/admin/orgs/{orgId:long}/spend",
                async (long orgId, HttpContext http, AdminSpendRepository spend, int days = DefaultDays) =>
                    TypedResults.Ok(ToResponse(await spend.RollupAsync(Since(days), orgId, http.RequestAborted))))
            .WithName("adminOrgSpend").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());

        app.MapGet("/api/admin/spend/runs",
                async (HttpContext http, AdminSpendRepository spend,
                    long? orgId = null, string? kind = null, int days = DefaultDays, int limit = 200) =>
                    TypedResults.Ok((await spend.ListRunsAsync(Since(days), orgId, kind, limit, http.RequestAborted))
                        .Select(r => new AdminSpendRunResponse(
                            r.Id, r.OrgId, r.OrgName, r.Model, r.Kind, r.Status,
                            r.InputTokens, r.OutputTokens, r.CostUsd, r.CreatedAt))
                        .ToList()))
            .WithName("adminSpendRuns").WithTags("Admin")
            .RequireAuthorization().AddEndpointFilter(PlatformAdmin.RequirePlatformAdmin());
    }

    private static DateTimeOffset Since(int days) =>
        DateTimeOffset.UtcNow - TimeSpan.FromDays(Math.Clamp(days, 1, 365));

    private static AdminSpendRollupResponse ToResponse(AdminSpendRollup rollup) => new(
        rollup.TotalUsd,
        rollup.ByModel
            .Select(m => new AdminSpendModelResponse(m.Model, m.RunCount, m.InputTokens, m.OutputTokens, m.CostUsd))
            .ToList(),
        rollup.ByOrg
            .Select(o => new AdminSpendOrgResponse(
                o.OrgId, o.OrgName, o.RunCount, o.InputTokens, o.OutputTokens, o.CostUsd))
            .ToList());
}
