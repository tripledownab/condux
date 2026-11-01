using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Condux.Core.Alerting;
using Condux.Core.Events;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Condux.Tests.Shared;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// A <c>dependabot_alert</c> webhook firing a project's alert rules: the signed delivery, the routing by
/// installation and repository in Postgres, the rule match on the advisory's severity, and the real
/// webhook notifier over a real socket.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdvisoryAlertWebhookTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    /// <summary>An org with a project linked to <c>Acme/API</c> and the GitHub installation it came through.</summary>
    private async Task<(long ProjectId, long InstallationId)> SeedAsync()
    {
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var installationId = Random.Shared.NextInt64(1, long.MaxValue);
        Assert.True(await new GithubInstallationRepository(pg.ConnectionString).LinkAsync(installationId, orgId));
        await new RepoLinkRepository(pg.ConnectionString).LinkAsync(projectId, "Acme/API", "main");
        return (projectId, installationId);
    }

    private static byte[] Delivery(long installationId, string action = "created") => Encoding.UTF8.GetBytes($$"""
        {
          "action": "{{action}}",
          "alert": {
            "dependency": { "package": { "ecosystem": "npm", "name": "semver" }, "manifest_path": "package-lock.json" },
            "security_advisory": { "ghsa_id": "GHSA-c2qf-rxjj-qqgw", "summary": "semver ReDoS", "severity": "critical" },
            "security_vulnerability": {
              "package": { "ecosystem": "npm", "name": "semver" },
              "vulnerable_version_range": "< 7.5.2", "first_patched_version": { "identifier": "7.5.2" }
            },
            "html_url": "https://github.com/acme/api/security/dependabot/20"
          },
          "repository": { "full_name": "acme/api" },
          "installation": { "id": {{installationId}} }
        }
        """);

    [Fact]
    public async Task A_new_advisory_reaches_a_rule_subscribed_to_advisories_at_its_level()
    {
        using var receiver = LoopbackHttpReceiver.Start();
        var (projectId, installationId) = await SeedAsync();
        var rules = new AlertRuleRepository(pg.ConnectionString);
        var rule = await rules.CreateRuleAsync(projectId, "deps", [AlertEventType.Advisory], [Level.Fatal]);
        await rules.AddChannelAsync(rule.Id, NotificationChannel.Webhook, receiver.Url);
        var app = ControlPlaneApp.Create(pg.ConnectionString).WithWebHostBuilder(GithubAppSettings.Apply);

        var next = receiver.NextRequestAsync();
        var response = await SendAsync(app.CreateClient(), Delivery(installationId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var doc = JsonDocument.Parse((await next.WaitAsync(TimeSpan.FromSeconds(10))).Body);
        Assert.Equal("advisory", doc.RootElement.GetProperty("trigger").GetString());
        Assert.Equal("Fatal", doc.RootElement.GetProperty("level").GetString());
        Assert.Equal("GHSA-c2qf-rxjj-qqgw: semver ReDoS", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("issueId").ValueKind);
    }

    private static readonly DateOnly Today = new(2026, 10, 23);

    [Fact]
    public async Task An_advisory_claims_each_linked_project_once_a_day_matching_the_name_in_any_case()
    {
        var (projectId, installationId) = await SeedAsync();
        var notices = new AdvisoryNoticeRepository(pg.ConnectionString);

        Assert.Equal([projectId], await notices.ClaimAsync(installationId, "acme/api", "GHSA-1", "npm:semver", Today));
        // A redelivery, or the same advisory raised on another manifest, notifies nobody again that day.
        Assert.Empty(await notices.ClaimAsync(installationId, "ACME/api", "GHSA-1", "npm:semver", Today));
        // Another advisory is a new notice, as is the same advisory for another package it names, and the
        // same one on a later day, as a reintroduced alert is.
        Assert.Equal([projectId], await notices.ClaimAsync(installationId, "acme/api", "GHSA-2", "npm:semver", Today));
        Assert.Equal([projectId], await notices.ClaimAsync(installationId, "acme/api", "GHSA-1", "npm:semver-utils", Today));
        Assert.Equal(
            [projectId], await notices.ClaimAsync(installationId, "acme/api", "GHSA-1", "npm:semver", Today.AddDays(1)));
    }

    // A repository name is whatever an org typed when linking, so a delivery through another org's
    // installation must not reach a project that merely names the same repository.
    [Fact]
    public async Task A_delivery_reaches_only_the_org_that_holds_its_installation()
    {
        var (projectId, installationId) = await SeedAsync();
        // A second org linking the same repository name through its own installation.
        await SeedAsync();

        var reached = await new AdvisoryNoticeRepository(pg.ConnectionString)
            .ClaimAsync(installationId, "acme/api", "GHSA-1", "npm:semver", Today);

        Assert.Equal([projectId], reached);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, byte[] payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/github/webhook")
        {
            Content = new ByteArrayContent(payload),
        };
        request.Headers.Add("X-GitHub-Event", "dependabot_alert");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(GithubAppSettings.WebhookSecret));
        request.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(payload)));
        return await client.SendAsync(request);
    }
}
