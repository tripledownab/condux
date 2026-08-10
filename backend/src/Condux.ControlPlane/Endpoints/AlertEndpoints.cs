using Condux.ControlPlane.Auth;
using Condux.Core.Alerting;
using Condux.Core.Auth;
using Condux.Core.Events;
using Condux.Notifications;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Alert rules management (#59): create/list/update/enable/delete a project's rules and attach, edit or remove their
/// notification channels. The consumer's alert engine (#55) is what actually fires them. Tenancy is
/// enforced (reads need member, writes need admin); every rule/channel is scoped to its project.
/// </summary>
internal static class AlertEndpoints
{
    public static void MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId:long}/alert-rules",
                async Task<Ok<IReadOnlyList<AlertRuleResponse>>> (long projectId, AlertRuleRepository alerts) =>
                {
                    var rules = await alerts.ListWithChannelsByProjectAsync(projectId);
                    return TypedResults.Ok<IReadOnlyList<AlertRuleResponse>>(
                        [.. rules.Select(r => ToResponse(r.Rule, r.Channels))]);
                })
            .WithName("listAlertRules").WithTags("Alerts")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Member));

        app.MapPost("/api/projects/{projectId:long}/alert-rules",
                async Task<Results<Created<AlertRuleResponse>, BadRequest<ErrorResponse>>> (
                    long projectId, CreateAlertRuleRequest req, AlertRuleRepository alerts) =>
                {
                    if (string.IsNullOrWhiteSpace(req.Name) || !ValidEvents(req.Events) || !ValidLevels(req.Levels))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_alert_rule"));
                    }
                    var rule = await alerts.CreateRuleAsync(
                        projectId, req.Name.Trim(), ToEvents(req.Events), ToLevels(req.Levels));
                    return TypedResults.Created(
                        $"/api/projects/{projectId}/alert-rules/{rule.Id}", ToResponse(rule, []));
                })
            .WithName("createAlertRule").WithTags("Alerts")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapPatch("/api/projects/{projectId:long}/alert-rules/{ruleId:guid}",
                async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, Guid ruleId, UpdateAlertRuleRequest req, AlertRuleRepository alerts) =>
                {
                    if (string.IsNullOrWhiteSpace(req.Name) || !ValidEvents(req.Events) || !ValidLevels(req.Levels))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_alert_rule"));
                    }
                    return await alerts.UpdateRuleAsync(
                        projectId, ruleId, req.Name.Trim(), ToEvents(req.Events),
                        ToLevels(req.Levels), req.Enabled)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound();
                })
            .WithName("updateAlertRule").WithTags("Alerts")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapDelete("/api/projects/{projectId:long}/alert-rules/{ruleId:guid}",
                async Task<Results<NoContent, NotFound>> (
                    long projectId, Guid ruleId, AlertRuleRepository alerts) =>
                    await alerts.DeleteRuleAsync(projectId, ruleId)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound())
            .WithName("deleteAlertRule").WithTags("Alerts")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapPost("/api/projects/{projectId:long}/alert-rules/{ruleId:guid}/channels",
                async Task<Results<Created<AlertChannelResponse>, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, Guid ruleId, AddAlertChannelRequest req, AlertRuleRepository alerts) =>
                {
                    if (!Enum.IsDefined((NotificationChannel)req.Channel)
                        || string.IsNullOrWhiteSpace(req.Target) || !ValidTemplate(req.Template))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_alert_channel"));
                    }
                    // Scope the rule to the project before attaching (AddChannelAsync keys only on rule id).
                    if (await alerts.GetRuleAsync(projectId, ruleId) is null)
                    {
                        return TypedResults.NotFound();
                    }
                    var channel = await alerts.AddChannelAsync(
                        ruleId, (NotificationChannel)req.Channel, req.Target.Trim(),
                        NormalizeTemplate(req.Template));
                    return TypedResults.Created(
                        $"/api/projects/{projectId}/alert-rules/{ruleId}/channels/{channel.Id}",
                        new AlertChannelResponse(channel.Id, (int)channel.Channel, channel.Target, channel.Template));
                })
            .WithName("addAlertChannel").WithTags("Alerts")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapPatch("/api/projects/{projectId:long}/alert-rules/{ruleId:guid}/channels/{channelId:guid}",
                async Task<Results<NoContent, NotFound, BadRequest<ErrorResponse>>> (
                    long projectId, Guid ruleId, Guid channelId, UpdateAlertChannelRequest req,
                    AlertRuleRepository alerts) =>
                {
                    if (!Enum.IsDefined((NotificationChannel)req.Channel)
                        || string.IsNullOrWhiteSpace(req.Target) || !ValidTemplate(req.Template))
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_alert_channel"));
                    }
                    // Scope the rule to the project before editing (UpdateChannelAsync keys only on rule id).
                    if (await alerts.GetRuleAsync(projectId, ruleId) is null)
                    {
                        return TypedResults.NotFound();
                    }
                    return await alerts.UpdateChannelAsync(
                        ruleId, channelId, (NotificationChannel)req.Channel, req.Target.Trim(),
                        NormalizeTemplate(req.Template))
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound();
                })
            .WithName("updateAlertChannel").WithTags("Alerts")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapDelete("/api/projects/{projectId:long}/alert-rules/{ruleId:guid}/channels/{channelId:guid}",
                async Task<Results<NoContent, NotFound>> (
                    long projectId, Guid ruleId, Guid channelId, AlertRuleRepository alerts) =>
                {
                    if (await alerts.GetRuleAsync(projectId, ruleId) is null)
                    {
                        return TypedResults.NotFound();
                    }
                    return await alerts.DeleteChannelAsync(ruleId, channelId)
                        ? TypedResults.NoContent()
                        : TypedResults.NotFound();
                })
            .WithName("deleteAlertChannel").WithTags("Alerts")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));

        app.MapPost("/api/projects/{projectId:long}/alert-rules/{ruleId:guid}/channels/{channelId:guid}/test",
                async Task<Results<Ok<TestChannelResponse>, NotFound>> (
                    long projectId, Guid ruleId, Guid channelId,
                    AlertRuleRepository alerts, ProjectRepository projects, ChannelTester tester,
                    HttpContext http) =>
                {
                    // Scope the rule to the project (tenancy), then find its channel by id.
                    if (await alerts.GetRuleAsync(projectId, ruleId, http.RequestAborted) is null)
                    {
                        return TypedResults.NotFound();
                    }
                    var channels = await alerts.ListChannelsByRuleAsync(ruleId, http.RequestAborted);
                    if (channels.FirstOrDefault(c => c.Id == channelId) is not { } channel)
                    {
                        return TypedResults.NotFound();
                    }
                    // The test renders as a real alert would, incl. the project's display name (never the bigint).
                    var projectName = (await projects.GetAsync(projectId, http.RequestAborted))?.Name ?? "";
                    var result = await tester.SendAlertTestAsync(channel, projectName, http.RequestAborted);
                    return TypedResults.Ok(new TestChannelResponse(result.Delivered, result.Error));
                })
            .WithName("testAlertChannel").WithTags("Alerts")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireProjectRole(OrgRole.Admin));
    }

    private static AlertRuleResponse ToResponse(AlertRule rule, IReadOnlyList<AlertChannel> channels) =>
        new(rule.Id, rule.Name, [.. rule.Events.Select(e => (int)e)],
            [.. rule.Levels.Select(level => (int)level)], rule.Enabled,
            [.. channels.Select(c => new AlertChannelResponse(c.Id, (int)c.Channel, c.Target, c.Template))]);

    // A rule must fire on at least one event, and each must be a defined event type.
    private static bool ValidEvents(IReadOnlyList<int> events) =>
        events.Count > 0 && events.All(e => Enum.IsDefined((AlertEventType)e));

    // A rule must fire on at least one level, and each must be a defined severity.
    private static bool ValidLevels(IReadOnlyList<int> levels) =>
        levels.Count > 0 && levels.All(level => Enum.IsDefined((Level)level));

    // A channel template (null = built-in default) must use only known placeholders and stay bounded.
    private static bool ValidTemplate(string? template) =>
        template is null || (template.Length <= 2000 && AlertTemplate.UnknownTokens(template).Count == 0);

    // Empty or the built-in default normalizes to null, so "restore default" clears the override.
    private static string? NormalizeTemplate(string? template) =>
        string.IsNullOrWhiteSpace(template) || template == AlertTemplate.Default ? null : template;

    private static IReadOnlyList<AlertEventType> ToEvents(IReadOnlyList<int> events) =>
        [.. events.Select(e => (AlertEventType)e)];

    private static IReadOnlyList<Level> ToLevels(IReadOnlyList<int> levels) =>
        [.. levels.Select(level => (Level)level)];
}
