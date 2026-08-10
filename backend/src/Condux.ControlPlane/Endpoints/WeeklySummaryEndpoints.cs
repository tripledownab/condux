using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Contracts;
using Condux.Core.Auth;
using Condux.Core.WeeklySummaries;
using Condux.Notifications;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Weekly summary email settings + test send (ADR-0031). Org-scoped and tenancy-enforced: reads need member,
/// the schedule edit and the test send need admin. The scheduled send itself runs in the consumer's worker;
/// the test here composes the current week live and emails just the requesting admin (bypassing the ledger
/// and the skip-empty rule so it always arrives). Settings live beside the org notification channels.
/// </summary>
internal static class WeeklySummaryEndpoints
{
    public static void MapWeeklySummaryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/orgs/{orgId:long}/weekly-summary",
                async Task<Results<Ok<WeeklySummarySettingsResponse>, NotFound>> (
                    long orgId, OrgRepository orgs, HttpContext http) =>
                    await orgs.GetAsync(orgId, http.RequestAborted) is { } org
                        ? TypedResults.Ok(ToSettings(org))
                        : TypedResults.NotFound())
            .WithName("getWeeklySummarySettings").WithTags("WeeklySummary")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        app.MapPatch("/api/orgs/{orgId:long}/weekly-summary",
                async Task<Results<Ok<WeeklySummarySettingsResponse>, BadRequest<ErrorResponse>, NotFound>> (
                    long orgId, UpdateWeeklySummaryRequest req, OrgRepository orgs, HttpContext http) =>
                {
                    if (req.DayOfWeek is < 0 or > 6 || req.Hour is < 0 or > 23)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_schedule"));
                    }
                    if (!WeeklySchedule.IsKnownZone(req.Timezone))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_timezone"));
                    }

                    var updated = await orgs.UpdateWeeklySummaryAsync(
                        orgId, req.Enabled, req.DayOfWeek, req.Hour, req.Timezone.Trim(), http.RequestAborted);
                    return updated is { } org ? TypedResults.Ok(ToSettings(org)) : TypedResults.NotFound();
                })
            .WithName("updateWeeklySummarySettings").WithTags("WeeklySummary")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        app.MapPost("/api/orgs/{orgId:long}/weekly-summary/test",
                async Task<Results<Ok<WeeklySummaryTestResponse>, BadRequest<ErrorResponse>, NotFound>> (
                    long orgId, OrgRepository orgs, WeeklySummaryComposer composer, WeeklySummaryMailer mailer,
                    HttpContext http) =>
                {
                    if (await orgs.GetAsync(orgId, http.RequestAborted) is not { } org)
                    {
                        return TypedResults.NotFound();
                    }

                    var recipient = OrgAuthorization.CurrentUserEmail(http.User);
                    if (string.IsNullOrWhiteSpace(recipient))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("no_recipient"));
                    }

                    var summary = await composer.ComposeAsync(
                        org.Id, org.Name, DateTimeOffset.UtcNow, http.RequestAborted);
                    var sent = await mailer.SendAsync(summary, [recipient], http.RequestAborted);
                    return TypedResults.Ok(new WeeklySummaryTestResponse(sent > 0, recipient));
                })
            .WithName("testWeeklySummary").WithTags("WeeklySummary")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));
    }

    private static WeeklySummarySettingsResponse ToSettings(Org org) =>
        new(org.WeeklySummaryEnabled, org.WeeklySummaryDow, org.WeeklySummaryHour, org.WeeklySummaryTz);
}
