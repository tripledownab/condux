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
/// Opt-in on top of the App config (<see cref="GitHubAppConfig.UserOAuthEnabled"/>): without the client
/// secret and callback URL, connect falls back to the plain install URL.
/// </summary>
internal static class GithubConnectEndpoints
{
    // The connect state is valid 15 minutes: long enough to install, short enough to limit replay. The
    // selection token outlives it slightly because the user has to read a list and choose.
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SelectionLifetime = TimeSpan.FromMinutes(30);

    public static void MapGithubConnectEndpoints(this IEndpointRouteBuilder app)
    {
        // Connect: authenticated. Mint a state token for the org (carrying where to return afterwards) and
        // return the URL to send the browser to. Started from inside a project so it lands back there. Admin+.
        app.MapPost("/api/orgs/{orgId:long}/github/connect",
                Results<Ok<GithubConnectResponse>, NotFound> (
                    long orgId, GithubConnectRequest req, GitHubAppConfig config, GitHubUserOAuth oauth) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var state = MintState(orgId, SafeReturnPath(req.ReturnPath), config);
                    return TypedResults.Ok(new GithubConnectResponse(
                        config.UserOAuthEnabled
                            ? oauth.AuthorizeUrl(config.Options!.ClientId, config.OAuthRedirectUri!, state)
                            : InstallUrl(config, state)));
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
                    if (!config.UserOAuthEnabled)
                    {
                        return TypedResults.NotFound();
                    }

                    if (GithubConnectState.Validate(
                            http.Request.Query["state"], DateTimeOffset.UtcNow, config.Options!.WebhookSecret)
                        is not { } state)
                    {
                        // No verified state means no org to act for, so there is nowhere safe to return to.
                        return TypedResults.Redirect(DashboardUrl(cfg, "/", "failed"));
                    }

                    var code = http.Request.Query["code"].ToString();
                    var userToken = string.IsNullOrEmpty(code)
                        ? null
                        : await oauth.ExchangeCodeAsync(
                            config.Options.ClientId, config.ClientSecret!, config.OAuthRedirectUri!,
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

                    // Authorized but never installed: send them on to install it. GitHub's Setup URL finishes
                    // the link, and the state we already minted is still valid for that leg.
                    if (reachable.Count == 0)
                    {
                        return TypedResults.Redirect(InstallUrl(
                            config, MintState(state.OrgId, state.ReturnPath, config)));
                    }

                    if (reachable.Count == 1)
                    {
                        var only = reachable[0];
                        if (await LinkedElsewhereAsync(installations, only.InstallationId, state.OrgId, http))
                        {
                            return TypedResults.Redirect(DashboardUrl(cfg, state.ReturnPath, "taken"));
                        }

                        await installations.LinkAsync(
                            only.InstallationId, state.OrgId, only.AccountLogin, http.RequestAborted);
                        return TypedResults.Redirect(DashboardUrl(cfg, state.ReturnPath, "connected"));
                    }

                    var selection = GithubSelectionToken.Create(
                        state.OrgId,
                        [.. reachable.Select(i => new GithubSelectionCandidate(i.InstallationId, i.AccountLogin))],
                        DateTimeOffset.UtcNow.Add(SelectionLifetime), config.Options.WebhookSecret);
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

        // The accounts behind a selection token, so the dashboard can render the choice. Reading it here
        // rather than in the browser keeps the signature the only thing that decides what the token says.
        app.MapGet("/api/orgs/{orgId:long}/github/selection",
                Results<Ok<GithubSelectionResponse>, BadRequest<ErrorResponse>, NotFound> (
                    long orgId, string token, GitHubAppConfig config) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var selection = GithubSelectionToken.Validate(
                        token, DateTimeOffset.UtcNow, config.Options!.WebhookSecret);
                    return selection is null || selection.OrgId != orgId
                        ? TypedResults.BadRequest(new ErrorResponse("invalid_selection"))
                        : TypedResults.Ok(new GithubSelectionResponse(
                            [.. selection.Candidates.Select(c =>
                                new GithubSelectionOption(c.InstallationId, c.AccountLogin))]));
                })
            .WithName("githubSelection").WithTags("GitHub")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));

        // Link the installation the user picked. Only ids inside the signed token are accepted, so the
        // choice is bounded by what GitHub told us that user can reach. Admin+.
        app.MapPost("/api/orgs/{orgId:long}/github/select",
                async Task<Results<NoContent, BadRequest<ErrorResponse>, Conflict<ErrorResponse>, NotFound>> (
                    long orgId, GithubSelectRequest req, GitHubAppConfig config,
                    GithubInstallationRepository installations, HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var selection = GithubSelectionToken.Validate(
                        req.Selection, DateTimeOffset.UtcNow, config.Options!.WebhookSecret);
                    var chosen = selection?.OrgId == orgId
                        ? selection.Candidates.FirstOrDefault(c => c.InstallationId == req.InstallationId)
                        : null;
                    if (chosen is null)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_selection"));
                    }

                    if (await LinkedElsewhereAsync(installations, chosen.InstallationId, orgId, http))
                    {
                        return TypedResults.Conflict(new ErrorResponse("installation_linked_elsewhere"));
                    }

                    await installations.LinkAsync(
                        chosen.InstallationId, orgId, chosen.AccountLogin, http.RequestAborted);
                    return TypedResults.NoContent();
                })
            .WithName("githubSelect").WithTags("GitHub")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Admin));
    }

    /// <summary>
    /// Whether this installation already belongs to a different org. Reaching an installation on GitHub only
    /// takes read access, so without this check a collaborator who admins their own Condux org could move
    /// another tenant's installation — and with it the repo access the Conductor mints tokens for.
    /// </summary>
    private static async Task<bool> LinkedElsewhereAsync(
        GithubInstallationRepository installations, long installationId, long orgId, HttpContext http) =>
        await installations.GetAsync(installationId, http.RequestAborted) is { } existing
        && existing.OrgId != orgId;

    private static string MintState(long orgId, string returnPath, GitHubAppConfig config) =>
        GithubConnectState.Create(
            orgId, returnPath, DateTimeOffset.UtcNow.Add(StateLifetime), config.Options!.WebhookSecret);

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
