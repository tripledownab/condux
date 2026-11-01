using System.Net;
using System.Text;
using Condux.Core.CveScanning;
using Condux.GitHub;
using Xunit;

namespace Condux.GitHub.Tests;

/// <summary>
/// The two GitHub signals behind dependency advisories: a <c>dependabot_alert</c> webhook saying an
/// alert is new, and the 403 the alerts read answers when it cannot be done, whose message says why.
/// </summary>
public class DependabotAlertEventTests
{
    // The shape GitHub documents for the event (octokit/webhooks, dependabot_alert created), trimmed to
    // what is read and renamed to neutral values.
    private static byte[] Delivery(
        string action, string? ghsaId = "GHSA-c2qf-rxjj-qqgw", string state = "open") => Encoding.UTF8.GetBytes($$"""
        {
          "action": "{{action}}",
          "alert": {
            "number": 20,
            "state": "{{state}}",
            "dependency": { "package": { "ecosystem": "npm", "name": "semver" }, "manifest_path": "web/package-lock.json" },
            "security_advisory": {
              "ghsa_id": {{(ghsaId is null ? "null" : $"\"{ghsaId}\"")}}, "cve_id": "CVE-2022-25883",
              "summary": "semver vulnerable to Regular Expression Denial of Service", "severity": "medium"
            },
            "security_vulnerability": {
              "package": { "ecosystem": "npm", "name": "semver" }, "severity": "medium",
              "vulnerable_version_range": "< 7.5.2", "first_patched_version": { "identifier": "7.5.2" }
            },
            "html_url": "https://github.com/acme/api/security/dependabot/20"
          },
          "repository": { "full_name": "acme/api" },
          "installation": { "id": 13986380 }
        }
        """);

    [Theory]
    [InlineData("created")]
    [InlineData("reintroduced")]
    [InlineData("auto_reopened")]
    public void A_new_alert_parses_with_its_installation_repository_and_advisory(string action)
    {
        var parsed = DependabotAlertEvent.Parse(Delivery(action));

        Assert.NotNull(parsed);
        Assert.Equal(13986380, parsed.InstallationId);
        Assert.Equal("acme/api", parsed.RepoFullName);
        Assert.Equal("GHSA-c2qf-rxjj-qqgw", parsed.Alert.GhsaId);
        Assert.Equal("semver", parsed.Alert.Package);
        Assert.Equal("7.5.2", parsed.Alert.FixedVersion);
        Assert.Equal("web/package-lock.json", parsed.Alert.ManifestPath);
    }

    [Theory]
    [InlineData("dismissed")]
    [InlineData("fixed")]
    [InlineData("reopened")]
    [InlineData("auto_dismissed")]
    [InlineData("withdrawn")]
    public void A_change_to_a_known_alert_is_not_a_new_one(string action) =>
        Assert.Null(DependabotAlertEvent.Parse(Delivery(action)));

    // An alert a team has already set aside, by a rule or by hand, is not news even when it is created.
    [Theory]
    [InlineData("auto_dismissed")]
    [InlineData("dismissed")]
    public void An_alert_that_is_not_open_is_not_a_new_one(string state) =>
        Assert.Null(DependabotAlertEvent.Parse(Delivery("created", state: state)));

    [Fact]
    public void A_delivery_with_no_installation_object_is_not_read() =>
        Assert.Null(DependabotAlertEvent.Parse(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(Delivery("created")).Replace("{ \"id\": 13986380 }", "null"))));

    [Fact]
    public void An_alert_without_its_advisory_id_is_not_read() =>
        Assert.Null(DependabotAlertEvent.Parse(Delivery("created", ghsaId: null)));

    private sealed class ForbiddenHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(body) });
    }

    // The first message is GitHub's for a repository with alerts switched off, as other clients of this
    // endpoint match it; the second is its standard refusal of an App lacking a permission.
    [Theory]
    [InlineData("""{"message":"Dependabot alerts are disabled for this repository.","documentation_url":"x"}""", CveScanUnavailable.AlertsDisabled)]
    [InlineData("""{"message":"Resource not accessible by integration","documentation_url":"x"}""", CveScanUnavailable.PermissionMissing)]
    [InlineData("""{"message":"Must have admin rights to Repository."}""", CveScanUnavailable.Failed)]
    [InlineData("not json", CveScanUnavailable.Failed)]
    public async Task A_refused_read_names_why(string body, CveScanUnavailable reason)
    {
        var client = new GitHubRepoClient(new HttpClient(new ForbiddenHandler(body)), "https://gh.test");

        var refused = await Assert.ThrowsAsync<CveScanUnavailableException>(
            () => client.ListDependabotAlertsAsync("ghs_test", "acme/api"));

        Assert.Equal(reason, refused.Reason);
    }
}
