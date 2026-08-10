using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The alert-rules management API (#59): create a rule, attach a channel, list rules with their
/// channels, edit the rule + channel, then disable and delete — all tenant-scoped to the project.
/// Validation rejects an out-of-range severity or an empty event set.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AlertApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private async Task<(HttpClient Client, long ProjectId)> ProvisionAsync()
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme", tier = 2 });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "backend", name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        return (client, projectId);
    }

    [Fact]
    public async Task CreateRule_AddChannel_List_ThenDisableAndDelete()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();

        var createResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/alert-rules",
            new { name = "prod errors", events = new[] { 1 }, levels = new[] { 4, 5 } });
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var ruleId = (await createResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var channelResp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/alert-rules/{ruleId}/channels",
            new { channel = 2, target = "https://hooks.slack.test/x" });
        Assert.Equal(HttpStatusCode.Created, channelResp.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/alert-rules");
        Assert.Equal(1, list.GetArrayLength());
        var rule = list[0];
        Assert.Equal("prod errors", rule.GetProperty("name").GetString());
        Assert.Equal(new[] { 4, 5 }, rule.GetProperty("levels").EnumerateArray().Select(l => l.GetInt32()).ToArray());
        Assert.True(rule.GetProperty("enabled").GetBoolean());
        var channels = rule.GetProperty("channels");
        Assert.Equal(1, channels.GetArrayLength());
        Assert.Equal("https://hooks.slack.test/x", channels[0].GetProperty("target").GetString());

        // Disable via the full update (the toggle sends the whole rule state).
        var patch = await client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/alert-rules/{ruleId}",
            new { name = "prod errors", events = new[] { 1 }, levels = new[] { 4, 5 }, enabled = false });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        var del = await client.DeleteAsync($"/api/projects/{projectId}/alert-rules/{ruleId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/alert-rules");
        Assert.Equal(0, after.GetArrayLength());
    }

    [Fact]
    public async Task UpdateRule_And_Channel_Persist()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();

        var createResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/alert-rules",
            new { name = "prod errors", events = new[] { 1 }, levels = new[] { 4, 5 } });
        var ruleId = (await createResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var channelResp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/alert-rules/{ruleId}/channels",
            new { channel = 2, target = "https://hooks.slack.test/x" });
        var channelId = (await channelResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();

        // Edit the rule: rename, raise severity to Fatal (5), and also fire on regressions.
        var editRule = await client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/alert-rules/{ruleId}",
            new { name = "fatals only", events = new[] { 1, 2 }, levels = new[] { 5 }, enabled = true });
        Assert.Equal(HttpStatusCode.NoContent, editRule.StatusCode);

        // Edit the channel: switch it to a webhook with a new target.
        var editChannel = await client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/alert-rules/{ruleId}/channels/{channelId}",
            new { channel = 3, target = "https://webhook.test/y" });
        Assert.Equal(HttpStatusCode.NoContent, editChannel.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/alert-rules");
        var rule = list[0];
        Assert.Equal("fatals only", rule.GetProperty("name").GetString());
        Assert.Equal(new[] { 5 }, rule.GetProperty("levels").EnumerateArray().Select(l => l.GetInt32()).ToArray());
        Assert.Equal(new[] { 1, 2 }, rule.GetProperty("events").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        var channel = rule.GetProperty("channels")[0];
        Assert.Equal(3, channel.GetProperty("channel").GetInt32());
        Assert.Equal("https://webhook.test/y", channel.GetProperty("target").GetString());
    }

    [Fact]
    public async Task UpdateRule_InvalidLevel_Returns400()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();
        var createResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/alert-rules",
            new { name = "prod errors", events = new[] { 1 }, levels = new[] { 4, 5 } });
        var ruleId = (await createResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var resp = await client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/alert-rules/{ruleId}",
            new { name = "prod errors", events = new[] { 1 }, levels = new[] { 99 }, enabled = true });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreateRule_InvalidLevel_Returns400()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/alert-rules",
            new { name = "bad", events = new[] { 1 }, levels = Array.Empty<int>() });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreateRule_NoEvents_Returns400()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/alert-rules",
            new { name = "bad", events = Array.Empty<int>(), levels = new[] { 4 } });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task TestChannel_UnconfiguredEmail_ReportsNotConfigured_MissingIs404()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync();

        var createResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/alert-rules",
            new { name = "prod errors", events = new[] { 1 }, levels = new[] { 4, 5 } });
        var ruleId = (await createResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        // Email (channel = 1) has no notifier without CONDUX_SMTP_HOST, so the test send stays network-free.
        var channelResp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/alert-rules/{ruleId}/channels",
            new { channel = 1, target = "ops@acme.test" });
        var channelId = (await channelResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();

        var test = await client.PostAsync(
            $"/api/projects/{projectId}/alert-rules/{ruleId}/channels/{channelId}/test", null);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var body = await test.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("delivered").GetBoolean());
        Assert.Equal("channel_not_configured", body.GetProperty("error").GetString());

        // A missing channel under a real rule 404s.
        var missing = await client.PostAsync(
            $"/api/projects/{projectId}/alert-rules/{ruleId}/channels/{Guid.NewGuid()}/test", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
