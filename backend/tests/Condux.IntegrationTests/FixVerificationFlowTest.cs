using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The fix-verification loop's persistence path (ADR-0019): a merged Conductor draft PR arriving on
/// the GitHub webhook flips the matching succeeded run to watching with a merged_at + fix_merged
/// audit; the watcher's queries list it and conclude it exactly once.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FixVerificationFlowTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string WebhookSecret = GithubAppSettings.WebhookSecret;

    private WebApplicationFactory<Program> CreateApp() =>
        ControlPlaneApp.Create(pg.ConnectionString)
            .WithWebHostBuilder(GithubAppSettings.Apply);

    [Fact]
    public async Task Merged_pull_request_starts_the_watch_and_the_watcher_concludes_once()
    {
        var app = CreateApp();
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);

        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "verify-" + Guid.NewGuid().ToString("N"), name = "Verify" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "backend", name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping("fp-verify-1", "TypeError: boom", "run"), Level.Error, DateTimeOffset.UtcNow);

        // A succeeded run whose draft PR lives on condux/fix-slug, as the orchestrator leaves it.
        var fixes = new PostgresFixStore(pg.ConnectionString);
        var fixId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await fixes.InsertAsync(new FixSuggestion(
            fixId, issue.Id, "acme/api", FixStatus.Succeeded, "fake", "model", "condux/fix-boom",
            "https://github.com/acme/api/pull/7", "fixed", now, now));

        // A close without a merge matches nothing.
        var closedUnmerged = await SendPullRequestWebhookAsync(app, merged: false);
        Assert.Equal(HttpStatusCode.NoContent, closedUnmerged.StatusCode);
        Assert.Equal(VerifyStatus.None, (await fixes.GetAsync(fixId))!.VerifyStatus);

        // The merge flips it to watching, stamps merged_at and audits fix_merged.
        var mergedResp = await SendPullRequestWebhookAsync(app, merged: true);
        Assert.Equal(HttpStatusCode.NoContent, mergedResp.StatusCode);
        var watchingFix = (await fixes.GetAsync(fixId))!;
        Assert.Equal(VerifyStatus.Watching, watchingFix.VerifyStatus);
        Assert.NotNull(watchingFix.MergedAt);
        Assert.Equal(1, await CountAuditAsync(fixId, "fix_merged"));

        // The watcher's list query joins the issue's ids for the ClickHouse lookup + resolve.
        var verification = new PostgresFixVerification(pg.ConnectionString);
        var watched = Assert.Single(await verification.ListWatchingAsync(), w => w.FixId == fixId);
        Assert.Equal(issue.Id, watched.IssueId);
        Assert.Equal(issue.PublicId, watched.IssuePublicId);
        Assert.Equal(projectId, watched.ProjectId);

        // Concluding is idempotent: the first tick wins, a concurrent one is a no-op.
        Assert.True(await verification.SetVerifyStatusAsync(fixId, VerifyStatus.Held, DateTimeOffset.UtcNow));
        Assert.False(await verification.SetVerifyStatusAsync(fixId, VerifyStatus.DidNotHold, DateTimeOffset.UtcNow));
        var concluded = (await fixes.GetAsync(fixId))!;
        Assert.Equal(VerifyStatus.Held, concluded.VerifyStatus);
        Assert.NotNull(concluded.VerifiedAt);

        // A second merge webhook for the same branch has nothing left to flip.
        var replay = await SendPullRequestWebhookAsync(app, merged: true);
        Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        Assert.Equal(VerifyStatus.Held, (await fixes.GetAsync(fixId))!.VerifyStatus);
    }

    private static async Task<HttpResponseMessage> SendPullRequestWebhookAsync(
        WebApplicationFactory<Program> app, bool merged)
    {
        var payload = Encoding.UTF8.GetBytes($$"""
            {
              "action": "closed",
              "repository": { "full_name": "acme/api" },
              "pull_request": {
                "merged": {{(merged ? "true" : "false")}},
                "merged_at": "2026-07-22T12:00:00Z",
                "html_url": "https://github.com/acme/api/pull/7",
                "head": { "ref": "condux/fix-boom" }
              }
            }
            """);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/github/webhook")
        {
            Content = new ByteArrayContent(payload),
        };
        req.Headers.Add("X-GitHub-Event", "pull_request");
        req.Headers.Add("X-Hub-Signature-256", Sign(WebhookSecret, payload));
        return await app.CreateClient().SendAsync(req);
    }

    private static string Sign(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(body));
    }

    private async Task<long> CountAuditAsync(Guid fixId, string eventName)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM fix_audit WHERE fix_id = @fix AND event = @event", conn);
        cmd.Parameters.AddWithValue("fix", fixId);
        cmd.Parameters.AddWithValue("event", eventName);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
