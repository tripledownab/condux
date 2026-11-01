using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.Core.Plans;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// A ticket's body read from the issue's real stored event (ADR-0053): its environment and the frames of its
/// last exception come from ClickHouse, which the rest of the Linear suite runs without, so only this class
/// can tell a worker that reads the event from one that never does.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LinearTicketBodyTest(PostgresFixture pg, ClickHouseFixture ch)
    : IClassFixture<PostgresFixture>, IClassFixture<ClickHouseFixture>
{
    [Fact]
    public async Task The_ticket_carries_the_environment_and_where_of_the_issues_latest_event()
    {
        var linear = new LinearStub();
        var app = linear.CreateApp(pg.ConnectionString, ch);
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var org = await client.PostAsJsonAsync("/api/orgs", new { name = "Acme" });
        var orgId = (await org.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var project = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects", new { name = "Backend", platform = "node" });
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project").GetProperty("id").GetInt64();
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping("fp-body", "RangeError: boom", "pay.ts"), Level.Error, DateTimeOffset.UtcNow);

        using var http = new HttpClient();
        ClickHouseRegistration.Configure(http, ch.BaseUrl, ch.ChUser, ch.ChPassword);
        var stored = new Event
        {
            EventId = "body1",
            Level = Level.Error,
            Environment = "staging",
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Exceptions =
            [
                new ExceptionValue
                {
                    Type = "RangeError",
                    Value = "boom",
                    Stacktrace = new Stacktrace
                    {
                        Frames =
                        [
                            new Frame { Function = "main", Filename = "src/index.ts", Lineno = 3, InApp = true },
                            new Frame { Function = "charge", Filename = "src/pay.ts", Lineno = 42, InApp = true },
                        ],
                    },
                },
            ],
        };
        await new ClickHouseEventWriter(http).InsertAsync([ClickHouseEventWriter.ToRow(
            projectId.ToString(System.Globalization.CultureInfo.InvariantCulture), (ulong)issue.Id, stored, "fp-body",
            PlanCatalog.DefaultRetentionDays, DateTimeOffset.UtcNow)]);

        Assert.Equal("connected", await ConnectAsync(app, orgId));
        await client.PutAsJsonAsync($"/api/projects/{projectId}/linear/mapping", new { teamId = "team-1" });
        await client.PostAsync($"/api/projects/{projectId}/issues/{issue.PublicId}/ticket", null);
        await LinearStub.WorkerOf(app).RunOnceAsync(CancellationToken.None);

        var description = JsonDocument.Parse(linear.Requests.Single(r => r.Body.Contains("issueCreate")).Body)
            .RootElement.GetProperty("variables").GetProperty("input").GetProperty("description").GetString()!;
        Assert.Contains("**Environment:** staging", description);
        Assert.Contains("- charge \\(src\\/pay\\.ts\\:42\\)\n- main \\(src\\/index\\.ts\\:3\\)", description);
    }

    private static async Task<string?> ConnectAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, long orgId)
    {
        var state = Condux.Core.Auth.ConnectState.Create(Condux.Core.Auth.ConnectState.Linear, orgId, "/", false,
            DateTimeOffset.UtcNow.AddMinutes(10), LinearStub.ClientSecret);
        var browser = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/linear/oauth/callback?code=c&state={Uri.EscapeDataString(state)}");
        request.Headers.Add("Cookie", $"condux_linear_state={state}");
        var response = await browser.SendAsync(request);
        return System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query)["linear"];
    }
}
