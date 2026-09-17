using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Condux.Core.Alerting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>Shared setup for the SSO domain-verification tests (ADR-0043): a stubbed DNS-over-HTTPS
/// resolver, an app wired to it, and a fresh org holding a provisional claim. Shared because the on-demand
/// Verify button and the daily re-check are two halves of one mechanism, and a second copy of this setup
/// would be free to drift from the half it was copied from.</summary>
internal sealed class SsoDomainClaim(string connectionString)
{
    private static readonly string SecretKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// A DNS-over-HTTPS resolver that answers with whatever the test published, in Cloudflare's quoted
    /// form. Records the names it was asked for, since which names are queried is part of the contract.
    ///
    /// A name holds a LIST of values, because that is what a real zone does and this ADR depends on it:
    /// two orgs claiming one domain both publish a challenge on the same apex, and a store of one value
    /// per name would let the second silently replace the first. A test written against that would not
    /// fail, it would verify the wrong org.
    /// </summary>
    public sealed class StubDoh : HttpMessageHandler
    {
        private readonly Dictionary<string, List<string>> _zone = [];

        public bool Unreachable { get; set; }
        public HashSet<string> Unreadable { get; } = [];
        public List<string> Queried { get; } = [];

        /// <summary>Adds a TXT value to a name, alongside anything already there.</summary>
        public void Publish(string name, string value)
        {
            if (!_zone.TryGetValue(name, out var values))
            {
                _zone[name] = values = [];
            }
            values.Add(value);
        }

        /// <summary>Removes one value, leaving any others on that name. Withdrawing one org's challenge
        /// must not take another org's down with it.</summary>
        public void Withdraw(string name, string value)
        {
            if (_zone.TryGetValue(name, out var values))
            {
                values.Remove(value);
            }
        }

        /// <summary>Empties the zone, for a test whose org lost its records altogether.</summary>
        public void Clear() => _zone.Clear();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var name = SsoFlow.QueryParam(request.RequestUri!, "name");
            Queried.Add(name);
            if (Unreachable || Unreadable.Contains(name))
            {
                throw new HttpRequestException("no resolver");
            }

            // Status 3 is NXDOMAIN with no Answer key at all, which is what a name with no records really
            // returns; a stub that always sent an empty array would not exercise the missing-key path.
            var body = _zone.TryGetValue(name, out var values) && values.Count > 0
                ? $$"""{"Status":0,"Answer":[{{Answers(name, values)}}]}"""
                : """{"Status":3}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/dns-json"),
            });
        }

        private static string Answers(string name, IEnumerable<string> values) =>
            string.Join(",", values.Select(v =>
                $$"""{"name":"{{name}}","type":16,"data":"\"{{v}}\""}"""));
    }

    /// <summary>An org's provisional claim: who it belongs to, a signed-in client for it, and the record
    /// it would have to publish.</summary>
    public readonly record struct Claim(long OrgId, HttpClient Client, string RecordName, string RecordValue);

    /// <summary>An app with SSO switched on and its resolver stubbed. <paramref name="skipVerification"/>
    /// is the self-hosted opt-out, which is environment-only, so a test can only get at it here.
    /// <paramref name="notifier"/> REPLACES the registered notifiers rather than joining them, because the
    /// dispatcher keys them by channel and a second webhook notifier would throw on the duplicate.</summary>
    public (WebApplicationFactory<Program> App, StubDoh Dns) CreateApp(
        bool skipVerification = false, INotifier? notifier = null)
    {
        var dns = new StubDoh();
        var app = ControlPlaneApp.Create(connectionString).WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_SECRET_KEY", SecretKey);
            if (skipVerification)
            {
                b.UseSetting("CONDUX_SSO_SKIP_DOMAIN_VERIFICATION", "1");
            }
            // AddHttpClient<T> names the client after the type, so the resolver's transport is replaceable
            // without the internal type being visible to the test.
            b.ConfigureTestServices(s =>
            {
                s.AddHttpClient("DohTxtResolver").ConfigurePrimaryHttpMessageHandler(() => dns);
                if (notifier is not null)
                {
                    s.RemoveAll<INotifier>();
                    s.AddSingleton(notifier);
                }
            });
        });
        return (app, dns);
    }

    public static object ConfigBody(string domain) => new
    {
        emailDomain = domain,
        issuer = "https://idp.example/",
        authorizationEndpoint = "https://idp.example/authorize",
        tokenEndpoint = "https://idp.example/token",
        clientId = "client-abc",
        clientSecret = "super-secret-value-xyz",
    };

    /// <summary>Signs up a fresh owner, gives their org a tier with SSO, and saves a provisional claim.</summary>
    public async Task<Claim> SaveAsync(WebApplicationFactory<Program> app, string domain)
    {
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var created = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org" });
        var orgId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(connectionString, orgId, 2); // Business has Sso

        var put = await client.PutAsJsonAsync($"/api/orgs/{orgId}/sso-config", ConfigBody(domain));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        return new Claim(orgId, client,
            body.GetProperty("verificationRecordName").GetString()!,
            body.GetProperty("verificationRecordValue").GetString()!);
    }

    /// <summary>Clicks Verify and returns the outcome body. Asserts 200, because every answer this route
    /// gives about DNS is a 200; a non-200 is a different failure and should not be read as an outcome.</summary>
    public static async Task<JsonElement> VerifyAsync(HttpClient client, long orgId)
    {
        var resp = await client.PostAsync($"/api/orgs/{orgId}/sso-config/verify", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The org's stored config as the dashboard reads it.</summary>
    public static Task<JsonElement> ReadConfigAsync(HttpClient client, long orgId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/sso-config");
}
