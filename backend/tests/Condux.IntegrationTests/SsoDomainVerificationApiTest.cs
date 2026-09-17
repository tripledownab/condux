using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Auth;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using static Condux.IntegrationTests.Fixtures.SsoDomainClaim;

namespace Condux.IntegrationTests;

/// <summary>
/// Proving control of a claimed email domain before it routes a login (ADR-0043).
///
/// The rule this pins is that exclusivity attaches to verification rather than to insertion. Several orgs
/// may hold a provisional claim on one domain and none of them signs anybody in, so squatting on a name no
/// longer locks its real owner out; the org that proves control takes it, and the rest keep a row that
/// does nothing. Both directions are covered, because a check that refused everything would satisfy a test
/// that only looked at the refusal.
///
/// The DNS resolver is stubbed at its transport, so these run the real <c>DohTxtResolver</c> parsing and
/// the real endpoint, and never make a network call.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SsoDomainVerificationApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private readonly SsoDomainClaim _sso = new(pg.ConnectionString);

    [Fact]
    public async Task A_saved_claim_is_provisional_and_publishing_the_record_proves_it()
    {
        var (app, dns) = _sso.CreateApp();
        var (orgId, client, recordName, recordValue) = await _sso.SaveAsync(app, "prove.test");

        // Saved, and saying plainly that it is not proved yet, which is what makes the next step findable.
        Assert.Equal("prove.test", recordName);
        Assert.StartsWith(DomainVerification.ValuePrefix, recordValue);
        var saved = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/sso-config");
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("verifiedAt").ValueKind);

        var before = await VerifyAsync(client, orgId);
        Assert.Equal("record_missing", before.GetProperty("outcome").GetString());

        dns.Publish(recordName, recordValue);
        var after = await VerifyAsync(client, orgId);
        Assert.Equal("verified", after.GetProperty("outcome").GetString());
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("verifiedAt").ValueKind);

        var proved = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/sso-config");
        Assert.NotEqual(JsonValueKind.Null, proved.GetProperty("verifiedAt").ValueKind);
    }

    [Fact]
    public async Task Only_a_proved_claim_routes_a_login()
    {
        var (app, dns) = _sso.CreateApp();
        var (orgId, client, recordName, recordValue) = await _sso.SaveAsync(app, "routing.test");
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // The whole point of the ADR: a domain an org merely named sends nobody to its IdP, so a false
        // claim cannot have its provider assert an address in a domain it does not control.
        var provisional = await browser.GetAsync("/api/auth/sso/start?email=someone@routing.test");
        Assert.Equal("/login?error=sso_not_available", provisional.Headers.Location!.ToString());

        dns.Publish(recordName, recordValue);
        await VerifyAsync(client, orgId);

        var proved = await browser.GetAsync("/api/auth/sso/start?email=someone@routing.test");
        Assert.StartsWith("https://idp.example/authorize", proved.Headers.Location!.ToString());
    }

    [Fact]
    public async Task The_challenge_subdomain_is_accepted_and_the_apex_is_tried_first()
    {
        var (app, dns) = _sso.CreateApp();
        var (orgId, client, _, recordValue) = await _sso.SaveAsync(app, "sub.test");

        dns.Publish($"{DomainVerification.ChallengeLabel}.sub.test", recordValue);
        Assert.Equal("verified", (await VerifyAsync(client, orgId)).GetProperty("outcome").GetString());

        // The apex is asked for first, because that is the form the instructions name and the one most
        // orgs will publish. An admin who used it must not have a second lookup made on their behalf.
        Assert.Equal(["sub.test", "_condux-challenge.sub.test"], dns.Queried);
    }

    [Fact]
    public async Task A_resolver_we_cannot_reach_is_reported_apart_from_a_missing_record()
    {
        var (app, dns) = _sso.CreateApp();
        var (orgId, client, _, _) = await _sso.SaveAsync(app, "outage.test");
        dns.Unreachable = true;

        // Told apart deliberately. Collapsing them would tell an admin their DNS was wrong during an
        // outage of ours, and would later let our own failure count against their grace period.
        var result = await VerifyAsync(client, orgId);
        Assert.Equal("resolver_unavailable", result.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("verifiedAt").ValueKind);
    }

    [Fact]
    public async Task One_name_we_could_not_read_is_not_reported_as_a_missing_record()
    {
        var (app, dns) = _sso.CreateApp();
        var (orgId, client, _, _) = await _sso.SaveAsync(app, "partial.test");

        // The apex lookup fails and the subdomain answers NXDOMAIN. The record may well be on the apex,
        // which is the name most orgs use, so calling this a missing record blames the org for a failure
        // of ours, and under the daily re-check it would count against their grace period.
        dns.Unreadable.Add("partial.test");

        var result = await VerifyAsync(client, orgId);
        Assert.Equal("resolver_unavailable", result.GetProperty("outcome").GetString());
        Assert.Equal(["partial.test", "_condux-challenge.partial.test"], dns.Queried);
    }

    [Fact]
    public async Task Another_orgs_provisional_claim_does_not_block_the_org_that_proves_the_domain()
    {
        var (app, dns) = _sso.CreateApp();
        var squatter = await _sso.SaveAsync(app, "contested.test");
        var owner = await _sso.SaveAsync(app, "contested.test");

        // Both saved. Under the old globally-unique key the first save took the domain outright and the
        // real owner could never configure SSO at all, which is the defect this replaces.
        dns.Publish(owner.RecordName, owner.RecordValue);
        Assert.Equal("verified", (await VerifyAsync(owner.Client, owner.OrgId)).GetProperty("outcome").GetString());

        // The squatter published nothing, so its own claim still proves nothing and now cannot: the domain
        // is taken by the org that proved it.
        var refused = await squatter.Client.PostAsync(
            $"/api/orgs/{squatter.OrgId}/sso-config/verify", null);
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Equal("record_missing",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString());

        // And the login goes to the org that proved it, not the one that claimed it first.
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var start = await browser.GetAsync("/api/auth/sso/start?email=someone@contested.test");
        Assert.StartsWith("https://idp.example/authorize", start.Headers.Location!.ToString());
        Assert.Equal(owner.OrgId, await OwnerOfVerifiedClaimAsync("contested.test"));
    }

    [Fact]
    public async Task A_domain_another_org_has_proved_cannot_be_proved_twice()
    {
        var (app, dns) = _sso.CreateApp();
        var first = await _sso.SaveAsync(app, "taken.test");
        dns.Publish(first.RecordName, first.RecordValue);
        await VerifyAsync(first.Client, first.OrgId);

        // A second org saving the same domain is now refused outright, because somebody proved it.
        var second = app.CreateClient();
        await ApiAuth.SignUpAsync(second);
        var created = await second.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org" });
        var secondOrg = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, secondOrg, 2);

        var put = await second.PutAsJsonAsync($"/api/orgs/{secondOrg}/sso-config", ConfigBody("taken.test"));
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal("email_domain_taken",
            (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_second_org_whose_record_also_resolves_is_still_refused_the_domain()
    {
        var (app, dns) = _sso.CreateApp();
        var first = await _sso.SaveAsync(app, "race.test");
        var second = await _sso.SaveAsync(app, "race.test");
        dns.Publish(first.RecordName, first.RecordValue);
        await VerifyAsync(first.Client, first.OrgId);

        // The second org's own challenge now resolves too, so the DNS check passes and the refusal has to
        // come from the write. That is the partial unique index, not a read beforehand, which is what makes
        // two orgs verifying at the same instant unable to both win the domain.
        dns.Publish(second.RecordName, second.RecordValue);
        var resp = await second.Client.PostAsync($"/api/orgs/{second.OrgId}/sso-config/verify", null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("email_domain_taken",
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(first.OrgId, await OwnerOfVerifiedClaimAsync("race.test"));
    }

    [Fact]
    public async Task Claiming_a_different_domain_starts_the_proof_again()
    {
        var (app, dns) = _sso.CreateApp();
        var (orgId, client, recordName, recordValue) = await _sso.SaveAsync(app, "first.test");
        dns.Publish(recordName, recordValue);
        await VerifyAsync(client, orgId);

        // Moving the claim must not inherit the old proof, or an org could verify a domain it controls and
        // then point the same row at one it does not.
        var moved = await client.PutAsJsonAsync($"/api/orgs/{orgId}/sso-config", ConfigBody("second.test"));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var body = await moved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("verifiedAt").ValueKind);
        Assert.NotEqual(recordValue, body.GetProperty("verificationRecordValue").GetString());

        var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var start = await browser.GetAsync("/api/auth/sso/start?email=someone@second.test");
        Assert.Equal("/login?error=sso_not_available", start.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Re_saving_the_same_domain_keeps_the_proof_and_the_challenge()
    {
        var (app, dns) = _sso.CreateApp();
        var (orgId, client, recordName, recordValue) = await _sso.SaveAsync(app, "stable.test");
        dns.Publish(recordName, recordValue);
        await VerifyAsync(client, orgId);

        // Editing an IdP endpoint must not sign the org out of its own SSO, and must not invalidate a TXT
        // record the org has already published.
        var again = await client.PutAsJsonAsync($"/api/orgs/{orgId}/sso-config", ConfigBody("stable.test"));
        var body = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("verifiedAt").ValueKind);
        Assert.Equal(recordValue, body.GetProperty("verificationRecordValue").GetString());
    }

    [Fact]
    public async Task An_org_that_dropped_off_an_sso_tier_cannot_switch_the_routing_on()
    {
        var (app, dns) = _sso.CreateApp();
        var (orgId, client, recordName, recordValue) = await _sso.SaveAsync(app, "downgrade.test");
        dns.Publish(recordName, recordValue);

        // A config row outlives a downgrade, so verification carries the same plan gate the write does.
        // Without it an org off an SSO tier could newly turn on routing for a claim saved while entitled.
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 0); // Free has no Sso

        var resp = await client.PostAsync($"/api/orgs/{orgId}/sso-config/verify", null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("sso_requires_upgrade",
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // Back on a tier that includes SSO, the same click works, so the refusal is about the plan and
        // not about the record.
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);
        Assert.Equal("verified", (await VerifyAsync(client, orgId)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task An_admin_who_is_not_an_owner_cannot_verify()
    {
        var (app, dns) = _sso.CreateApp();
        var (orgId, client, recordName, recordValue) = await _sso.SaveAsync(app, "gate.test");
        dns.Publish(recordName, recordValue);

        // Verification is the switch that makes a claim route, so it is gated exactly like writing the
        // config: an admin who could flip it would hold the same authority by a second route.
        var admin = app.CreateClient();
        var adminUser = await ApiAuth.SignUpAsync(admin);
        await new OrgMemberRepository(pg.ConnectionString).AddAsync(orgId, adminUser.UserId, OrgRole.Admin);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PostAsync($"/api/orgs/{orgId}/sso-config/verify", null)).StatusCode);
        Assert.Equal("verified", (await VerifyAsync(client, orgId)).GetProperty("outcome").GetString());
    }

    /// <summary>A self-hosted deployment on an internal domain has no public DNS to publish into, so the
    /// check passes for the whole deployment. Environment-only and never a per-org field: a tenant that
    /// could switch off its own domain verification would have none.</summary>
    [Fact]
    public async Task A_deployment_that_opted_out_verifies_without_dns()
    {
        var (app, dns) = _sso.CreateApp(skipVerification: true);
        dns.Unreachable = true;
        var (orgId, client, _, _) = await _sso.SaveAsync(app, "internal.test");

        Assert.Equal("verified", (await VerifyAsync(client, orgId)).GetProperty("outcome").GetString());
        // Not a resolver that was reached and lied to: no lookup happened at all.
        Assert.Empty(dns.Queried);
    }

    private async Task<long?> OwnerOfVerifiedClaimAsync(string domain) =>
        (await new PostgresSsoConfigStore(pg.ConnectionString).GetVerifiedByEmailDomainAsync(domain))?.OrgId;
}
