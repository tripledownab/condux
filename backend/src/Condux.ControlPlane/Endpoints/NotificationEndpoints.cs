using Condux.ControlPlane.Auth;
using Condux.Core.Alerting;
using Condux.Core.Auth;
using Condux.Notifications;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Org notification channels (#129): where the platform delivers operational notices for an org — today
/// a Conductor pause when the AI-fix cost cap or allowance is reached in auto mode (#130). Org-scoped and
/// tenancy-enforced: reads need member, writes admin. Distinct from a project's alert-rule channels (#59).
/// </summary>
internal static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/orgs/{orgId:long}/notification-channels",
                async (long orgId, OrgNotificationChannelRepository channels, HttpContext http) =>
                    TypedResults.Ok((await channels.ListByOrgAsync(orgId, http.RequestAborted))
                        .Select(c => new NotificationChannelResponse(c.Id, (int)c.Channel, c.Target))
                        .ToList()))
            .WithName("listNotificationChannels").WithTags("Notifications")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        app.MapPost("/api/orgs/{orgId:long}/notification-channels",
                async Task<Results<Created<NotificationChannelResponse>, BadRequest<ErrorResponse>>> (
                    long orgId, AddNotificationChannelRequest req, OrgNotificationChannelRepository channels,
                    HttpContext http) =>
                {
                    if (!Enum.IsDefined((NotificationChannel)req.Channel) || string.IsNullOrWhiteSpace(req.Target))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_channel"));
                    }

                    var channel = await channels.AddAsync(
                        orgId, (NotificationChannel)req.Channel, req.Target.Trim(), http.RequestAborted);
                    return TypedResults.Created(
                        $"/api/orgs/{orgId}/notification-channels/{channel.Id}",
                        new NotificationChannelResponse(channel.Id, (int)channel.Channel, channel.Target));
                })
            .WithName("addNotificationChannel").WithTags("Notifications")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        app.MapDelete("/api/orgs/{orgId:long}/notification-channels/{id:guid}",
                async Task<Results<NoContent, NotFound>> (
                    long orgId, Guid id, OrgNotificationChannelRepository channels, HttpContext http) =>
                    await channels.DeleteAsync(orgId, id, http.RequestAborted)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound())
            .WithName("deleteNotificationChannel").WithTags("Notifications")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        app.MapPost("/api/orgs/{orgId:long}/notification-channels/{id:guid}/test",
                async Task<Results<Ok<TestChannelResponse>, NotFound>> (
                    long orgId, Guid id, OrgNotificationChannelRepository channels, ChannelTester tester,
                    HttpContext http) =>
                {
                    var list = await channels.ListByOrgAsync(orgId, http.RequestAborted);
                    if (list.FirstOrDefault(c => c.Id == id) is not { } channel)
                    {
                        return TypedResults.NotFound();
                    }
                    var result = await tester.SendTestAsync(
                        channel.Channel, channel.Target, http.RequestAborted);
                    return TypedResults.Ok(new TestChannelResponse(result.Delivered, result.Error));
                })
            .WithName("testNotificationChannel").WithTags("Notifications")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));
    }
}
