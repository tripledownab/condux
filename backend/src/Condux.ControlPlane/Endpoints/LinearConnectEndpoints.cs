using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Contracts;
using Condux.ControlPlane.Setup;
using Condux.ControlPlane.Tickets;
using Condux.Core.Auth;
using Condux.Core.Secrets;
using Condux.Core.Tickets;
using Condux.Linear;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Connecting an org to a Linear workspace (ADR-0053 decisions 3 and 4): one grant per org, started from a
/// project's Linear tab and returning there. Every route answers 404 while Linear is not configured, so a
/// deployment without it shows no Linear surface at all.
/// </summary>
internal static class LinearConnectEndpoints
{
    public static void MapLinearConnectEndpoints(this IEndpointRouteBuilder app)
    {
        // Admin+: hand back Linear's authorize URL, carrying a signed state the callback checks against the
        // browser's cookie.
        app.MapPost("/api/orgs/{orgId:long}/linear/connect",
                Results<Ok<LinearConnectResponse>, NotFound> (
                    long orgId, LinearConnectRequest req, LinearConfig config, IServiceProvider services,
                    HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }
                    var state = ConnectFlow.Start(
                        http, ConnectFlow.Linear, config.Options!.ClientSecret, orgId,
                        ConnectFlow.SafeReturnPath(req.ReturnPath), installed: false);
                    return TypedResults.Ok(new LinearConnectResponse(
                        services.GetRequiredService<LinearOAuth>().BuildAuthorizeUrl(state)));
                })
            .WithName("linearConnect").WithTags("Linear")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        // Linear, through the browser, back to us. The signed state, paired with the cookie, is what says
        // which org this grant is for; the code is exchanged for it here and the workspace read with it.
        app.MapGet("/api/linear/oauth/callback",
                async Task<Results<RedirectHttpResult, NotFound>> (
                    LinearConfig config, IServiceProvider services, TrackerConnectionRepository connections,
                    ProjectTrackerMappingRepository mappings, IConfiguration cfg, HttpContext http,
                    ILoggerFactory loggers) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }
                    if (ConnectFlow.Verify(http, ConnectFlow.Linear, config.Options!.ClientSecret) is not { } state)
                    {
                        return TypedResults.Redirect(ConnectFlow.DashboardUrl(cfg, ConnectFlow.Linear, "/", "failed"));
                    }
                    var back = (string outcome) =>
                        TypedResults.Redirect(ConnectFlow.DashboardUrl(cfg, ConnectFlow.Linear, state.ReturnPath, outcome));

                    var code = http.Request.Query["code"].ToString();
                    if (string.IsNullOrEmpty(code))
                    {
                        // Linear sends the user back without a code when they decline.
                        return back("declined");
                    }
                    var oauth = services.GetRequiredService<LinearOAuth>();
                    LinearTokens? tokens = null;
                    try
                    {
                        var api = services.GetRequiredService<LinearApi>();
                        var secrets = services.GetRequiredService<SecretBox>();
                        tokens = await oauth.ExchangeCodeAsync(code, http.RequestAborted);
                        if (tokens is null)
                        {
                            return back("failed");
                        }
                        var workspace = await api.GetWorkspaceAsync(tokens.AccessToken, http.RequestAborted);
                        var previous = await connections.GetAsync(state.OrgId, TicketProvider.Linear, http.RequestAborted);
                        await connections.UpsertAsync(
                            new TrackerConnection(
                                state.OrgId, TicketProvider.Linear, workspace.Id, workspace.Name,
                                secrets.Seal(tokens.AccessToken), secrets.Seal(tokens.RefreshToken), tokens.ExpiresAt,
                                BrokenAt: null),
                            // The callback carries no authorization requirement, since Linear is what calls
                            // it, so the session may be absent; 0 is how CurrentUserId says so.
                            OrgAuthorization.CurrentUserId(http.User) is > 0 and var by ? by : null,
                            http.RequestAborted);
                        if (previous is not null && previous.WorkspaceId != workspace.Id)
                        {
                            // A team id from one workspace means nothing in another.
                            await mappings.DeleteForOrgAsync(state.OrgId, TicketProvider.Linear, http.RequestAborted);
                        }
                        return back("connected");
                    }
                    catch (Exception ex)
                    {
                        // A grant issued and then not stored, for any reason including our own database or the
                        // browser going away, is one nothing would ever revoke. So it is revoked here, on a token
                        // the request's own cancellation cannot stop, before the failure is answered.
                        if (tokens is not null)
                        {
                            await oauth.RevokeAsync(tokens.RefreshToken, "refresh_token", CancellationToken.None);
                            await oauth.RevokeAsync(tokens.AccessToken, "access_token", CancellationToken.None);
                        }
                        if (ex is LinearUnauthorizedException || LinearHttp.IsCallFailure(ex, http.RequestAborted))
                        {
                            loggers.CreateLogger("LinearConnect").LogWarning(ex, "linear connect failed org={OrgId}", state.OrgId);
                            return back("failed");
                        }
                        throw;
                    }
                })
            .WithName("linearOauthCallback").WithTags("Linear");

        // Member+: whether the org is connected, and to what.
        app.MapGet("/api/orgs/{orgId:long}/linear",
                async Task<Results<Ok<LinearOrgStatusResponse>, NotFound>> (
                    long orgId, LinearConfig config, TrackerConnectionRepository connections,
                    ProjectTrackerMappingRepository mappings, HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }
                    var connection = await connections.GetAsync(orgId, TicketProvider.Linear, http.RequestAborted);
                    var mapped = await mappings.ListMappedProjectNamesAsync(orgId, TicketProvider.Linear, http.RequestAborted);
                    return TypedResults.Ok(new LinearOrgStatusResponse(
                        connection is not null, connection?.WorkspaceName, connection?.BrokenAt is not null, mapped));
                })
            .WithName("getLinearConnection").WithTags("Linear")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        // Admin+: remove the org's grant and every project's team, which the dialog lists before this runs.
        // Revoked at Linear first so it stops working rather than only being forgotten, but removed either
        // way: keeping a connection the admin asked to remove is the worse outcome, and the response says
        // whether the revoke landed so the dashboard can say what is left.
        app.MapDelete("/api/orgs/{orgId:long}/linear",
                async Task<Results<Ok<LinearDisconnectResponse>, NotFound>> (
                    long orgId, LinearConfig config, IServiceProvider services, TrackerConnectionRepository connections,
                    ProjectTrackerMappingRepository mappings, HttpContext http, ILoggerFactory loggers) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }
                    if (await connections.GetAsync(orgId, TicketProvider.Linear, http.RequestAborted) is not { } connection)
                    {
                        return TypedResults.NotFound();
                    }
                    var revoked = await LinearRevocation.TryRevokeAsync(services, connection, http.RequestAborted);
                    if (!revoked)
                    {
                        loggers.CreateLogger("LinearConnect").LogWarning("linear revoke not confirmed org={OrgId}", orgId);
                    }
                    await connections.DeleteAsync(orgId, TicketProvider.Linear, http.RequestAborted);
                    await mappings.DeleteForOrgAsync(orgId, TicketProvider.Linear, http.RequestAborted);
                    return TypedResults.Ok(new LinearDisconnectResponse(revoked));
                })
            .WithName("disconnectLinear").WithTags("Linear")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));
    }
}
