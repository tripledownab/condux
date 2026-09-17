using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.ControlPlane.Auth;
using Condux.Core.Alerting;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using static Condux.IntegrationTests.Fixtures.SsoDomainClaim;

namespace Condux.IntegrationTests;

/// <summary>
/// The daily re-check that takes a proved SSO domain claim away again (ADR-0043 slice 2). A claim true in
/// March should not still route in December.
///
/// Both directions are covered on purpose. A re-check that lapsed everything would satisfy a test that
/// only watched the lapse, and one that lapsed nothing would satisfy a test that only watched the grace
/// period. The two failure modes cost opposite things: the first takes an enterprise's sign-in away over
/// our own DNS blip, the second leaves a domain routing to an org that no longer holds it.
///
/// A pass is run by hand with an explicit instant rather than by waiting for the timer, which is what
/// makes a seven-day grace period testable in milliseconds.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SsoVerificationRecheckTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private readonly SsoDomainClaim _sso = new(pg.ConnectionString);

    private sealed class CapturingNotifier : INotifier
    {
        public List<(string Subject, string Body)> Sent { get; } = [];
        public NotificationChannel Channel => NotificationChannel.Webhook;

        public Task SendAsync(
            AlertChannel target, AlertNotification notification, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SendMessageAsync(string target, string subject, string body, CancellationToken ct = default)
        {
            Sent.Add((subject, body));
            return Task.CompletedTask;
        }
    }

    /// <summary>The registered worker, not a fresh one: resolving it from the app proves it is actually
    /// wired into the control-plane, which a hand-built instance would not.</summary>
    private static SsoVerificationWorker WorkerOf(WebApplicationFactory<Program> app) =>
        app.Services.GetServices<IHostedService>().OfType<SsoVerificationWorker>().Single();

    private static Task RunPassAsync(WebApplicationFactory<Program> app, DateTimeOffset now) =>
        WorkerOf(app).RunOnceAsync(now, CancellationToken.None);

    /// <summary>Publishes the record, proves the claim, and hands back the browser that will try to sign
    /// in with it. Every test below starts from a claim that genuinely routes.</summary>
    private async Task<(Claim Claim, HttpClient Browser)> ProvedClaimAsync(
        WebApplicationFactory<Program> app, StubDoh dns, string domain)
    {
        var claim = await _sso.SaveAsync(app, domain);
        dns.Publish(claim.RecordName, claim.RecordValue);
        Assert.Equal("verified", (await VerifyAsync(claim.Client, claim.OrgId)).GetProperty("outcome").GetString());
        dns.Queried.Clear();
        return (claim, app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));
    }

    private static async Task<bool> RoutesAsync(HttpClient browser, string domain)
    {
        var start = await browser.GetAsync($"/api/auth/sso/start?email=someone@{domain}");
        return start.Headers.Location!.ToString().StartsWith("https://idp.example/authorize");
    }

    private static async Task<JsonElement> StateAsync(Claim claim) =>
        await ReadConfigAsync(claim.Client, claim.OrgId);

    private static bool IsSet(JsonElement config, string property) =>
        config.GetProperty(property).ValueKind != JsonValueKind.Null;

    // A pass walks every due claim in the database, which is what it does in production too, and the tests
    // of one class share a database. So both of these name the domain under test rather than counting
    // everything: a global count would make each test depend on which ones ran before it.
    private static int LookupsFor(StubDoh dns, string domain) =>
        dns.Queried.Count(name => name == domain || name.EndsWith($".{domain}", StringComparison.Ordinal));

    private static List<(string Subject, string Body)> NoticesFor(CapturingNotifier notifier, string domain) =>
        notifier.Sent.Where(sent => sent.Subject.Contains(domain, StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task A_claim_whose_record_vanishes_keeps_routing_through_the_grace_period_then_stops()
    {
        var notifier = new CapturingNotifier();
        var (app, dns) = _sso.CreateApp(notifier: notifier);
        var (claim, browser) = await ProvedClaimAsync(app, dns, "vanish.test");
        await new OrgNotificationChannelRepository(pg.ConnectionString)
            .AddAsync(claim.OrgId, NotificationChannel.Webhook, "https://hooks.test/w");

        var start = DateTimeOffset.UtcNow;
        dns.Clear(); // the org withdrew the record, or lost the zone

        // First failing check. Routing must continue: most ways a published record stops resolving are not
        // the org's doing, and taking sign-in away on the first miss would make a registrar blip an outage.
        await RunPassAsync(app, start);
        Assert.True(await RoutesAsync(browser, "vanish.test"));
        var failing = await StateAsync(claim);
        Assert.True(IsSet(failing, "verifiedAt"));
        Assert.True(IsSet(failing, "verificationLostAt"));
        // The deadline is served, not computed in the browser, so the dashboard and the email agree.
        Assert.True(IsSet(failing, "verificationLapsesAt"));

        var warning = Assert.Single(NoticesFor(notifier, "vanish.test"));
        Assert.Contains("no password to fall back on", warning.Body);

        // Eight days on, past the grace period. Now it stops.
        await RunPassAsync(app, start.AddDays(8));
        Assert.False(await RoutesAsync(browser, "vanish.test"));
        var lapsed = await StateAsync(claim);
        Assert.False(IsSet(lapsed, "verifiedAt"));
        Assert.True(IsSet(lapsed, "verificationLostAt")); // kept: it is the only record of when
        Assert.False(IsSet(lapsed, "verificationLapsesAt")); // no longer a deadline, it already happened

        var notices = NoticesFor(notifier, "vanish.test");
        Assert.Equal(2, notices.Count);
        Assert.Contains("has stopped", notices[1].Subject);

        // A third pass changes nothing and says nothing more. The transition is what sends the notice, so
        // a claim that stays broken cannot turn into a daily mail.
        await RunPassAsync(app, start.AddDays(9));
        Assert.Equal(2, NoticesFor(notifier, "vanish.test").Count);
    }

    [Fact]
    public async Task A_resolver_we_cannot_reach_never_counts_against_a_claim()
    {
        var (app, dns) = _sso.CreateApp();
        var (claim, browser) = await ProvedClaimAsync(app, dns, "outage.test");

        // Our failure, not theirs. If this started the grace period, a long enough outage of ours would
        // sign every enterprise customer out and tell each of them their DNS was wrong.
        dns.Unreachable = true;
        var start = DateTimeOffset.UtcNow;
        await RunPassAsync(app, start);
        await RunPassAsync(app, start.AddDays(2));
        await RunPassAsync(app, start.AddDays(9)); // well past the grace period

        Assert.True(await RoutesAsync(browser, "outage.test"));
        Assert.False(IsSet(await StateAsync(claim), "verificationLostAt"));
    }

    [Fact]
    public async Task A_record_that_comes_back_inside_the_grace_period_clears_the_failure()
    {
        var notifier = new CapturingNotifier();
        var (app, dns) = _sso.CreateApp(notifier: notifier);
        var (claim, browser) = await ProvedClaimAsync(app, dns, "restored.test");
        await new OrgNotificationChannelRepository(pg.ConnectionString)
            .AddAsync(claim.OrgId, NotificationChannel.Webhook, "https://hooks.test/w");

        var start = DateTimeOffset.UtcNow;
        dns.Clear();
        await RunPassAsync(app, start);
        Assert.True(IsSet(await StateAsync(claim), "verificationLostAt"));

        // Republished. The org clicks nothing: the next check picks it up, which is what the notice says.
        dns.Publish(claim.RecordName, claim.RecordValue);
        await RunPassAsync(app, start.AddDays(2));

        Assert.False(IsSet(await StateAsync(claim), "verificationLostAt"));
        Assert.True(await RoutesAsync(browser, "restored.test"));

        // And it does not lapse later off the stale failure, which is the point of clearing it.
        await RunPassAsync(app, start.AddDays(10));
        Assert.True(await RoutesAsync(browser, "restored.test"));
        // Only the original warning: no recovery mail, which would be noise, and no lapse mail, which
        // would be wrong.
        Assert.Single(NoticesFor(notifier, "restored.test"));
    }

    [Fact]
    public async Task A_lapsed_domain_is_released_for_the_org_that_can_prove_it()
    {
        var (app, dns) = _sso.CreateApp();
        var held = await _sso.SaveAsync(app, "handover.test");
        // The second org's claim is saved first, because once somebody has PROVED a domain a later save of
        // it is refused. That is the state the ADR designed for: several provisional claims, one proof.
        var successor = await _sso.SaveAsync(app, "handover.test");
        // Both records resolve throughout, so the only thing that changes below is who holds the proof.
        // Publishing the successor's up front is what lets the same click be made before and after.
        dns.Publish(held.RecordName, held.RecordValue);
        dns.Publish(successor.RecordName, successor.RecordValue);
        await VerifyAsync(held.Client, held.OrgId);

        // While the first org holds it, the successor's own record resolving is not enough: the partial
        // unique index refuses the write even though the DNS check passed.
        var refused = await successor.Client.PostAsync(
            $"/api/orgs/{successor.OrgId}/sso-config/verify", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("email_domain_taken",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // The first org's record goes and its claim lapses.
        var start = DateTimeOffset.UtcNow;
        dns.Withdraw(held.RecordName, held.RecordValue);
        await RunPassAsync(app, start);
        await RunPassAsync(app, start.AddDays(8));
        Assert.Null(await OwnerOfVerifiedClaimAsync("handover.test"));

        // The identical click now succeeds. That is the whole point: a domain really does change hands,
        // and the org that holds it is no longer locked out by the one that used to.
        Assert.Equal("verified",
            (await VerifyAsync(successor.Client, successor.OrgId)).GetProperty("outcome").GetString());
        Assert.Equal(successor.OrgId, await OwnerOfVerifiedClaimAsync("handover.test"));
        Assert.NotEqual(held.OrgId, successor.OrgId);
    }

    [Fact]
    public async Task A_provisional_claim_is_never_re_checked()
    {
        var (app, dns) = _sso.CreateApp();
        var claim = await _sso.SaveAsync(app, "provisional.test");

        // It routes nothing, so re-reading its record changes no outcome and would spend a lookup on every
        // squatter's claim for ever. The org's own Verify button is what promotes it.
        await RunPassAsync(app, DateTimeOffset.UtcNow);

        Assert.Equal(0, LookupsFor(dns, "provisional.test"));
        Assert.False(IsSet(await StateAsync(claim), "verificationLostAt"));
    }

    [Fact]
    public async Task A_claim_is_re_checked_at_most_once_a_day()
    {
        var (app, dns) = _sso.CreateApp();
        var (claim, _) = await ProvedClaimAsync(app, dns, "paced.test");

        var start = DateTimeOffset.UtcNow;
        await RunPassAsync(app, start);
        Assert.Equal(1, LookupsFor(dns, "paced.test")); // the apex answers, so the subdomain is not asked

        // The pass stamps the row as it claims it, so an hourly timer re-reads DNS daily rather than
        // hourly, and several replicas running this loop cannot each check the same domain.
        dns.Queried.Clear();
        await RunPassAsync(app, start.AddHours(1));
        Assert.Equal(0, LookupsFor(dns, "paced.test"));

        await RunPassAsync(app, start.AddHours(25));
        Assert.Equal(1, LookupsFor(dns, "paced.test"));
        Assert.True(IsSet(await StateAsync(claim), "verifiedAt"));
    }

    [Fact]
    public async Task A_deployment_that_opted_out_keeps_its_claims_and_makes_no_lookup()
    {
        var (app, dns) = _sso.CreateApp(skipVerification: true);
        var claim = await _sso.SaveAsync(app, "internal.test");
        await VerifyAsync(claim.Client, claim.OrgId);
        dns.Queried.Clear();

        // The opt-out exists for a single-tenant install on an internal domain with no public DNS. If this
        // pass reached the resolver, that installation's SSO would lapse a week after upgrading.
        var start = DateTimeOffset.UtcNow;
        await RunPassAsync(app, start);
        await RunPassAsync(app, start.AddDays(9));

        Assert.Equal(0, LookupsFor(dns, "internal.test"));
        Assert.False(IsSet(await StateAsync(claim), "verificationLostAt"));
        Assert.True(IsSet(await StateAsync(claim), "verifiedAt"));
    }

    private async Task<long?> OwnerOfVerifiedClaimAsync(string domain) =>
        (await new PostgresSsoConfigStore(pg.ConnectionString).GetVerifiedByEmailDomainAsync(domain))?.OrgId;
}
