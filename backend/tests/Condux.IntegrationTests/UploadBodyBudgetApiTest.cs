using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Condux.ControlPlane.Uploads;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Condux.IntegrationTests.Fixtures.ReleaseTokenUploads;
using static Condux.Tests.Shared.ElfFiles;

namespace Condux.IntegrationTests;

/// <summary>
/// The CI uploads' shared body budget over HTTP: while it is taken, source maps, debug files and obfuscation
/// maps alike are turned away with 429 and a Retry-After, and every upload gives back what its body took,
/// whatever it was answered. The test client sends nothing until a body ends and offers no minimum data rate,
/// so the rate the reader sets is covered by UploadBodiesTests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UploadBodyBudgetApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string BuildId = "7a3ef9306827da183fa36f07eecef2fb";
    private const string DebugId = "30f93e7a-2768-18da-3fa3-6f07eecef2fb";
    private const string SourceMap = "{\"version\":3,\"file\":\"app.js\",\"sources\":[\"app.ts\"],\"mappings\":\"AAAA\"}";

    [Fact]
    public async Task Every_upload_route_is_turned_away_while_the_budget_is_taken()
    {
        var (app, ci) = await ConnectAsync();
        var budget = app.Services.GetRequiredService<UploadBodyBudget>();

        Assert.True(budget.TryReserve(UploadBodyBudget.Bytes));
        HttpResponseMessage[] turnedAway =
        [
            await ci.PostAsync("/api/sourcemaps?release=1.0.0&filename=app.js", Json(SourceMap)),
            await ci.PostAsync("/api/debug-files", new ByteArrayContent(SymbolsFile(BuildId))),
            await ci.PostAsync($"/api/dart-symbol-maps?debugId={DebugId}", Json("[\"Cart\",\"xY\"]")),
        ];
        budget.Release(UploadBodyBudget.Bytes);
        var stored = await ci.PostAsync("/api/debug-files", new ByteArrayContent(SymbolsFile(BuildId)));

        Assert.All(turnedAway, response =>
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.NotNull(response.Headers.RetryAfter);
        });
        Assert.Equal(HttpStatusCode.Created, stored.StatusCode);
    }

    [Fact]
    public async Task Every_upload_gives_back_what_its_body_took_whatever_it_was_answered()
    {
        var (app, ci) = await ConnectAsync();
        var budget = app.Services.GetRequiredService<UploadBodyBudget>();

        var file = await ci.PostAsync("/api/debug-files", new ByteArrayContent(SymbolsFile(BuildId)));
        var debugId = (await file.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("debugId").GetString();
        var answers = new[]
        {
            file.StatusCode,
            (await ci.PostAsync("/api/debug-files", new ByteArrayContent([1, 2, 3]))).StatusCode,
            (await ci.PostAsync($"/api/dart-symbol-maps?debugId={debugId}", Json("[\"Cart\",\"xY\"]"))).StatusCode,
            (await ci.PostAsync($"/api/dart-symbol-maps?debugId={debugId}", Json("not a map"))).StatusCode,
            (await ci.PostAsync("/api/sourcemaps?release=1.0.0&filename=app.js", Json(SourceMap))).StatusCode,
            (await ci.PostAsync("/api/sourcemaps?release=1.0.0&filename=app.js", Json(new string(' ', 25 * 1024 * 1024 + 1))))
                .StatusCode,
        };

        Assert.Equal(
        [
            HttpStatusCode.Created, HttpStatusCode.BadRequest, HttpStatusCode.Created, HttpStatusCode.BadRequest,
            HttpStatusCode.Created, HttpStatusCode.RequestEntityTooLarge,
        ], answers);
        Assert.True(budget.TryReserve(UploadBodyBudget.Bytes), "an upload kept part of the budget");
        Assert.False(budget.TryReserve(1), "an upload gave back more than it took");
    }

    private async Task<(WebApplicationFactory<Program> App, HttpClient Ci)> ConnectAsync()
    {
        var app = AppWithStore(pg.ConnectionString, new RecordingObjectStore());
        var (admin, project) = await ProvisionAsync(app, pg.ConnectionString);
        return (app, Bearer(app, await MintTokenAsync(admin, project)));
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
