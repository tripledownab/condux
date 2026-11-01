using System.Net;
using System.Text;
using System.Text.Json;
using Condux.ControlPlane.Tickets;
using Condux.Linear;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// A stand in Linear for a control plane test host: the token, revoke and GraphQL endpoints, routed by path
/// and by which operation the query names, with each answer settable and every request recorded. It also
/// takes the hosted ticket worker out of the host, so a test runs a pass exactly when it means to rather
/// than racing one that started on its own.
/// </summary>
internal sealed class LinearStub : HttpMessageHandler
{
    public const string ClientSecret = "linear-client-secret";
    public const string RedirectUri = "https://app.condux.test/api/linear/oauth/callback";

    /// <summary>The workspace id the organization query answers with; null answers a JSON null.</summary>
    public string? WorkspaceId { get; set; } = "ws-1";
    public string Teams { get; set; } = """[{"id":"team-1","key":"ENG","name":"Engineering"}]""";
    /// <summary>Answer every label page as if more followed, so the client stops at its page ceiling.</summary>
    public bool LabelsNeverEnd { get; set; }

    public string Labels { get; set; } = """[{"id":"label-bug","name":"Bug","color":"#f00"},{"id":"label-be","name":"Backend","color":"#0f0"}]""";
    public HttpStatusCode CreateStatus { get; set; } = HttpStatusCode.OK;
    public string? CreateBody { get; set; }
    public HttpStatusCode RevokeStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>Ticket ids Linear answers 429 for, so a test can rate limit one org's ticket and not another's.</summary>
    public HashSet<string> RateLimitedIds { get; } = [];

    public List<(string Path, string Body)> Requests { get; } = [];

    public int Creates => Requests.Count(r => r.Path == "/graphql" && r.Body.Contains("issueCreate"));

    public WebApplicationFactory<Program> CreateApp(string postgres, ClickHouseFixture? clickHouse = null) =>
        ControlPlaneApp.Create(postgres, clickHouse, appBaseUrl: "https://app.condux.test").WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_LINEAR_CLIENT_ID", "linear-client-id");
            b.UseSetting("CONDUX_LINEAR_CLIENT_SECRET", ClientSecret);
            b.UseSetting("CONDUX_LINEAR_OAUTH_REDIRECT_URI", RedirectUri);
            b.ConfigureTestServices(s =>
            {
                var options = new LinearOptions("linear-client-id", ClientSecret, RedirectUri)
                {
                    AuthorizeEndpoint = "https://linear.test/oauth/authorize",
                    ApiBaseUrl = "https://api.linear.test",
                };
                var http = new HttpClient(this);
                s.AddSingleton(new LinearOAuth(http, options, () => DateTimeOffset.UtcNow));
                s.AddSingleton(new LinearApi(http, options));
                s.Remove(s.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(TicketWorker)));
            });
        });

    /// <summary>A worker over the host's own services, for a test to run passes on.</summary>
    public static TicketWorker WorkerOf(WebApplicationFactory<Program> app) =>
        ActivatorUtilities.CreateInstance<TicketWorker>(app.Services);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Requests)
        {
            Requests.Add((path, body));
        }
        return path switch
        {
            "/oauth/token" => Json(HttpStatusCode.OK,
                $$"""{"access_token":"at-{{Guid.NewGuid():N}}","refresh_token":"rt-{{Guid.NewGuid():N}}","expires_in":86399}"""),
            "/oauth/revoke" => Json(RevokeStatus, ""),
            // Create first: its input names a team, so a match on "teams" must not be what catches it.
            "/graphql" when body.Contains("issueCreate") && RateLimitedIds.Any(body.Contains) =>
                Json(HttpStatusCode.TooManyRequests, "{}"),
            "/graphql" when body.Contains("issueCreate") => Json(CreateStatus, CreateBody ?? CreatedFor(body)),
            "/graphql" when body.Contains("issueLabels") => Json(HttpStatusCode.OK,
                """{"data":{"issueLabels":{"nodes":""" + Labels + (LabelsNeverEnd
                    ? ""","pageInfo":{"hasNextPage":true,"endCursor":"more"}}}}"""
                    : ""","pageInfo":{"hasNextPage":false,"endCursor":null}}}}""")),
            "/graphql" when body.Contains("organization") => Json(HttpStatusCode.OK,
                """{"data":{"organization":{"id":""" + (WorkspaceId is null ? "null" : Quote(WorkspaceId)) + ""","name":"Acme"}}}"""),
            "/graphql" when body.Contains("teams") => Json(HttpStatusCode.OK,
                """{"data":{"teams":{"nodes":""" + Teams + ""","pageInfo":{"hasNextPage":false,"endCursor":null}}}}"""),
            _ => Json(HttpStatusCode.NotFound, "{}"),
        };
    }

    // Echo the id we were sent into the identifier, so a test can see which request made the ticket.
    private static string CreatedFor(string body)
    {
        var id = JsonDocument.Parse(body).RootElement.GetProperty("variables").GetProperty("input").GetProperty("id").GetString();
        return """{"data":{"issueCreate":{"success":true,"issue":{"identifier":""" + Quote($"ENG-{id![..4]}")
            + ""","url":""" + Quote($"https://linear.test/acme/issue/{id}") + "}}}}";
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
