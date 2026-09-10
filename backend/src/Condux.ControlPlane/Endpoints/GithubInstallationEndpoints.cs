using System.Net;
using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.SourceControl;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// What an org can see through its GitHub App installation: whether one is linked, whether GitHub still
/// honours it, and the repositories and branches it reaches. Every route is org-scoped and member+, and
/// the installation token stays server-side. The two routes GitHub itself drives live in
/// <see cref="GithubEndpoints"/>, and starting or ending a connection in
/// <see cref="GithubConnectEndpoints"/>. All behind the opt-in <see cref="GitHubAppConfig"/>; when the app
/// isn't configured they return 404.
/// </summary>
internal static class GithubInstallationEndpoints
{
    /// <summary>The connection states the health check reports. Strings on the wire, matching the other
    /// status DTOs, but named once here so the API and the dashboard cannot drift apart.</summary>
    internal static class GithubHealth
    {
        public const string NotConnected = "not_connected";
        public const string Healthy = "healthy";
        public const string Revoked = "revoked";
        public const string Unreachable = "unreachable";
    }

    public static void MapGithubInstallationEndpoints(this IEndpointRouteBuilder app)
    {
        // Installations: authenticated read of the org's linked GitHub App installs, so the settings tab
        // can show a real connected state instead of only the transient post-redirect banner. Member+.
        app.MapGet("/api/orgs/{orgId:long}/github",
                async Task<Results<Ok<IReadOnlyList<GithubInstallationResponse>>, NotFound>> (
                    long orgId, GitHubAppConfig config, GithubInstallationRepository installations,
                    HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var linked = await installations.GetByOrgAsync(orgId, http.RequestAborted);
                    return TypedResults.Ok<IReadOnlyList<GithubInstallationResponse>>(
                        [.. linked.Select(i => new GithubInstallationResponse(
                            i.InstallationId, i.AccountLogin, i.CreatedAt, ManageUrl(config)))]);
                })
            .WithName("listGithubInstallations").WithTags("GitHub")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        // Health: actually ask GitHub whether the org's installation still works, rather than trusting our
        // stored row. Minting an installation token is the check, because that is the exact credential every
        // fix run needs, so a pass here means the Conductor can really reach the repo. Member+.
        app.MapGet("/api/orgs/{orgId:long}/github/health",
                async Task<Results<Ok<GithubHealthResponse>, NotFound>> (
                    long orgId, GitHubAppConfig config,
                    GithubInstallationRepository installations, HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var linked = await installations.GetByOrgAsync(orgId, http.RequestAborted);
                    if (linked.Count == 0)
                    {
                        // Nothing recorded here. GitHub may still hold an installation whose link we lost,
                        // and reconnecting relinks it, so this is a reconnect prompt rather than an error.
                        return TypedResults.Ok(new GithubHealthResponse(
                            GithubHealth.NotConnected, null, null, null));
                    }

                    var installation = linked[0];
                    var tokens = http.RequestServices.GetRequiredService<ISourceHostTokens>();
                    try
                    {
                        await tokens.GetAsync(installation.InstallationId, http.RequestAborted);
                        return TypedResults.Ok(new GithubHealthResponse(
                            GithubHealth.Healthy, installation.InstallationId, installation.AccountLogin, null));
                    }
                    catch (HttpRequestException failure)
                    {
                        // A rejected installation (uninstalled or suspended on GitHub) is the org's problem to
                        // fix by reconnecting; anything else is GitHub being unreachable and is worth
                        // retrying. Collapsing the two would send someone to reinstall over a transient 502.
                        var revoked = failure.StatusCode
                            is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                        return TypedResults.Ok(new GithubHealthResponse(
                            revoked ? GithubHealth.Revoked : GithubHealth.Unreachable,
                            installation.InstallationId, installation.AccountLogin, failure.Message));
                    }
                })
            .WithName("githubHealth").WithTags("GitHub")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        // Repositories: proxy what the org's installation can reach, so linking a repo is a pick rather
        // than free text. Typing it invites a repo that does not exist, or one outside the installation,
        // which links fine and only fails much later when a fix run cannot mint a token for it. The token
        // stays server-side. Member+.
        app.MapGet("/api/orgs/{orgId:long}/github/repositories",
                async Task<Results<Ok<GithubRepositoriesResponse>, NotFound>> (
                    long orgId, GitHubAppConfig config,
                    GithubInstallationRepository installations, HttpContext http) =>
                    await ResolveInstallationAsync(orgId, config, installations, http) is not { } reach
                        ? TypedResults.NotFound()
                        : TypedResults.Ok(new GithubRepositoriesResponse(
                            await reach.Client.ListInstallationRepositoriesAsync(
                                reach.Token, http.RequestAborted))))
            .WithName("listGithubRepositories").WithTags("GitHub")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        // Branches: proxy the repo's branch list from GitHub with the org's installation token, so the
        // settings tab can offer a real base-branch picker. The token stays server-side. Member+.
        app.MapGet("/api/orgs/{orgId:long}/github/branches",
                async Task<Results<Ok<GithubBranchesResponse>, NotFound>> (
                    long orgId, string repo, GitHubAppConfig config,
                    GithubInstallationRepository installations, HttpContext http) =>
                    await ResolveInstallationAsync(orgId, config, installations, http) is not { } reach
                        ? TypedResults.NotFound()
                        : TypedResults.Ok(new GithubBranchesResponse(
                            await reach.Client.ListBranchesAsync(reach.Token, repo, http.RequestAborted))))
            .WithName("listGithubBranches").WithTags("GitHub")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));
    }

    /// <summary>
    /// The org's installation token and the client to spend it on, or null when the app is off or the org
    /// has nothing linked. Both proxy routes need exactly this pair and answer 404 for both empty cases,
    /// so the pair is resolved once here rather than restated. The services are resolved from the request
    /// because they are only registered when the app is configured.
    /// </summary>
    private static async Task<(string Token, ISourceHostClient Client)?> ResolveInstallationAsync(
        long orgId, GitHubAppConfig config, GithubInstallationRepository installations, HttpContext http)
    {
        if (!config.Enabled)
        {
            return null;
        }

        var linked = await installations.GetByOrgAsync(orgId, http.RequestAborted);
        if (linked.Count == 0)
        {
            return null;
        }

        var tokens = http.RequestServices.GetRequiredService<ISourceHostTokens>();
        var client = http.RequestServices.GetRequiredService<ISourceHostClient>();
        return (await tokens.GetAsync(linked[0].InstallationId, http.RequestAborted), client);
    }

    /// <summary>
    /// Where a user manages this app on GitHub. For an account that already has it installed GitHub serves
    /// the installation's configure page here (repository access, and Uninstall), rather than a new install,
    /// which is the same behaviour that makes this URL useless for re-connecting.
    /// </summary>
    private static string ManageUrl(GitHubAppConfig config) =>
        $"https://github.com/apps/{config.AppSlug}/installations/new";
}
