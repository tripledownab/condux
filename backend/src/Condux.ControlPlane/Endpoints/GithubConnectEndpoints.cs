using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.GitHub;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// Connecting an org to a GitHub App installation. The install URL alone cannot do this: once the app is
/// installed on an account GitHub stops redirecting to the Setup URL, so an org that loses its link (a
/// restored database, a moved deployment) has no way back short of uninstalling the app. Going through
/// user authorization instead always returns to us, and the user's own token tells us which installations
/// they can reach — so a fresh install and a re-link are the same flow.
///
/// An installation is linked here, or in <see cref="GithubSelectionEndpoints"/> when the user had to
/// choose between several, and nowhere else (ADR-0045). Both sit downstream of the one read that says
/// which installations this person can reach. The Setup URL redirects back into this flow rather than
/// writing, because the id in its query is not evidence that the caller may act for that installation and
/// the user's token is. So the client secret and callback URL are required config rather than optional
/// extras: see <see cref="GitHubAppConfig"/>.
/// </summary>
internal static class GithubConnectEndpoints
{
    // The selection token outlives the connect state (GithubConnectFlow) slightly, because the user has to
    // read a list and choose.
    private static readonly TimeSpan SelectionLifetime = TimeSpan.FromMinutes(30);

    public static void MapGithubConnectEndpoints(this IEndpointRouteBuilder app)
    {
        // Connect: authenticated. Mint a state token for the org (carrying where to return afterwards) and
        // return the URL to send the browser to. Started from inside a project so it lands back there. Admin+.
        app.MapPost("/api/orgs/{orgId:long}/github/connect",
                Results<Ok<GithubConnectResponse>, NotFound> (
                    long orgId, GithubConnectRequest req, GitHubAppConfig config, GitHubUserOAuth oauth,
                    HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var state = GithubConnectFlow.Start(
                        http, orgId, SafeReturnPath(req.ReturnPath), config, installed: false);
                    return TypedResults.Ok(new GithubConnectResponse(
                        oauth.AuthorizeUrl(config.Options!.ClientId, config.OAuthRedirectUri!, state)));
                })
            .WithName("githubConnect").WithTags("GitHub")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        // OAuth callback: GitHub → the user's browser → us. The signed state authorizes acting for the org;
        // the code buys a user token whose only use is listing the installations that user can reach.
        app.MapGet("/api/github/oauth/callback",
                async Task<Results<RedirectHttpResult, NotFound>> (
                    GitHubAppConfig config, GitHubUserOAuth oauth,
                    GithubInstallationRepository installations, IConfiguration cfg, HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    if (GithubConnectFlow.Verify(http, config) is not { } state)
                    {
                        // No verified state means no org to act for, so there is nowhere safe to return to.
                        return TypedResults.Redirect(DashboardUrl(cfg, "/", "failed"));
                    }

                    var code = http.Request.Query["code"].ToString();
                    var userToken = string.IsNullOrEmpty(code)
                        ? null
                        : await oauth.ExchangeCodeAsync(
                            config.Options!.ClientId, config.ClientSecret!, config.OAuthRedirectUri!,
                            code, http.RequestAborted);
                    if (userToken is null)
                    {
                        return TypedResults.Redirect(DashboardUrl(cfg, state.ReturnPath, "failed"));
                    }

                    IReadOnlyList<GithubUserInstallation> reachable;
                    try
                    {
                        reachable = await oauth.ListInstallationsAsync(
                            userToken, config.AppSlug, http.RequestAborted);
                    }
                    catch (HttpRequestException)
                    {
                        return TypedResults.Redirect(DashboardUrl(cfg, state.ReturnPath, "failed"));
                    }

                    // Nothing reachable, which means one of two things the API cannot tell apart. Coming
                    // from connect it means the app is not installed yet, so send them to install it (a
                    // fresh state, because this one's cookie is now spent). Coming back from the Setup URL
                    // it means GitHub is holding the install for an organization owner to approve, and
                    // sending them to install it again would loop forever.
                    if (reachable.Count == 0)
                    {
                        return TypedResults.Redirect(state.Installed
                            ? DashboardUrl(cfg, state.ReturnPath, "pending")
                            : InstallUrl(config, GithubConnectFlow.Start(
                                http, state.OrgId, state.ReturnPath, config, installed: false)));
                    }

                    if (reachable.Count == 1)
                    {
                        var only = reachable[0];
                        var linked = await installations.LinkAsync(
                            only.InstallationId, state.OrgId, only.AccountLogin, http.RequestAborted);
                        return TypedResults.Redirect(
                            DashboardUrl(cfg, state.ReturnPath, linked ? "connected" : "taken"));
                    }

                    var selection = GithubSelectionToken.Create(
                        state.OrgId,
                        [.. reachable.Select(i => new GithubSelectionCandidate(i.InstallationId, i.AccountLogin))],
                        DateTimeOffset.UtcNow.Add(SelectionLifetime), config.Options!.WebhookSecret);
                    return TypedResults.Redirect(
                        $"{DashboardUrl(cfg, state.ReturnPath, "select")}"
                        + $"&selection={Uri.EscapeDataString(selection)}");
                })
            .WithName("githubOauthCallback").WithTags("GitHub");

        // Disconnect: stop using the org's installation. Admin+.
        //
        // Unlinks rather than uninstalling on GitHub, deliberately. Uninstalling would revoke an
        // org-wide grant from a single dashboard button, and it is not needed for the cases that matter:
        // the flow is reversible (connecting again finds the same installation) and an installation this
        // org no longer holds is free for another to claim, which is the only way to move one between
        // orgs. Revoking access entirely stays on GitHub, where the panel links to.
        //
        // Repo links are left alone: reconnecting restores them, and deleting a project's code mappings
        // as a side effect of a reversible action would be a poor trade.
        app.MapDelete("/api/orgs/{orgId:long}/github",
                async Task<Results<NoContent, NotFound>> (
                    long orgId, GitHubAppConfig config,
                    GithubInstallationRepository installations, HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var removed = await installations.DeleteByOrgAsync(orgId, http.RequestAborted);
                    return removed == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
                })
            .WithName("disconnectGithub").WithTags("GitHub")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

    }

    private static string InstallUrl(GitHubAppConfig config, string state) =>
        $"https://github.com/apps/{config.AppSlug}/installations/new?state={Uri.EscapeDataString(state)}";

    /// <summary>
    /// Where to send the browser after a connect leg: the dashboard's base URL plus the in-app path the flow
    /// started from, flagged with the outcome so the page can report it.
    /// </summary>
    internal static string DashboardUrl(IConfiguration cfg, string returnPath, string outcome)
    {
        var separator = returnPath.Contains('?') ? "&" : "?";
        return $"{AppUrls.BaseUrl(cfg)}{returnPath}{separator}github={outcome}";
    }

    /// <summary>
    /// Only a same-origin relative path may be echoed back through the connect state (an open-redirect
    /// guard): it must be a single leading slash, no protocol-relative "//" and no backslash. Anything
    /// else falls back to home.
    /// </summary>
    internal static string SafeReturnPath(string? path) =>
        !string.IsNullOrEmpty(path) && path.Length <= 512 && path[0] == '/'
        && !path.StartsWith("//", StringComparison.Ordinal) && !path.Contains('\\')
            ? path
            : "/";
}
