using System.Globalization;
using System.Text.Json;
using Condux.ControlPlane.Auth;
using Condux.ControlPlane.Setup;
using Condux.Core.FixEngine;
using Condux.GitHub;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// The two routes GitHub itself drives, which is why neither has a session behind it (#61): the webhook
/// receiver, authenticated by its HMAC signature and handling the installation lifecycle plus merged
/// pull requests, and the Setup URL redirect, authorized by our own signed connect state. What an org
/// reads back through its installation lives in <see cref="GithubInstallationEndpoints"/>, and starting
/// or ending a connection in <see cref="GithubConnectEndpoints"/>. All behind the opt-in
/// <see cref="GitHubAppConfig"/>; when the app isn't configured they return 404.
/// </summary>
internal static class GithubEndpoints
{
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

        // Setup URL: GitHub → the user's browser → us, once the app has been installed. This leg
        // deliberately writes nothing. GitHub puts an installation_id in the query, but anyone can put one
        // there and nothing here can check the caller is entitled to it, while the signed state proves
        // only which org minted it and any org admin can mint one for their own org. So instead of
        // trusting the id, hand the browser back to user authorization: its callback asks GitHub which
        // installations THIS person can reach and links only those. For a user who authorized at the
        // start of this same flow that hop is a silent redirect.
        app.MapGet("/api/github/setup",
                Results<RedirectHttpResult, BadRequest<ErrorResponse>, NotFound> (
                    GitHubAppConfig config, GitHubUserOAuth oauth, HttpContext http) =>
                {
                    if (!config.Enabled)
                    {
                        return TypedResults.NotFound();
                    }

                    if (GithubConnectFlow.Verify(http, config) is not { } state)
                    {
                        return TypedResults.BadRequest(new ErrorResponse("invalid_setup"));
                    }

                    // Marked installed, so the callback reads an empty list of reachable installations as
                    // an organization owner still having to approve this one, rather than sending the
                    // user back to install it again.
                    return TypedResults.Redirect(oauth.AuthorizeUrl(
                        config.Options!.ClientId, config.OAuthRedirectUri!,
                        GithubConnectFlow.Start(http, state.OrgId, state.ReturnPath, config, installed: true)));
                })
            .WithName("githubSetup").WithTags("GitHub");
    }

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
