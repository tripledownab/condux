using System.Globalization;
using System.Net;
using System.Text.Json;
using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.Auth;
using Condux.Core.FixEngine;
using Condux.Core.SourceControl;
using Condux.GitHub;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// GitHub App install flow (#61): the webhook receiver (HMAC-verified, handles the installation
/// lifecycle), the Setup URL redirect (validates our connect state and ties the installation to an org),
/// and the reads the dashboard needs — what is linked, whether it still works, and a repo's branches.
/// Starting a connect lives in <see cref="GithubConnectEndpoints"/>. All behind the opt-in
/// <see cref="GitHubAppConfig"/>; when the app isn't configured they return 404.
/// </summary>
internal static class GithubEndpoints
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

    public static void MapGithubEndpoints(this IEndpointRouteBuilder app)
    {
        // Webhook: GitHub → us (server to server). No cookie auth; the HMAC signature is the authenticator.
        app.MapPost("/api/github/webhook",
                async Task<Results<NoContent, UnauthorizedHttpResult, NotFound>> (
                    HttpContext http, GitHubAppConfig config, GithubInstallationRepository installations,
                    PostgresFixVerification fixVerification, IFixStore fixes) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var body = await ReadBodyAsync(http);
                    if (!GitHubWebhook.IsValidSignature(
                            config.Options!.WebhookSecret, body, http.Request.Headers["X-Hub-Signature-256"]))
                    {
                        return TypedResults.Unauthorized();
                    }

                    switch (http.Request.Headers["X-GitHub-Event"].ToString())
                    {
                        case "installation":
                            await HandleInstallationEventAsync(body, installations, http.RequestAborted);
                            break;
                        case "pull_request":
                            await HandlePullRequestEventAsync(body, fixVerification, fixes, http.RequestAborted);
                            break;
                    }
                    return TypedResults.NoContent();
                })
            .WithName("githubWebhook").WithTags("GitHub");

        // Setup URL: GitHub → the user's browser → us. The signed state authorizes tying the install to an org.
        app.MapGet("/api/github/setup",
                async Task<Results<RedirectHttpResult, BadRequest<ErrorResponse>, NotFound>> (
                    GitHubAppConfig config, GithubInstallationRepository installations,
                    IConfiguration cfg, HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    if (!long.TryParse(http.Request.Query["installation_id"], NumberStyles.None,
                            CultureInfo.InvariantCulture, out var installationId)
                        || GithubConnectState.Validate(
                            http.Request.Query["state"], DateTimeOffset.UtcNow, config.Options!.WebhookSecret)
                            is not { } state)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_setup"));
                    }

                    await installations.LinkAsync(installationId, state.OrgId, ct: http.RequestAborted);
                    return TypedResults.Redirect(
                        GithubConnectEndpoints.DashboardUrl(cfg, state.ReturnPath, "connected"));
                })
            .WithName("githubSetup").WithTags("GitHub");

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
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var linked = await installations.GetByOrgAsync(orgId, http.RequestAborted);
                    if (linked.Count == 0)
                    {
                        return TypedResults.NotFound();
                    }

                    // Resolved lazily: these services are only registered when the app is configured.
                    var tokens = http.RequestServices.GetRequiredService<ISourceHostTokens>();
                    var repoClient = http.RequestServices.GetRequiredService<ISourceHostClient>();
                    var token = await tokens.GetAsync(linked[0].InstallationId, http.RequestAborted);
                    var repositories = await repoClient.ListInstallationRepositoriesAsync(
                        token, http.RequestAborted);
                    return TypedResults.Ok(new GithubRepositoriesResponse(repositories));
                })
            .WithName("listGithubRepositories").WithTags("GitHub")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

        // Branches: proxy the repo's branch list from GitHub with the org's installation token, so the
        // settings tab can offer a real base-branch picker. The token stays server-side. Member+.
        app.MapGet("/api/orgs/{orgId:long}/github/branches",
                async Task<Results<Ok<GithubBranchesResponse>, NotFound>> (
                    long orgId, string repo, GitHubAppConfig config,
                    GithubInstallationRepository installations, HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    var linked = await installations.GetByOrgAsync(orgId, http.RequestAborted);
                    if (linked.Count == 0)
                    {
                        return TypedResults.NotFound();
                    }

                    // Resolved lazily: these services are only registered when the app is configured.
                    var tokens = http.RequestServices.GetRequiredService<ISourceHostTokens>();
                    var repoClient = http.RequestServices.GetRequiredService<ISourceHostClient>();
                    var token = await tokens.GetAsync(linked[0].InstallationId, http.RequestAborted);
                    var branches = await repoClient.ListBranchesAsync(token, repo, http.RequestAborted);
                    return TypedResults.Ok(new GithubBranchesResponse(branches));
                })
            .WithName("listGithubBranches").WithTags("GitHub")
            .RequireAuthorization().AddEndpointFilter(OrgAuthorization.RequireOrgRole(OrgRole.Member));

    }

    /// <summary>
    /// Where a user manages this app on GitHub. For an account that already has it installed GitHub serves
    /// the installation's configure page here (repository access, and Uninstall), rather than a new install,
    /// which is the same behaviour that makes this URL useless for re-connecting.
    /// </summary>
    private static string ManageUrl(GitHubAppConfig config) =>
        $"https://github.com/apps/{config.AppSlug}/installations/new";

    private static async Task<byte[]> ReadBodyAsync(HttpContext http)
    {
        using var ms = new MemoryStream();
        await http.Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static async Task HandleInstallationEventAsync(
        byte[] body, GithubInstallationRepository installations, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("installation", out var installation)
            || !installation.TryGetProperty("id", out var idElement))
        {
            return;
        }

        var installationId = idElement.GetInt64();
        var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
        if (action == "deleted")
        {
            await installations.DeleteAsync(installationId, ct);
            return;
        }

        // created / new_permissions_accepted / etc.: record the account login if the row exists (setup made it).
        if (installation.TryGetProperty("account", out var account)
            && account.TryGetProperty("login", out var login) && login.GetString() is { } loginValue)
        {
            await installations.SetAccountLoginAsync(installationId, loginValue, ct);
        }
    }

    // A merged Conductor draft PR starts the verification watch (ADR-0019): match the PR's repo +
    // head branch to the fix run that opened it, stamp merged_at and audit the merge. Non-Conductor
    // PRs simply match nothing.
    private static async Task HandlePullRequestEventAsync(
        byte[] body, PostgresFixVerification fixVerification, IFixStore fixes,
        CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if ((root.TryGetProperty("action", out var a) ? a.GetString() : null) != "closed"
            || !root.TryGetProperty("pull_request", out var pr)
            || !pr.TryGetProperty("merged", out var merged) || !merged.GetBoolean()
            || !root.TryGetProperty("repository", out var repository)
            || !repository.TryGetProperty("full_name", out var repoName)
            || repoName.GetString() is not { } repo
            || !pr.TryGetProperty("head", out var head)
            || !head.TryGetProperty("ref", out var headRef) || headRef.GetString() is not { } branch)
        {
            return;
        }

        var mergedAt = pr.TryGetProperty("merged_at", out var m) && m.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(m.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

        var prUrl = pr.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
        foreach (var fixId in await fixVerification.MarkMergedAsync(repo, branch, mergedAt, ct))
        {
            var detail = JsonSerializer.Serialize(new { prUrl, mergedAt });
            await fixes.AppendAuditAsync(fixId, "github", "fix_merged", detail, ct);
        }
    }

}
